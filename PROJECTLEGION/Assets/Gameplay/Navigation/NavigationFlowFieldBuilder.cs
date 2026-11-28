using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;

namespace ProjectLegion.Gameplay
{
    internal sealed class NavigationFlowFieldBuilder : IDisposable
    {
        private NativeArray<NavigationHeapEntry> _heap;
        private NativeArray<int> _positions;
        private NativeArray<double> _noOverrides;
        private NativeArray<NavigationFlowStats> _stats;
        private NavigationFlowField _pending;
        private NavigationGrid _source;
        private JobHandle _build;
        internal bool Busy => _pending != null;
        internal bool IsCompleted => Busy && _build.IsCompleted;
        internal long PayloadBytes => _heap.IsCreated ? WorkspaceBytes(_heap.Length) : 0;
        internal static long WorkspaceBytes(int nodes) => (long)nodes *
            (UnsafeUtility.SizeOf<NavigationHeapEntry>() + sizeof(int)) + UnsafeUtility.SizeOf<NavigationFlowStats>();

        internal NavigationFlowFieldBuilder(int capacity)
        {
            if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
            try
            {
                _heap = Allocate<NavigationHeapEntry>(capacity);
                _positions = Allocate<int>(capacity);
                _stats = Allocate<NavigationFlowStats>(1);
                _noOverrides = Allocate<double>(0);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static NativeArray<T> Allocate<T>(int count) where T : struct =>
            new NativeArray<T>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

        // Tests may supply per-outgoing-slot weights to exercise the production kernel on asymmetric graphs.
        // Missing edges are still determined exclusively by the grid's connectivity mask.
        internal NavigationFlowBuildStatus Begin(NavigationGrid grid, int2 goal, bool asynchronous,
            NativeArray<double> edgeCosts = default, JobHandle dependency = default)
        {
            if (!_heap.IsCreated) throw new ObjectDisposedException(nameof(NavigationFlowFieldBuilder));
            if (Busy) throw new InvalidOperationException("A build already owns this workspace.");
            if (!asynchronous && !dependency.Equals(default(JobHandle)))
                throw new ArgumentException("Dependencies require an asynchronous build.", nameof(dependency));
            if (grid == null || !grid.AcceptsQueries || !grid.TryGetNode(goal, out var node) || !node.Walkable)
                return NavigationFlowBuildStatus.InvalidGoal;
            int count = checked(grid.Size.x * grid.Size.y);
            if (_heap.Length < count) throw new ArgumentException("Insufficient workspace.", nameof(grid));
            if (edgeCosts.IsCreated && (long)edgeCosts.Length != (long)count * 8)
                throw new ArgumentException("Explicit costs require eight slots per node.", nameof(edgeCosts));

            _source = grid;
            _pending = new NavigationFlowField(grid, goal);
            var graph = new FlowGraph
            {
                Grid = grid.AsReadOnly(),
                Costs = (edgeCosts.IsCreated ? edgeCosts : _noOverrides).AsReadOnly()
            };
            var integration = new IntegrationJob
            {
                Graph = graph, Goal = graph.Grid.Index(goal), Cost = _pending.Integration,
                Entries = _heap, Positions = _positions
            };
            var directions = new DirectionJob
            {
                Graph = graph, Goal = integration.Goal, Cost = _pending.Integration, Output = _pending.Directions
            };
            var validate = new ValidateJob { Directions = _pending.Directions, Cost = _pending.Integration, Output = _stats };
            int chunks = (count - 1) / DirectionJob.ChunkSize + 1;

            if (asynchronous)
            {
                // Keep the latest scheduled dependency even if a subsequent schedule fails.
                try
                {
                    _build = integration.Schedule(dependency);
                    _build = directions.ScheduleParallel(chunks, 1, _build);
                    _build = validate.Schedule(_build);
                    grid.RegisterReader(_build);
                    JobHandle.ScheduleBatchedJobs();
                }
                catch
                {
                    _build.Complete();
                    Discard();
                    throw;
                }
            }
            else
            {
                integration.Run();
                directions.Run(chunks);
                validate.Run();
            }

            return NavigationFlowBuildStatus.Success;
        }

        internal bool TryFinish(NavigationGrid current, out NavigationFlowBuildStatus status, out NavigationFlowField field)
        {
            status = default;
            field = null;
            if (!Busy || !_build.IsCompleted)
                return false;
            _build.Complete();
            if (!ReferenceEquals(current, _source) || !_source.AcceptsQueries || current.Generation != _pending.GridGeneration)
                status = NavigationFlowBuildStatus.Invalidated;
            else if (_stats[0].NumericFailure)
                status = NavigationFlowBuildStatus.NumericFailure;
            else
            {
                field = _pending;
                field.Publish(_stats[0]);
                _pending = null;
            }
            Discard();
            return true;
        }

        private void Discard()
        {
            _pending?.Dispose();
            _pending = null;
            _source = null;
            _build = default;
        }

        public void Dispose()
        {
            if (Busy) _build.Complete();
            Discard();
            if (_heap.IsCreated) _heap.Dispose();
            if (_positions.IsCreated) _positions.Dispose();
            if (_stats.IsCreated) _stats.Dispose();
            if (_noOverrides.IsCreated) _noOverrides.Dispose();
        }

        private struct FlowGraph
        {
            internal NavigationGraphView Grid;
            internal NativeArray<double>.ReadOnly Costs;

            internal bool Forward(int node, int direction, out int next, out double cost)
            {
                if (!Grid.TryGetNeighbor(node, direction, out next, out cost))
                    return false;
                if (Costs.Length != 0) cost = Costs[node * 8 + direction];
                return true;
            }

            internal bool Incoming(int2 cell, int node, int direction, out int previous, out double cost)
            {
                previous = -1;
                cost = 0;
                int2 candidate = cell - NavigationGrid.Offset(direction);
                if (!Grid.IsWalkable(candidate))
                    return false;
                previous = Grid.Index(candidate);
                return Forward(previous, direction, out int destination, out cost) && destination == node;
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct IntegrationJob : IJob
        {
            private static readonly ProfilerMarker Marker = new ProfilerMarker("Navigation.Flow.Integration");
            internal FlowGraph Graph;
            internal int Goal;
            internal NativeArray<double> Cost;
            internal NativeArray<NavigationHeapEntry> Entries;
            internal NativeArray<int> Positions;

            public void Execute()
            {
                using var scope = Marker.Auto();
                for (int i = 0; i < Cost.Length; i++)
                    Cost[i] = double.PositiveInfinity;
                var heap = new NavigationOpenHeap(Entries, Positions);
                Cost[Goal] = 0;
                heap.Push(new NavigationHeapEntry(Goal, 0, 0));
                while (heap.Count > 0)
                {
                    int node = heap.Pop().Node;
                    int2 cell = Graph.Grid.Cell(node);
                    for (int dir = 0; dir < 8; dir++)
                    {
                        if (!Graph.Incoming(cell, node, dir, out int previous, out double edge))
                            continue;
                        double oldCost = Cost[previous];
                        double candidate = Cost[node] + edge;
                        if (!(candidate < oldCost))
                            continue;
                        var entry = new NavigationHeapEntry(previous, candidate, 0);
                        Cost[previous] = candidate;
                        if (math.isfinite(oldCost) && Positions[previous] >= 0)
                            heap.DecreaseKey(entry);
                        else
                            heap.Push(entry);
                    }
                }
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct DirectionJob : IJobFor
        {
            internal const int ChunkSize = 128;
            private static readonly ProfilerMarker Marker = new ProfilerMarker("Navigation.Flow.Direction");
            internal FlowGraph Graph;
            internal int Goal;
            [ReadOnly] internal NativeArray<double> Cost;
            [WriteOnly, NativeDisableParallelForRestriction] internal NativeArray<byte> Output;

            public void Execute(int chunk)
            {
                using var scope = Marker.Auto();
                // Each chunk exclusively owns one contiguous range.
                int start = chunk * ChunkSize;
                int end = start + math.min(ChunkSize, Output.Length - start);
                for (int index = start; index < end; index++)
                    Output[index] = Select(index);
            }

            private byte Select(int index)
            {
                if (index == Goal) return NavigationFlowField.GoalCode;
                if (!Graph.Grid.IsWalkable(Graph.Grid.Cell(index))) return NavigationFlowField.BlockedCode;
                double current = Cost[index];
                if (!math.isfinite(current)) return NavigationFlowField.UnreachableCode;

                byte choice = NavigationFlowField.ErrorCode;
                double best = double.PositiveInfinity;
                for (byte dir = 0; dir < 8; dir++)
                {
                    if (!Graph.Forward(index, dir, out int next, out double edge) || !(Cost[next] < current))
                        continue;
                    double score = edge + Cost[next];
                    if (score < best)
                    {
                        best = score;
                        choice = dir;
                    }
                }
                return best == current ? choice : NavigationFlowField.ErrorCode;
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct ValidateJob : IJob
        {
            [ReadOnly] internal NativeArray<byte> Directions;
            [ReadOnly] internal NativeArray<double> Cost;
            internal NativeArray<NavigationFlowStats> Output;

            public void Execute()
            {
                var stats = new NavigationFlowStats();
                for (int i = 0; i < Cost.Length; i++)
                {
                    stats.NumericFailure |= Directions[i] == NavigationFlowField.ErrorCode;
                    if (!math.isfinite(Cost[i])) continue;
                    stats.Reachable++;
                    stats.MaxCost = math.max(stats.MaxCost, Cost[i]);
                }
                Output[0] = stats;
            }
        }
    }
}
