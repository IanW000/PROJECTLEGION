using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;

namespace ProjectLegion.Gameplay
{
    internal sealed class NavigationHierarchyBuilder : IDisposable
    {
        private enum Stage { Idle, Topology, Distances, Pack }
        private struct LaneWork { internal long Total, Maximum; }
        private struct Crossing : IComparable<Crossing>
        {
            internal int From, To, GroupA, GroupB;
            internal double Cost;

            public int CompareTo(Crossing other)
            {
                if (GroupA != other.GroupA) return GroupA.CompareTo(other.GroupA);
                if (GroupB != other.GroupB) return GroupB.CompareTo(other.GroupB);
                if (Cost != other.Cost) return Cost.CompareTo(other.Cost);
                if (From != other.From) return From.CompareTo(other.From);
                return To.CompareTo(other.To);
            }
        }

        private Stage _stage;
        private NavigationGrid _grid;
        private HierarchyInput _input;
        private HierarchyPartition _partition;
        private NavigationPortalMode _mode;
        private JobHandle _job;
        private bool _async;
        private long _budget, _peak, _transientPeak;
        private int _lanes;

        private NativeArray<int> _labels, _nodeToPortal, _order, _stack, _cursor;
        private NativeList<Crossing> _crossings;
        private NativeList<int> _discoveredPortals;
        private NativeArray<HierarchyCluster> _clusters;
        private NativeArray<HierarchyBuildStats> _stats;
        private NativeArray<double> _emptyOverrides, _matrix, _cost;
        private NativeArray<int> _positions, _offsets, _portals;
        private NativeArray<NavigationHeapEntry> _heap;
        private NativeArray<HierarchyEdge> _edges;
        private NativeArray<LaneWork> _laneStats;
        private long _bytes;
        private bool _disposed;

        internal bool Busy => _stage != Stage.Idle;
        internal bool IsStageComplete => Busy && _job.IsCompleted;
        internal long PayloadBytes => _bytes;
        internal NavigationHierarchyBuildStatus BeginLayered(LayeredNavigationSnapshot snapshot) =>
            snapshot != null && snapshot.AcceptsQueries ? NavigationHierarchyBuildStatus.UnsupportedTopology :
                NavigationHierarchyBuildStatus.InvalidGrid;

        internal NavigationHierarchyBuildStatus Begin(NavigationGrid grid, int2 clusterSize, NavigationPortalMode mode,
            int workers, long budget, bool asynchronous, NativeArray<double> edgeCosts = default)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NavigationHierarchyBuilder));
            if (Busy) throw new InvalidOperationException("A hierarchy build is already active.");
            if (grid == null || !grid.AcceptsQueries) return NavigationHierarchyBuildStatus.InvalidGrid;
            if (math.any(clusterSize <= 0) || workers < 1 || workers > 16)
                return NavigationHierarchyBuildStatus.InvalidSettings;
            if (budget <= 0) return NavigationHierarchyBuildStatus.BudgetExceeded;
            int nodes = checked(grid.Size.x * grid.Size.y);
            if (edgeCosts.IsCreated && (long)edgeCosts.Length != (long)nodes * 8)
                return NavigationHierarchyBuildStatus.InvalidSettings;

            _partition = new HierarchyPartition(grid.Size, clusterSize);
            long crosses = 6 * ((long)(_partition.Counts.x - 1) * grid.Size.y +
                (long)(_partition.Counts.y - 1) * grid.Size.x);
            if (crosses > int.MaxValue) return NavigationHierarchyBuildStatus.BudgetExceeded;
            int portalCapacity = (int)Math.Min(nodes, crosses * 2);
            long initial = (long)nodes * sizeof(int) * 2 + (long)_partition.MaxNodes * sizeof(int) * 3 +
                crosses * Size<Crossing>() + (long)portalCapacity * sizeof(int) +
                (long)_partition.Count * Size<HierarchyCluster>() + Size<HierarchyBuildStats>();
            if (initial > budget) return NavigationHierarchyBuildStatus.BudgetExceeded;

            _grid = grid;
            _mode = mode;
            _budget = budget;
            _async = asynchronous;
            _lanes = Math.Min(workers, _partition.Count);
            _peak = _transientPeak = 0;
            try
            {
                _labels = Alloc<int>(nodes);
                _nodeToPortal = Alloc<int>(nodes);
                _order = Alloc<int>(_partition.MaxNodes);
                _stack = Alloc<int>(_partition.MaxNodes);
                _cursor = Alloc<int>(_partition.MaxNodes);
                _crossings = new NativeList<Crossing>((int)crosses, Allocator.Persistent);
                _discoveredPortals = new NativeList<int>(portalCapacity, Allocator.Persistent);
                _bytes += crosses * Size<Crossing>() + (long)portalCapacity * sizeof(int);
                _clusters = Alloc<HierarchyCluster>(_partition.Count);
                _stats = Alloc<HierarchyBuildStats>(1);
                _emptyOverrides = Alloc<double>(0);
                _input = new HierarchyInput { Graph = grid.AsReadOnly(),
                    Overrides = (edgeCosts.IsCreated ? edgeCosts : _emptyOverrides).AsReadOnly() };
                var job = new TopologyJob { Input = _input, Partition = _partition, Mode = mode, Labels = _labels,
                    NodeToPortal = _nodeToPortal, Order = _order, Stack = _stack, Cursor = _cursor,
                    Crossings = _crossings, Portals = _discoveredPortals, Clusters = _clusters, Stats = _stats };
                _stage = Stage.Topology;
                TrackPeak();
                if (_async) Register(job.Schedule());
                else job.Run();
            }
            catch
            {
                Abort();
                throw;
            }
            return NavigationHierarchyBuildStatus.Success;
        }

        internal bool TryFinish(NavigationGrid current, out NavigationHierarchyBuildStatus status,
            out NavigationHierarchyGraph graph, long availableBuildBytes = long.MaxValue)
        {
            status = default;
            graph = null;
            while (IsStageComplete)
            {
                _job.Complete();
                if (!ReferenceEquals(current, _grid) || !_grid.AcceptsQueries)
                {
                    status = NavigationHierarchyBuildStatus.Invalidated;
                    Clear();
                    return true;
                }

                try
                {
                    _budget = Math.Min(_budget, availableBuildBytes);
                    if (_stage == Stage.Topology && BeginDistances()) continue;
                    if (_stage == Stage.Distances && BeginPacking()) continue;
                    if (_stage == Stage.Pack)
                    {
                        long graphBytes = (long)_clusters.Length * Size<HierarchyCluster>() +
                            ((long)_portals.Length + _offsets.Length) * sizeof(int) + (long)_edges.Length * Size<HierarchyEdge>();
                        var stats = _stats[0];
                        stats.PeakBytes = _peak;
                        stats.TemporaryBytes = _transientPeak;
                        graph = new NavigationHierarchyGraph(_grid, _partition, _mode, _clusters, _portals,
                            _offsets, _edges, stats, graphBytes);
                        _clusters = default;
                        _portals = default;
                        _offsets = default;
                        _edges = default;
                        Clear();
                        return true;
                    }
                }
                catch
                {
                    Abort();
                    throw;
                }

                status = NavigationHierarchyBuildStatus.BudgetExceeded;
                Clear();
                return true;
            }
            return false;
        }

        private bool BeginDistances()
        {
            Release(ref _labels);
            Release(ref _nodeToPortal);
            Release(ref _order);
            Release(ref _stack);
            Release(ref _cursor);
            var stats = _stats[0];
            long laneNodes = (long)_lanes * _partition.MaxNodes;
            long bytes = stats.MatrixCells * sizeof(double) + laneNodes * (sizeof(double) + sizeof(int) + Size<NavigationHeapEntry>()) +
                (long)_lanes * Size<LaneWork>() + ((long)stats.Portals + 1) * sizeof(int);
            if (stats.MatrixCells + stats.CrossEdges >= int.MaxValue || laneNodes > int.MaxValue || bytes > _budget - _bytes)
                return false;

            _matrix = Alloc<double>((int)stats.MatrixCells);
            _cost = Alloc<double>((int)laneNodes);
            _positions = Alloc<int>((int)laneNodes);
            _heap = Alloc<NavigationHeapEntry>((int)laneNodes);
            _laneStats = Alloc<LaneWork>(_lanes);
            _offsets = Alloc<int>(stats.Portals + 1);
            TrackPeak();
            var job = new DistancesJob
            {
                Input = _input, Partition = _partition, Lanes = _lanes, Matrix = _matrix,
                Cost = _cost, Positions = _positions, Heap = _heap, LaneStats = _laneStats,
                Portals = _discoveredPortals.AsArray().AsReadOnly(), Clusters = _clusters.AsReadOnly()
            };
            var count = new CountJob { Matrix = _matrix.AsReadOnly(), Crossings = _crossings.AsArray().AsReadOnly(),
                Clusters = _clusters.AsReadOnly(), LaneStats = _laneStats.AsReadOnly(), Offsets = _offsets, Stats = _stats };
            _stage = Stage.Distances;
            if (_async)
            {
                _job = job.ScheduleParallel(_lanes, 1, default);
                Register(count.Schedule(_job));
            }
            else
            {
                job.Run(_lanes);
                count.Run();
            }
            return true;
        }

        private bool BeginPacking()
        {
            Release(ref _cost);
            Release(ref _positions);
            Release(ref _heap);
            Release(ref _laneStats);
            var stats = _stats[0];
            if ((long)stats.Edges * Size<HierarchyEdge>() + (long)stats.Portals * sizeof(int) > _budget - _bytes)
                return false;
            _edges = Alloc<HierarchyEdge>(stats.Edges);
            _portals = Alloc<int>(stats.Portals);
            TrackPeak();
            var pack = new PackJob { Matrix = _matrix.AsReadOnly(), Crossings = _crossings.AsArray().AsReadOnly(),
                Clusters = _clusters.AsReadOnly(), Offsets = _offsets.AsReadOnly(), Edges = _edges,
                SourcePortals = _discoveredPortals.AsArray().AsReadOnly(), Portals = _portals };
            _stage = Stage.Pack;
            if (_async) Register(pack.Schedule());
            else pack.Run();
            return true;
        }

        private void Register(JobHandle handle)
        {
            _job = handle;
            _grid.RegisterReader(handle);
            JobHandle.ScheduleBatchedJobs();
        }

        private static int Size<T>() where T : struct => UnsafeUtility.SizeOf<T>();

        private NativeArray<T> Alloc<T>(int count) where T : struct
        {
            var result = new NativeArray<T>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            _bytes += (long)count * Size<T>();
            return result;
        }

        private void Release<T>(ref NativeArray<T> array) where T : struct
        {
            if (!array.IsCreated) return;
            _bytes -= (long)array.Length * Size<T>();
            array.Dispose();
            array = default;
        }

        private void TrackPeak()
        {
            _peak = Math.Max(_peak, _bytes);
            long output = (long)_clusters.Length * Size<HierarchyCluster>() +
                ((long)_portals.Length + _offsets.Length) * sizeof(int) + (long)_edges.Length * Size<HierarchyEdge>();
            _transientPeak = Math.Max(_transientPeak, _bytes - output);
        }

        private void Abort()
        {
            if (Busy) _job.Complete();
            Clear();
        }

        private void Clear()
        {
            Release(ref _labels);
            Release(ref _nodeToPortal);
            Release(ref _order);
            Release(ref _stack);
            Release(ref _cursor);
            if (_crossings.IsCreated) _crossings.Dispose();
            if (_discoveredPortals.IsCreated) _discoveredPortals.Dispose();
            Release(ref _clusters);
            Release(ref _stats);
            Release(ref _emptyOverrides);
            Release(ref _matrix);
            Release(ref _cost);
            Release(ref _positions);
            Release(ref _heap);
            Release(ref _laneStats);
            Release(ref _offsets);
            Release(ref _portals);
            Release(ref _edges);
            _bytes = 0;
            _job = default;
            _grid = null;
            _stage = Stage.Idle;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Abort();
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct TopologyJob : IJob
        {
            private static readonly ProfilerMarker Marker = new ProfilerMarker("Navigation.HPA.Topology");
            internal HierarchyInput Input;
            internal HierarchyPartition Partition;
            internal NavigationPortalMode Mode;
            internal NativeArray<int> Labels, NodeToPortal, Order, Stack, Cursor;
            internal NativeList<Crossing> Crossings;
            internal NativeList<int> Portals;
            internal NativeArray<HierarchyCluster> Clusters;
            internal NativeArray<HierarchyBuildStats> Stats;

            public void Execute()
            {
                using var scope = Marker.Auto();
                for (int i = 0; i < Labels.Length; i++)
                {
                    Labels[i] = -1;
                    NodeToPortal[i] = -1;
                }

                for (int cluster = 0; cluster < Partition.Count; cluster++)
                    LabelComponents(Partition.Region(cluster));

                for (int i = 0; i < Labels.Length; i++)
                {
                    if (Labels[i] < 0) continue;
                    int cluster = Partition.Cluster(Input.Graph.Cell(i));
                    for (int dir = 0; dir < 8; dir++)
                    {
                        if (!Input.Forward(i, dir, out int to, out double cost) ||
                            Labels[to] < 0 || Partition.Cluster(Input.Graph.Cell(to)) == cluster) continue;
                        Crossings.AddNoResize(new Crossing
                        {
                            From = i, To = to, Cost = cost, GroupA = Labels[i], GroupB = Labels[to]
                        });
                    }
                }
                Crossings.AsArray().Sort();
                int raw = Crossings.Length, kept = 0, entrances = 0;
                int groupA = -1, groupB = -1;
                for (int i = 0; i < raw; i++)
                {
                    var edge = Crossings[i];
                    bool newGroup = edge.GroupA != groupA || edge.GroupB != groupB;
                    if (newGroup) entrances++;
                    groupA = edge.GroupA;
                    groupB = edge.GroupB;
                    if (!newGroup && Mode == NavigationPortalMode.Representative) continue;
                    Crossings[kept++] = edge;
                    NodeToPortal[edge.From] = -2;
                    NodeToPortal[edge.To] = -2;
                }
                Crossings.ResizeUninitialized(kept);

                long cells = 0;
                int max = 0;
                for (int cluster = 0; cluster < Partition.Count; cluster++)
                {
                    var region = Partition.Region(cluster);
                    int start = Portals.Length;
                    for (int i = 0; i < region.Count; i++)
                    {
                        int node = region.Global(i);
                        if (NodeToPortal[node] != -2) continue;
                        NodeToPortal[node] = Portals.Length;
                        Portals.AddNoResize(node);
                    }
                    int count = Portals.Length - start;
                    Clusters[cluster] = new HierarchyCluster { PortalStart = start, PortalCount = count,
                        MatrixStart = cells <= int.MaxValue ? (int)cells : 0 };
                    cells += (long)count * count;
                    max = math.max(max, count);
                }
                for (int i = 0; i < kept; i++)
                {
                    var edge = Crossings[i];
                    edge.From = edge.GroupA = NodeToPortal[edge.From];
                    edge.To = edge.GroupB = NodeToPortal[edge.To];
                    Crossings[i] = edge;
                }
                Crossings.AsArray().Sort();
                Stats[0] = new HierarchyBuildStats { Clusters = Partition.Count, Entrances = entrances,
                    CrossCandidates = raw, CrossEdges = kept, Portals = Portals.Length, MaxPortals = max, MatrixCells = cells };
            }

            // Iterative Kosaraju, restricted to a cluster on both passes.
            private void LabelComponents(HierarchyRegion region)
            {
                int ordered = 0;
                for (int i = 0; i < region.Count; i++)
                {
                    int start = region.Global(i);
                    if (Labels[start] != -1 || !Input.Graph.IsWalkable(Input.Graph.Cell(start))) continue;
                    int top = 0;
                    Stack[0] = start;
                    Cursor[0] = 0;
                    Labels[start] = -2;
                    while (top >= 0)
                    {
                        int node = Stack[top];
                        int dir = Cursor[top]++;
                        if (dir == 8)
                        {
                            Order[ordered++] = node;
                            top--;
                        }
                        else if (Input.Forward(node, dir, out int to, out _) && Labels[to] == -1 &&
                            region.Contains(Input.Graph.Cell(to)) && Input.Graph.IsWalkable(Input.Graph.Cell(to)))
                        {
                            Labels[to] = -2;
                            Stack[++top] = to;
                            Cursor[top] = 0;
                        }
                    }
                }
                for (int i = ordered - 1; i >= 0; i--)
                {
                    int root = Order[i];
                    if (Labels[root] != -2) continue;
                    int count = 1;
                    Labels[root] = root;
                    Stack[0] = root;
                    while (count > 0)
                    {
                        int node = Stack[--count];
                        for (int direction = 0; direction < 8; direction++)
                        {
                            if (!Input.Incoming(node, direction, out int from, out _) ||
                                Labels[from] != -2 || !region.Contains(Input.Graph.Cell(from))) continue;
                            Labels[from] = root;
                            Stack[count++] = from;
                        }
                    }
                }
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct DistancesJob : IJobFor
        {
            private static readonly ProfilerMarker Marker = new ProfilerMarker("Navigation.HPA.Precompute");
            internal HierarchyInput Input;
            internal HierarchyPartition Partition;
            internal int Lanes;
            internal NativeArray<int>.ReadOnly Portals;
            internal NativeArray<HierarchyCluster>.ReadOnly Clusters;
            [NativeDisableParallelForRestriction] internal NativeArray<double> Matrix, Cost;
            [NativeDisableParallelForRestriction] internal NativeArray<int> Positions;
            [NativeDisableParallelForRestriction] internal NativeArray<NavigationHeapEntry> Heap;
            internal NativeArray<LaneWork> LaneStats;

            public void Execute(int lane)
            {
                using var scope = Marker.Auto();
                int offset = lane * Partition.MaxNodes;
                var search = new HierarchyLocalSearch { Cost = Cost.GetSubArray(offset, Partition.MaxNodes),
                    Positions = Positions.GetSubArray(offset, Partition.MaxNodes),
                    Entries = Heap.GetSubArray(offset, Partition.MaxNodes) };
                // Preprocessing never reconstructs, so no parent storage is needed.
                long total = 0, max = 0;
                for (int id = lane; id < Partition.Count; id += Lanes)
                {
                    var cluster = Clusters[id];
                    var region = Partition.Region(id);
                    long expanded = 0;
                    for (int row = 0; row < cluster.PortalCount; row++)
                    {
                        expanded += search.Run(Input, region, Portals[cluster.PortalStart + row]);
                        for (int col = 0; col < cluster.PortalCount; col++)
                        {
                            int target = Portals[cluster.PortalStart + col];
                            Matrix[cluster.MatrixStart + row * cluster.PortalCount + col] = row == col ?
                                double.PositiveInfinity : search.Cost[region.Local(Input.Graph.Cell(target))];
                        }
                    }
                    total += expanded;
                    max = Math.Max(max, expanded);
                }
                LaneStats[lane] = new LaneWork { Total = total, Maximum = max };
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        private struct CountJob : IJob
        {
            private static readonly ProfilerMarker Marker = new ProfilerMarker("Navigation.HPA.Count");
            internal NativeArray<double>.ReadOnly Matrix;
            internal NativeArray<Crossing>.ReadOnly Crossings;
            internal NativeArray<HierarchyCluster>.ReadOnly Clusters;
            internal NativeArray<LaneWork>.ReadOnly LaneStats;
            internal NativeArray<int> Offsets;
            internal NativeArray<HierarchyBuildStats> Stats;

            public void Execute()
            {
                using var scope = Marker.Auto();
                int cursor = 0, count = 0;
                for (int c = 0; c < Clusters.Length; c++)
                {
                    var cluster = Clusters[c];
                    for (int row = 0; row < cluster.PortalCount; row++)
                    {
                        int portal = cluster.PortalStart + row;
                        Offsets[portal] = count;
                        for (int col = 0; col < cluster.PortalCount; col++)
                            if (math.isfinite(Matrix[cluster.MatrixStart + row * cluster.PortalCount + col])) count++;
                        while (cursor < Crossings.Length && Crossings[cursor].From == portal)
                        {
                            count++;
                            cursor++;
                        }
                    }
                }
                Offsets[Offsets.Length - 1] = count;
                var stats = Stats[0];
                stats.Edges = count;
                stats.MinLaneExpanded = long.MaxValue;
                for (int i = 0; i < LaneStats.Length; i++)
                {
                    LaneWork lane = LaneStats[i];
                    stats.Expanded += lane.Total;
                    stats.MinLaneExpanded = Math.Min(stats.MinLaneExpanded, lane.Total);
                    stats.MaxLaneExpanded = Math.Max(stats.MaxLaneExpanded, lane.Total);
                    stats.MaxClusterExpanded = Math.Max(stats.MaxClusterExpanded, lane.Maximum);
                }
                Stats[0] = stats;
            }
        }

        [BurstCompile(CompileSynchronously = true)]
        private struct PackJob : IJob
        {
            private static readonly ProfilerMarker Marker = new ProfilerMarker("Navigation.HPA.Pack");
            internal NativeArray<double>.ReadOnly Matrix;
            internal NativeArray<Crossing>.ReadOnly Crossings;
            internal NativeArray<HierarchyCluster>.ReadOnly Clusters;
            internal NativeArray<int>.ReadOnly Offsets, SourcePortals;
            internal NativeArray<int> Portals;
            internal NativeArray<HierarchyEdge> Edges;

            public void Execute()
            {
                using var scope = Marker.Auto();
                for (int i = 0; i < Portals.Length; i++) Portals[i] = SourcePortals[i];
                int cursor = 0;
                for (int c = 0; c < Clusters.Length; c++)
                {
                    var cluster = Clusters[c];
                    for (int row = 0; row < cluster.PortalCount; row++)
                    {
                        int portal = cluster.PortalStart + row;
                        int index = Offsets[portal];
                        for (int col = 0; col < cluster.PortalCount; col++)
                        {
                            double cost = Matrix[cluster.MatrixStart + row * cluster.PortalCount + col];
                            if (math.isfinite(cost))
                                Edges[index++] = new HierarchyEdge { To = cluster.PortalStart + col, Cost = cost };
                        }
                        while (cursor < Crossings.Length && Crossings[cursor].From == portal)
                        {
                            var cross = Crossings[cursor++];
                            Edges[index++] = new HierarchyEdge { To = cross.To, Cost = cross.Cost, Crossing = true };
                        }
                    }
                }
            }
        }
    }
}
