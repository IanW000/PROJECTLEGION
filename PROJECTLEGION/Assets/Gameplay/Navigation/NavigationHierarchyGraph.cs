using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace ProjectLegion.Gameplay
{
    internal enum NavigationPortalMode { Exhaustive, Representative }
    internal enum NavigationHierarchyBuildStatus { Success, InvalidGrid, InvalidSettings, BudgetExceeded, Invalidated,
        UnsupportedTopology }
    internal enum NavigationHierarchyStatus { Success, InvalidStart, InvalidDestination, Unreachable,
        Invalidated, OutputCapacity, InvalidRefinement }

    internal struct HierarchyCluster
    {
        internal int PortalStart, PortalCount, MatrixStart;
    }

    internal struct HierarchyEdge
    {
        internal double Cost;
        internal int To;
        internal bool Crossing;
    }

    internal struct HierarchyBuildStats
    {
        internal int Clusters, Entrances, CrossCandidates, CrossEdges, Portals, Edges, MaxPortals;
        internal long MatrixCells, Expanded, MaxClusterExpanded, MinLaneExpanded, MaxLaneExpanded;
        internal long PeakBytes, TemporaryBytes;
    }

    internal struct HierarchyInput
    {
        internal NavigationGraphView Graph;
        internal NativeArray<double>.ReadOnly Overrides;

        internal bool Forward(int from, int direction, out int to, out double cost)
        {
            if (!Graph.TryGetNeighbor(from, direction, out to, out cost))
                return false;
            if (Overrides.Length != 0)
                cost = Overrides[from * 8 + direction];
            return true;
        }

        internal bool Incoming(int at, int direction, out int from, out double cost)
        {
            from = -1;
            cost = 0;
            int2 cell = Graph.Cell(at) - NavigationGrid.Offset(direction);
            if (!Graph.IsWalkable(cell))
                return false;
            from = Graph.Index(cell);
            return Forward(from, direction, out int target, out cost) && target == at;
        }

        internal double Heuristic(int from, int to) => Overrides.Length == 0 ? Graph.Distance(from, to) : 0;
    }

    internal readonly struct HierarchyPartition
    {
        internal readonly int2 Size, ClusterSize, Counts;

        internal HierarchyPartition(int2 size, int2 clusterSize)
        {
            Size = size;
            ClusterSize = math.min(size, clusterSize);
            Counts = (size - 1) / ClusterSize + 1;
        }

        internal int Count => Counts.x * Counts.y;
        internal int MaxNodes => ClusterSize.x * ClusterSize.y;

        internal int Cluster(int2 cell)
        {
            int2 c = cell / ClusterSize;
            return c.y * Counts.x + c.x;
        }

        internal HierarchyRegion Region(int id)
        {
            int2 origin = new int2(id % Counts.x, id / Counts.x) * ClusterSize;
            return new HierarchyRegion(origin, math.min(ClusterSize, Size - origin), Size.x);
        }
    }

    internal readonly struct HierarchyRegion
    {
        internal readonly int2 Origin, Size;
        internal readonly int Width;

        internal HierarchyRegion(int2 origin, int2 size, int width)
        {
            Origin = origin;
            Size = size;
            Width = width;
        }

        internal int Count => Size.x * Size.y;
        internal bool Contains(int2 cell) => math.all(cell >= Origin & cell < Origin + Size);
        internal int Local(int2 cell) => (cell.y - Origin.y) * Size.x + cell.x - Origin.x;
        internal int Global(int index) => (Origin.y + index / Size.x) * Width + Origin.x + index % Size.x;
    }

    internal readonly struct NavigationHierarchyView
    {
        internal readonly HierarchyPartition Partition;
        internal readonly NativeArray<HierarchyCluster>.ReadOnly Clusters;
        internal readonly NativeArray<int>.ReadOnly Portals, Offsets;
        internal readonly NativeArray<HierarchyEdge>.ReadOnly Edges;

        internal NavigationHierarchyView(HierarchyPartition partition, NativeArray<HierarchyCluster>.ReadOnly clusters,
            NativeArray<int>.ReadOnly portals, NativeArray<int>.ReadOnly offsets, NativeArray<HierarchyEdge>.ReadOnly edges)
        {
            Partition = partition;
            Clusters = clusters;
            Portals = portals;
            Offsets = offsets;
            Edges = edges;
        }
    }

    internal sealed class NavigationHierarchyGraph : IDisposable
    {
        private readonly NavigationGrid _source;
        private NativeArray<HierarchyCluster> _clusters;
        private NativeArray<int> _portals;
        private NativeArray<int> _offsets;
        private NativeArray<HierarchyEdge> _edges;
        private JobHandle _readers, _retirement;
        private bool _hasReaders, _published;
        internal readonly HierarchyPartition Partition;
        internal readonly NavigationPortalMode Mode;
        internal long GridGeneration => _source.Generation;
        internal HierarchyBuildStats Stats { get; private set; }
        internal long PayloadBytes { get; private set; }

        internal NavigationHierarchyGraph(NavigationGrid source, HierarchyPartition partition,
            NavigationPortalMode mode, NativeArray<HierarchyCluster> clusters, NativeArray<int> portals,
            NativeArray<int> offsets, NativeArray<HierarchyEdge> edges, HierarchyBuildStats stats, long bytes)
        {
            _source = source;
            Partition = partition;
            Mode = mode;
            _clusters = clusters;
            _portals = portals;
            _offsets = offsets;
            _edges = edges;
            Stats = stats;
            PayloadBytes = bytes;
            _published = true;
        }

        // Owner-thread contract: acquire, schedule and register before changing snapshots.
        internal bool IsCurrent(NavigationGrid grid) => _published && ReferenceEquals(grid, _source) &&
            _source.IsCreated && grid.Generation == GridGeneration;

        internal bool TryGetView(NavigationGrid grid, out NavigationHierarchyView view)
        {
            view = default;
            if (!IsCurrent(grid))
                return false;
            view = new NavigationHierarchyView(Partition, _clusters.AsReadOnly(), _portals.AsReadOnly(),
                _offsets.AsReadOnly(), _edges.AsReadOnly());
            return true;
        }

        internal void RegisterReader(JobHandle handle)
        {
            if (!_published) throw new InvalidOperationException("Hierarchy is retired.");
            if (_hasReaders && _readers.IsCompleted)
            {
                _readers.Complete();
                _hasReaders = false;
            }
            _readers = _hasReaders ? JobHandle.CombineDependencies(_readers, handle) : handle;
            _hasReaders = true;
        }

        internal JobHandle Retire()
        {
            if (!_published) return _retirement;
            _published = false;
            PayloadBytes = 0;
            if (_hasReaders && !_readers.IsCompleted)
            {
                _retirement = JobHandle.CombineDependencies(
                    JobHandle.CombineDependencies(_clusters.Dispose(_readers), _portals.Dispose(_readers)),
                    JobHandle.CombineDependencies(_offsets.Dispose(_readers), _edges.Dispose(_readers)));
                JobHandle.ScheduleBatchedJobs();
            }
            else
            {
                if (_hasReaders) _readers.Complete();
                _clusters.Dispose();
                _portals.Dispose();
                _offsets.Dispose();
                _edges.Dispose();
            }
            return _retirement;
        }

        public void Dispose() => Retire().Complete();
    }

    internal struct HierarchyLocalSearch
    {
        internal NativeArray<double> Cost;
        internal NativeArray<int> Parents, Positions;
        internal NativeArray<NavigationHeapEntry> Entries;

        // With a target, the same kernel becomes a bounded A*. Otherwise it settles all reachable nodes.
        internal int Run(HierarchyInput graph, HierarchyRegion region, int start, int target = -1, bool reverse = false)
        {
            for (int i = 0; i < region.Count; i++)
                Cost[i] = double.PositiveInfinity;
            int first = region.Local(graph.Graph.Cell(start));
            var heap = new NavigationOpenHeap(Entries, Positions);
            Cost[first] = 0;
            if (Parents.IsCreated) Parents[first] = -1;
            heap.Push(new NavigationHeapEntry(first, 0, target < 0 ? 0 : graph.Heuristic(start, target)));
            int expanded = 0;
            while (heap.Count > 0)
            {
                int current = heap.Pop().Node;
                int global = region.Global(current);
                if (global == target) break;
                expanded++;
                for (int d = 0; d < 8; d++)
                {
                    bool edge = reverse ? graph.Incoming(global, d, out int next, out double weight) :
                        graph.Forward(global, d, out next, out weight);
                    if (!edge) continue;
                    int2 cell = graph.Graph.Cell(next);
                    if (!region.Contains(cell) || !graph.Graph.IsWalkable(cell)) continue;
                    int local = region.Local(cell);
                    double previous = Cost[local];
                    double candidate = Cost[current] + weight;
                    if (!(candidate < previous)) continue;
                    var entry = new NavigationHeapEntry(local, candidate, target < 0 ? 0 : graph.Heuristic(next, target));
                    Cost[local] = candidate;
                    if (Parents.IsCreated) Parents[local] = current;
                    if (math.isfinite(previous) && Positions[local] >= 0) heap.DecreaseKey(entry);
                    else heap.Push(entry);
                }
            }
            return expanded;
        }
    }
}
