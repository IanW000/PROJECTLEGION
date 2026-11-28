using System;
using System.Diagnostics;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;

namespace ProjectLegion.Gameplay
{
    internal enum HierarchyFallback : byte { None, FullAStar, BudgetExceeded, UnsupportedWeights }

    internal struct NavigationHierarchyResult
    {
        internal NavigationHierarchyStatus Status;
        internal HierarchyFallback Fallback;
        internal double Cost, AbstractCost, FallbackMilliseconds;
        internal int PathCount, AbstractCount, ConnectExpanded, HighExpanded, RefineExpanded, FallbackExpanded;
    }

    internal sealed class NavigationHierarchyPathfinder : IDisposable
    {
        private NativeArray<double> _emptyOverrides, _localCosts, _startCosts, _goalCosts, _highCosts;
        private NativeArray<int> _localParents, _localPositions, _highParents, _highPositions, _abstractPath, _path;
        private NativeArray<uint> _stamps;
        private NativeArray<NavigationHeapEntry> _localHeap, _highHeap;
        private NativeArray<NavigationHierarchyResult> _result;
        private NavigationPathfinder _fallback;
        private NavigationHierarchyGraph _hierarchy;
        private NavigationGrid _grid;
        private NavigationHierarchyGraph _publishedHierarchy;
        private NavigationGrid _publishedGrid;
        private JobHandle _job;
        private uint _generation;
        private bool _pending, _asynchronous, _allowFallback, _overrideCosts, _fallbackPending, _published;
        private long _fallbackStart;
        private int2 _start, _goal;
        private int _fallbackNodes;
        private readonly long _budget;
        private long _bytes;
        internal bool Busy => _pending;
        internal bool IsCompleted => Busy && _job.IsCompleted;
        internal int OutputCapacity => _path.IsCreated ? _path.Length : 0;
        internal long PayloadBytes => _bytes + (_fallback?.PayloadBytes ?? 0);

        internal NavigationHierarchyPathfinder(NavigationHierarchyGraph hierarchy, int outputCapacity, long budget)
        {
            if (outputCapacity < 1 || budget < 1) throw new ArgumentOutOfRangeException();
            _budget = budget;
            int local = hierarchy.Partition.MaxNodes;
            int high = checked(hierarchy.Stats.Portals + 2);
            int endpoints = hierarchy.Stats.MaxPortals;
            long searchBytes = sizeof(double) + 2 * sizeof(int) + UnsafeUtility.SizeOf<NavigationHeapEntry>();
            long required = (long)local * searchBytes + (long)high * (searchBytes + sizeof(uint) + sizeof(int)) +
                (2L * endpoints * sizeof(double)) + (long)outputCapacity * sizeof(int) +
                UnsafeUtility.SizeOf<NavigationHierarchyResult>();
            if (required > budget) throw new ArgumentException("Query workspace exceeds its memory budget.", nameof(budget));
            try
            {
                _emptyOverrides = Alloc<double>(0);
                _localCosts = Alloc<double>(local);
                _localParents = Alloc<int>(local);
                _localPositions = Alloc<int>(local);
                _localHeap = Alloc<NavigationHeapEntry>(local);
                _startCosts = Alloc<double>(endpoints);
                _goalCosts = Alloc<double>(endpoints);
                _highCosts = Alloc<double>(high);
                _highParents = Alloc<int>(high);
                _highPositions = Alloc<int>(high);
                _highHeap = Alloc<NavigationHeapEntry>(high);
                _abstractPath = Alloc<int>(high);
                _stamps = new NativeArray<uint>(high, Allocator.Persistent);
                _bytes += (long)high * sizeof(uint);
                _path = Alloc<int>(outputCapacity);
                _result = Alloc<NavigationHierarchyResult>(1);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal bool TrySetOutputCapacity(int capacity)
        {
            if (Busy || !_path.IsCreated || capacity < 1) return false;
            if (_path.Length == capacity) return true;
            if (PayloadBytes + (long)capacity * sizeof(int) > _budget) return false;
            // Check the replacement peak and preserve the old buffer on allocation failure.
            var replacement = Alloc<int>(capacity);
            Release(ref _path);
            _path = replacement;
            _published = false;
            _publishedHierarchy = null;
            _publishedGrid = null;
            return true;
        }

        // The caller may supply fixture-only edge costs. They must remain immutable until the query ends.
        internal bool Begin(NavigationGrid grid, NavigationHierarchyGraph hierarchy, int2 start, int2 goal,
            bool asynchronous, bool allowFallback = true, NativeArray<double> overrideCosts = default)
        {
            if (!_result.IsCreated) throw new ObjectDisposedException(nameof(NavigationHierarchyPathfinder));
            if (Busy) throw new InvalidOperationException("A query already owns this workspace.");
            _published = false;
            _publishedHierarchy = null;
            _publishedGrid = null;
            if (!hierarchy.TryGetView(grid, out var view)) return false;
            if (view.Portals.Length > _highCosts.Length - 2 || hierarchy.Partition.MaxNodes > _localCosts.Length ||
                hierarchy.Stats.MaxPortals > _startCosts.Length)
                throw new ArgumentException("Query workspace does not fit this hierarchy.", nameof(hierarchy));
            if (overrideCosts.IsCreated && (long)overrideCosts.Length != (long)grid.Size.x * grid.Size.y * 8)
                throw new ArgumentException("Explicit costs require eight slots per node.", nameof(overrideCosts));

            _grid = grid;
            _hierarchy = hierarchy;
            _start = start;
            _goal = goal;
            _asynchronous = asynchronous;
            _allowFallback = allowFallback && hierarchy.Mode == NavigationPortalMode.Representative;
            _overrideCosts = overrideCosts.IsCreated;
            _fallbackPending = false;
            uint generation = unchecked(++_generation);
            if (generation == 0) _generation = 1;
            var query = new QueryJob
            {
                Input = new HierarchyInput { Graph = grid.AsReadOnly(),
                    Overrides = (overrideCosts.IsCreated ? overrideCosts : _emptyOverrides).AsReadOnly() },
                View = view, Start = start, Goal = goal, Generation = generation,
                Local = new HierarchyLocalSearch { Cost = _localCosts, Parents = _localParents,
                    Positions = _localPositions, Entries = _localHeap },
                StartCosts = _startCosts, GoalCosts = _goalCosts, HighCosts = _highCosts,
                HighParents = _highParents, HighPositions = _highPositions, HighHeap = _highHeap,
                Stamps = _stamps, Abstract = _abstractPath, Path = _path, Output = _result
            };
            _pending = true;
            try
            {
                if (_asynchronous)
                {
                    _job = query.Schedule();
                    grid.RegisterReader(_job);
                    hierarchy.RegisterReader(_job);
                    JobHandle.ScheduleBatchedJobs();
                }
                else query.Run();
            }
            catch
            {
                _job.Complete();
                Reset();
                throw;
            }
            return true;
        }

        internal bool TryFinish(NavigationGrid current, NavigationHierarchyGraph currentHierarchy,
            out NavigationHierarchyResult result, long availableFallbackBytes = long.MaxValue)
        {
            result = default;
            if (!IsCompleted) return false;
            _job.Complete();
            if (!ReferenceEquals(currentHierarchy, _hierarchy) || !_hierarchy.TryGetView(current, out _))
            {
                result.Status = NavigationHierarchyStatus.Invalidated;
                Reset();
                return true;
            }

            result = _result[0];
            if (_fallbackPending)
            {
                var fallback = _fallback.ReadResult();
                result.Fallback = HierarchyFallback.FullAStar;
                result.FallbackMilliseconds = (Stopwatch.GetTimestamp() - _fallbackStart) * 1000d / Stopwatch.Frequency;
                result.FallbackExpanded = fallback.Expanded;
                result.Status = fallback.Status == NavigationPathStatus.Success
                    ? (fallback.PathCount <= OutputCapacity ? NavigationHierarchyStatus.Success : NavigationHierarchyStatus.OutputCapacity)
                    : NavigationHierarchyStatus.Unreachable;
                result.Cost = result.Status == NavigationHierarchyStatus.Success ? fallback.Cost : 0;
                result.PathCount = result.Status == NavigationHierarchyStatus.Success ? fallback.PathCount : 0;
                result.AbstractCount = 0;
            }
            else if (_allowFallback && result.Status == NavigationHierarchyStatus.Unreachable)
            {
                if (_overrideCosts) result.Fallback = HierarchyFallback.UnsupportedWeights;
                else if (TryBeginFallback(availableFallbackBytes))
                    return TryFinish(current, currentHierarchy, out result, availableFallbackBytes);
                else result.Fallback = HierarchyFallback.BudgetExceeded;
            }
            _result[0] = result;
            _published = result.Status == NavigationHierarchyStatus.Success;
            _publishedHierarchy = _published ? _hierarchy : null;
            _publishedGrid = _published ? _grid : null;
            Reset();
            return true;
        }

        private bool TryBeginFallback(long availableBytes)
        {
            int nodes = _grid.Size.x * _grid.Size.y;
            if (_fallbackNodes != nodes)
            {
                long required = NavigationPathScheduler.WorkspaceSize(nodes);
                if (_bytes + required > _budget || required - (_fallback?.PayloadBytes ?? 0) > availableBytes)
                    return false;
                _fallback?.Dispose();
                _fallback = null;
                _fallbackNodes = 0;
                _fallback = new NavigationPathfinder(nodes);
                _fallbackNodes = nodes;
            }
            _fallbackStart = Stopwatch.GetTimestamp();
            _fallbackPending = true;
            if (_asynchronous)
            {
                _job = _fallback.Schedule(_grid, _start, _goal);
                JobHandle.ScheduleBatchedJobs();
            }
            else _fallback.Search(_grid, _start, _goal);
            return true;
        }

        internal int PathNode(int index)
        {
            if (!_published || !_publishedHierarchy.IsCurrent(_publishedGrid) || (uint)index >= (uint)_result[0].PathCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _result[0].Fallback == HierarchyFallback.FullAStar ? _fallback.PathNode(index) : _path[index];
        }

        internal int AbstractNode(int index)
        {
            if (!_published || !_publishedHierarchy.IsCurrent(_publishedGrid) || (uint)index >= (uint)_result[0].AbstractCount)
                throw new ArgumentOutOfRangeException(nameof(index));
            return _abstractPath[index];
        }

        private void Reset()
        {
            _pending = _fallbackPending = false;
            _hierarchy = null;
            _grid = null;
            _job = default;
        }

        private NativeArray<T> Alloc<T>(int count) where T : struct
        {
            var array = new NativeArray<T>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _bytes += (long)count * UnsafeUtility.SizeOf<T>();
            return array;
        }

        private void Release<T>(ref NativeArray<T> array) where T : struct
        {
            if (!array.IsCreated) return;
            _bytes -= (long)array.Length * UnsafeUtility.SizeOf<T>();
            array.Dispose();
            array = default;
        }

        public void Dispose()
        {
            if (Busy) _job.Complete();
            Reset();
            _published = false;
            _fallback?.Dispose();
            _fallback = null;
            Release(ref _emptyOverrides);
            Release(ref _localCosts);
            Release(ref _localParents);
            Release(ref _localPositions);
            Release(ref _localHeap);
            Release(ref _startCosts);
            Release(ref _goalCosts);
            Release(ref _highCosts);
            Release(ref _highParents);
            Release(ref _highPositions);
            Release(ref _highHeap);
            Release(ref _abstractPath);
            Release(ref _stamps);
            Release(ref _path);
            Release(ref _result);
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct QueryJob : IJob
        {
            private static readonly ProfilerMarker ConnectMarker = new ProfilerMarker("Navigation.HPA.Connect");
            private static readonly ProfilerMarker SearchMarker = new ProfilerMarker("Navigation.HPA.Abstract");
            private static readonly ProfilerMarker RefineMarker = new ProfilerMarker("Navigation.HPA.Refine");
            private static readonly ProfilerMarker StitchMarker = new ProfilerMarker("Navigation.HPA.Stitch");
            private static readonly ProfilerMarker ValidateMarker = new ProfilerMarker("Navigation.HPA.Validate");

            internal HierarchyInput Input;
            internal NavigationHierarchyView View;
            internal int2 Start, Goal;
            internal HierarchyLocalSearch Local;
            internal uint Generation;
            internal NativeArray<double> StartCosts, GoalCosts, HighCosts;
            internal NativeArray<int> HighParents, HighPositions, Abstract, Path;
            internal NativeArray<uint> Stamps;
            internal NativeArray<NavigationHeapEntry> HighHeap;
            internal NativeArray<NavigationHierarchyResult> Output;

            public void Execute() => Output[0] = Search();

            private NavigationHierarchyResult Search()
            {
                if (!Input.Graph.IsWalkable(Start))
                    return new NavigationHierarchyResult { Status = NavigationHierarchyStatus.InvalidStart };
                if (!Input.Graph.IsWalkable(Goal))
                    return new NavigationHierarchyResult { Status = NavigationHierarchyStatus.InvalidDestination };

                int start = Input.Graph.Index(Start), goal = Input.Graph.Index(Goal);
                if (start == goal)
                {
                    Path[0] = Abstract[0] = start;
                    return new NavigationHierarchyResult { PathCount = 1, AbstractCount = 1 };
                }

                if (Generation == 0)
                {
                    for (int i = 0; i < Stamps.Length; i++) Stamps[i] = 0;
                    Generation = 1;
                }
                int startCluster = View.Partition.Cluster(Start), goalCluster = View.Partition.Cluster(Goal);
                var result = new NavigationHierarchyResult { Status = NavigationHierarchyStatus.Unreachable };
                double direct = Connect(start, goal, startCluster, goalCluster, ref result.ConnectExpanded);
                if (!AbstractSearch(start, goal, startCluster, goalCluster, direct, ref result))
                    return result;

                result.Status = Refine(start, goal, ref result);
                if (result.Status == NavigationHierarchyStatus.Success && !Validate(start, goal, ref result))
                    result.Status = NavigationHierarchyStatus.InvalidRefinement;
                if (result.Status != NavigationHierarchyStatus.Success)
                {
                    result.AbstractCount = 0;
                    result.PathCount = 0;
                }
                return result;
            }

            private double Connect(int start, int goal, int startCluster, int goalCluster, ref int expanded)
            {
                using var span = ConnectMarker.Auto();
                var region = View.Partition.Region(startCluster);
                var portals = View.Clusters[startCluster];
                if (portals.PortalCount == 0)
                {
                    if (startCluster != goalCluster) return double.PositiveInfinity;
                    expanded += Local.Run(Input, region, start, goal);
                    return Local.Cost[region.Local(Goal)];
                }
                expanded += Local.Run(Input, region, start);
                for (int i = 0; i < portals.PortalCount; i++)
                    StartCosts[i] = Local.Cost[region.Local(Input.Graph.Cell(View.Portals[portals.PortalStart + i]))];
                double direct = startCluster == goalCluster ?
                    Local.Cost[region.Local(Goal)] : double.PositiveInfinity;

                region = View.Partition.Region(goalCluster);
                portals = View.Clusters[goalCluster];
                if (portals.PortalCount > 0)
                {
                    expanded += Local.Run(Input, region, goal, reverse: true);
                    for (int i = 0; i < portals.PortalCount; i++)
                        GoalCosts[i] = Local.Cost[region.Local(Input.Graph.Cell(View.Portals[portals.PortalStart + i]))];
                }
                return direct;
            }

            private bool AbstractSearch(int start, int goal, int startCluster, int goalCluster, double direct,
                ref NavigationHierarchyResult result)
            {
                using var span = SearchMarker.Auto();
                int virtualStart = View.Portals.Length, virtualGoal = virtualStart + 1;
                var source = View.Clusters[startCluster];
                var target = View.Clusters[goalCluster];
                var heap = new NavigationOpenHeap(HighHeap, HighPositions);
                Stamps[virtualStart] = Generation;
                HighCosts[virtualStart] = 0;
                HighParents[virtualStart] = -1;
                heap.Push(new NavigationHeapEntry(virtualStart, 0, Input.Heuristic(start, goal)));

                while (heap.Count > 0)
                {
                    int current = heap.Pop().Node;
                    if (current == virtualGoal)
                    {
                        result.AbstractCost = HighCosts[current];
                        for (int node = current; node >= 0; node = HighParents[node])
                            Abstract[result.AbstractCount++] = node == virtualGoal ? goal :
                                node == virtualStart ? start : View.Portals[node];
                        for (int i = 0, j = result.AbstractCount - 1; i < j; i++, j--)
                        {
                            int temp = Abstract[i]; Abstract[i] = Abstract[j]; Abstract[j] = temp;
                        }
                        return true;
                    }

                    result.HighExpanded++;
                    if (current == virtualStart)
                    {
                        Relax(ref heap, current, virtualGoal, goal, goal, direct);
                        for (int i = 0; i < source.PortalCount; i++)
                        {
                            int next = source.PortalStart + i;
                            Relax(ref heap, current, next, View.Portals[next], goal, StartCosts[i]);
                        }
                    }
                    else
                    {
                        if (current >= target.PortalStart && current - target.PortalStart < target.PortalCount)
                            Relax(ref heap, current, virtualGoal, goal, goal, GoalCosts[current - target.PortalStart]);
                        for (int edge = View.Offsets[current]; edge < View.Offsets[current + 1]; edge++)
                        {
                            var item = View.Edges[edge];
                            Relax(ref heap, current, item.To, View.Portals[item.To], goal, item.Cost);
                        }
                    }
                }
                return false;
            }

            private void Relax(ref NavigationOpenHeap heap, int current, int next, int nextNode, int goal, double edge)
            {
                if (!math.isfinite(edge)) return;
                double candidate = HighCosts[current] + edge;
                bool seen = Stamps[next] == Generation;
                if (seen && !(candidate < HighCosts[next])) return;
                var entry = new NavigationHeapEntry(next, candidate, Input.Heuristic(nextNode, goal));
                HighCosts[next] = candidate;
                HighParents[next] = current;
                Stamps[next] = Generation;
                if (seen && HighPositions[next] >= 0) heap.DecreaseKey(entry);
                else heap.Push(entry);
            }

            private NavigationHierarchyStatus Refine(int start, int goal, ref NavigationHierarchyResult result)
            {
                Path[0] = start;
                result.PathCount = 1;
                for (int i = 1; i < result.AbstractCount; i++)
                {
                    int from = Abstract[i - 1], to = Abstract[i];
                    if (from == to) continue;
                    int2 fromCell = Input.Graph.Cell(from), toCell = Input.Graph.Cell(to);
                    int fromCluster = View.Partition.Cluster(fromCell);
                    if (fromCluster != View.Partition.Cluster(toCell))
                    {
                        using var stitch = StitchMarker.Auto();
                        if (result.PathCount == Path.Length) return NavigationHierarchyStatus.OutputCapacity;
                        Path[result.PathCount++] = to;
                        continue;
                    }

                    var region = View.Partition.Region(fromCluster);
                    int localTarget = region.Local(toCell);
                    using (RefineMarker.Auto())
                        result.RefineExpanded += Local.Run(Input, region, from, to);
                    if (!math.isfinite(Local.Cost[localTarget])) return NavigationHierarchyStatus.InvalidRefinement;
                    using (StitchMarker.Auto())
                    {
                        int steps = 0;
                        for (int node = localTarget; Local.Parents[node] >= 0; node = Local.Parents[node])
                        {
                            if (steps == region.Count) return NavigationHierarchyStatus.InvalidRefinement;
                            Local.Positions[steps++] = region.Global(node);
                        }
                        if (steps > Path.Length - result.PathCount) return NavigationHierarchyStatus.OutputCapacity;
                        while (steps > 0) Path[result.PathCount++] = Local.Positions[--steps];
                    }
                }
                return Path[result.PathCount - 1] == goal ? NavigationHierarchyStatus.Success :
                    NavigationHierarchyStatus.InvalidRefinement;
            }

            private bool Validate(int start, int goal, ref NavigationHierarchyResult result)
            {
                using var span = ValidateMarker.Auto();
                if (Path[0] != start || Path[result.PathCount - 1] != goal) return false;
                double cost = 0;
                for (int i = 1; i < result.PathCount; i++)
                {
                    int from = Path[i - 1], to = Path[i];
                    int2 delta = Input.Graph.Cell(to) - Input.Graph.Cell(from);
                    int direction = 0;
                    while (direction < 8 && !NavigationGrid.Offset(direction).Equals(delta)) direction++;
                    if (direction == 8 || !Input.Forward(from, direction, out int target, out double edge) ||
                        target != to || !Input.Graph.IsWalkable(Input.Graph.Cell(to))) return false;
                    cost += edge;
                }
                result.Cost = cost;
                return math.abs(cost - result.AbstractCost) <= math.max(1, cost) * 1e-10;
            }
        }
    }
}
