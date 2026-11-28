using System;
using System.Collections.Generic;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace ProjectLegion.Gameplay
{
    public enum LayerConnectionType : byte { Surface, Ramp, Drop, Nonlocal, Bridge }

    internal readonly struct LayerNodeHandle
    {
        internal readonly long Generation;
        internal readonly int Id;
        internal LayerNodeHandle(long generation, int id) { Generation = generation; Id = id; }
    }

    internal readonly struct LayerEdgeHandle
    {
        internal readonly long Generation;
        internal readonly int Id;
        internal LayerEdgeHandle(long generation, int id) { Generation = generation; Id = id; }
    }

    internal struct LayerNode
    {
        internal float3 Position;
        internal float Cost;
        internal int SurfaceKey;
        internal bool Walkable;
    }

    internal struct LayerEdge
    {
        internal double Cost;
        internal int From, To, Owner, StableKey;
        internal LayerConnectionType Type;
    }

    internal readonly struct LayerGraphView
    {
        internal readonly NativeArray<LayerNode>.ReadOnly Nodes;
        internal readonly NativeArray<LayerEdge>.ReadOnly Edges;
        internal readonly NativeArray<int>.ReadOnly Outgoing, Incoming, IncomingEdges;
        private readonly bool _geometricHeuristic;

        internal LayerGraphView(NativeArray<LayerNode>.ReadOnly nodes, NativeArray<LayerEdge>.ReadOnly edges,
            NativeArray<int>.ReadOnly outgoing, NativeArray<int>.ReadOnly incoming, NativeArray<int>.ReadOnly incomingEdges,
            bool geometricHeuristic)
        {
            Nodes = nodes; Edges = edges; Outgoing = outgoing; Incoming = incoming;
            IncomingEdges = incomingEdges; _geometricHeuristic = geometricHeuristic;
        }

        internal bool Valid(int node) => (uint)node < (uint)Nodes.Length && Nodes[node].Walkable;
        internal double Heuristic(int from, int to) => _geometricHeuristic
            ? math.distance((double3)Nodes[from].Position, (double3)Nodes[to].Position) : 0;
    }

    internal sealed class LayeredNavigationSnapshot : IDisposable
    {
        private static long _generation;
        private NativeArray<LayerNode> _nodes;
        private NativeArray<LayerEdge> _edges;
        private NativeArray<int> _columns, _outgoing, _incoming, _incomingEdges;
        private JobHandle _readers, _retirement;
        private bool _hasReaders;
        internal long Generation { get; } = Interlocked.Increment(ref _generation);
        internal int2 Size { get; }
        internal float2 Origin { get; }
        internal float CellSize { get; }
        internal bool IsCreated => _nodes.IsCreated;
        internal bool AcceptsQueries => IsCreated && !_gated;
        private bool _gated;
        internal void GateQueries() => _gated = true;
        internal int NodeCount => IsCreated ? _nodes.Length : 0;
        internal int EdgeCount => IsCreated ? _edges.Length : 0;
        internal bool GeometricHeuristic { get; }
        internal long PayloadBytes => IsCreated ? Estimate(Size, NodeCount, EdgeCount) : 0;

        internal static long Estimate(int2 size, int nodes, int edges) =>
            4L * ((long)size.x * size.y + 3) + (8L + UnsafeUtility.SizeOf<LayerNode>()) * nodes +
            (4L + UnsafeUtility.SizeOf<LayerEdge>()) * edges;

        internal LayeredNavigationSnapshot(int2 size, float2 origin, float cellSize, LayerNode[] nodes,
            int[] columns, List<LayerEdge> edges, long budget)
        {
            Size = size; Origin = origin; CellSize = cellSize;
            if (Estimate(size, nodes.Length, edges.Count) > budget) throw new ArgumentException("Snapshot exceeds budget.");
            edges.Sort(Compare);
            bool geometric = true;
            for (int i = 0; i < edges.Count; i++)
            {
                var edge = edges[i];
                if ((uint)edge.From >= (uint)nodes.Length || (uint)edge.To >= (uint)nodes.Length ||
                    !nodes[edge.From].Walkable || !nodes[edge.To].Walkable ||
                    !math.isfinite(edge.Cost) || edge.Cost <= 0 ||
                    edge.Cost > double.MaxValue / Math.Max(1, nodes.Length))
                    throw new ArgumentException("Invalid directed edge.");
                if (i > 0 && Compare(edges[i - 1], edge) == 0)
                    throw new ArgumentException("Duplicate edge without a distinct stable ownership key.");
                geometric &= edge.Cost >= math.distance((double3)nodes[edge.From].Position,
                    (double3)nodes[edge.To].Position);
            }
            GeometricHeuristic = geometric;
            try
            {
                _nodes = new NativeArray<LayerNode>(nodes, Allocator.Persistent);
                _edges = new NativeArray<LayerEdge>(edges.Count, Allocator.Persistent);
                _columns = new NativeArray<int>(columns, Allocator.Persistent);
                _outgoing = new NativeArray<int>(nodes.Length + 1, Allocator.Persistent);
                _incoming = new NativeArray<int>(nodes.Length + 1, Allocator.Persistent);
                _incomingEdges = new NativeArray<int>(edges.Count, Allocator.Persistent);
                for (int i = 0; i < edges.Count; i++)
                {
                    var edge = edges[i];
                    _edges[i] = edge;
                    _outgoing[edge.From + 1]++;
                    _incoming[edge.To + 1]++;
                }
                for (int i = 1; i <= nodes.Length; i++)
                {
                    _outgoing[i] += _outgoing[i - 1];
                    _incoming[i] += _incoming[i - 1];
                }
                using var cursor = new NativeArray<int>(_incoming, Allocator.Temp);
                var writable = cursor;
                for (int i = 0; i < edges.Count; i++) _incomingEdges[writable[_edges[i].To]++] = i;
            }
            catch { Dispose(); throw; }
        }

        private static int Compare(LayerEdge a, LayerEdge b)
        {
            int order = a.From.CompareTo(b.From);
            if (order == 0) order = a.To.CompareTo(b.To);
            if (order == 0) order = ((byte)a.Type).CompareTo((byte)b.Type);
            if (order == 0) order = a.Owner.CompareTo(b.Owner);
            if (order == 0) order = a.StableKey.CompareTo(b.StableKey);
            return order == 0 ? a.Cost.CompareTo(b.Cost) : order;
        }

        internal bool Resolve(float3 position, float tolerance, out LayerNodeHandle handle)
        {
            handle = default;
            if (!AcceptsQueries || !math.all(math.isfinite(position)) || !math.isfinite(tolerance) || tolerance < 0) return false;
            double2 cell = math.floor(((double2)position.xz - Origin) / CellSize);
            if (math.any(cell < 0 | cell >= Size)) return false;
            int column = (int)cell.y * Size.x + (int)cell.x;
            int best = -1;
            double delta = tolerance;
            for (int n = _columns[column]; n < _columns[column + 1]; n++)
            {
                var node = _nodes[n];
                if (!node.Walkable) continue;
                double distance = math.abs((double)node.Position.y - position.y);
                if (distance > delta) continue;
                if (distance == delta && best >= 0) return false;
                delta = distance;
                best = n;
            }
            if (best < 0) return false;
            handle = new LayerNodeHandle(Generation, best);
            return true;
        }

        internal bool Matches(LayerNodeHandle node) => AcceptsQueries && node.Generation == Generation &&
            (uint)node.Id < (uint)_nodes.Length;

        internal int LayerIndex(int node)
        {
            var cell = (int2)math.floor((_nodes[node].Position.xz - Origin) / CellSize);
            return node - _columns[cell.y * Size.x + cell.x];
        }
        internal bool TryGetEdge(LayerEdgeHandle edge, out LayerEdge value)
        {
            value = default;
            if (!AcceptsQueries || edge.Generation != Generation || (uint)edge.Id >= (uint)_edges.Length) return false;
            value = _edges[edge.Id];
            return true;
        }

        internal LayerGraphView View() => new LayerGraphView(_nodes.AsReadOnly(), _edges.AsReadOnly(),
            _outgoing.AsReadOnly(), _incoming.AsReadOnly(), _incomingEdges.AsReadOnly(), GeometricHeuristic);

        internal int ColumnStart(int column) => _columns[column];

        internal int ResolveSurface(float3 position, int stableKey)
        {
            if (!IsCreated || stableKey <= 0 || !math.all(math.isfinite(position))) return -1;
            var cell = (int2)math.floor((position.xz - Origin) / CellSize);
            if (math.any(cell < 0 | cell >= Size)) return -1;
            int column = cell.y * Size.x + cell.x, found = -1;
            for (int node = _columns[column]; node < _columns[column + 1]; node++)
            {
                var sample = _nodes[node];
                if (!sample.Walkable || sample.SurfaceKey != stableKey || !sample.Position.Equals(position)) continue;
                if (found >= 0) return -1;
                found = node;
            }
            return found;
        }

        internal void RegisterReader(JobHandle job)
        {
            if (!IsCreated) throw new ObjectDisposedException(nameof(LayeredNavigationSnapshot));
            if (_hasReaders && !_readers.IsCompleted) _readers = JobHandle.CombineDependencies(_readers, job);
            else
            {
                if (_hasReaders) _readers.Complete();
                _readers = job;
            }
            _hasReaders = true;
        }

        internal JobHandle Retire()
        {
            if (!IsCreated) return _retirement;
            var dependency = _hasReaders ? _readers : default;
            _retirement = JobHandle.CombineDependencies(
                JobHandle.CombineDependencies(_nodes.Dispose(dependency), _edges.Dispose(dependency)),
                JobHandle.CombineDependencies(
                    JobHandle.CombineDependencies(_columns.Dispose(dependency), _outgoing.Dispose(dependency)),
                    JobHandle.CombineDependencies(_incoming.Dispose(dependency), _incomingEdges.Dispose(dependency))));
            JobHandle.ScheduleBatchedJobs();
            return _retirement;
        }

        public void Dispose()
        {
            if (!_columns.IsCreated || !_incomingEdges.IsCreated)
            {
                if (_nodes.IsCreated) _nodes.Dispose();
                if (_edges.IsCreated) _edges.Dispose();
                if (_columns.IsCreated) _columns.Dispose();
                if (_outgoing.IsCreated) _outgoing.Dispose();
                if (_incoming.IsCreated) _incoming.Dispose();
                if (_incomingEdges.IsCreated) _incomingEdges.Dispose();
            }
            Retire().Complete();
        }
    }
}
