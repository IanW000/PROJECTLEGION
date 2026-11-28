using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;

namespace ProjectLegion.Gameplay
{
    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
    internal struct LayerSearchJob : IJob
    {
        private static readonly ProfilerMarker PathMarker = new ProfilerMarker("Navigation.Layered.AStar");
        private static readonly ProfilerMarker FlowMarker = new ProfilerMarker("Navigation.Layered.Flow");
        internal LayerGraphView Graph;
        internal int Start, Goal;
        internal bool Reverse;
        internal NativeArray<double> Costs;
        internal NativeArray<int> ParentEdges, HeapIndices, Nodes, SegmentEdges;
        internal NativeArray<NavigationHeapEntry> HeapEntries;
        internal NativeArray<NavigationPathResult> Result;

        public void Execute()
        {
            if (Reverse) { using var scope = FlowMarker.Auto(); Solve(); }
            else { using var scope = PathMarker.Auto(); Solve(); }
        }

        private void Solve()
        {
            var result = new NavigationPathResult { Status = NavigationPathStatus.Unreachable };
            if (!Graph.Valid(Start)) { result.Status = NavigationPathStatus.InvalidStart; Result[0] = result; return; }
            if (!Reverse && !Graph.Valid(Goal)) { result.Status = NavigationPathStatus.InvalidDestination; Result[0] = result; return; }

            for (int i = 0; i < Costs.Length; i++)
            {
                Costs[i] = double.PositiveInfinity;
                ParentEdges[i] = -1;
            }
            Costs[Start] = 0;
            var heap = new NavigationOpenHeap(HeapEntries, HeapIndices);
            heap.Push(new NavigationHeapEntry(Start, 0, Reverse ? 0 : Graph.Heuristic(Start, Goal)));
            result.Pushes++;
            while (heap.Count > 0)
            {
                int node = heap.Pop().Node;
                result.Pops++;
                if (!Reverse && node == Goal)
                {
                    result.Status = NavigationPathStatus.Success;
                    result.Cost = Costs[node];
                    int steps = 0;
                    while (node != Start)
                    {
                        int edge = ParentEdges[node];
                        Nodes[steps] = node;
                        SegmentEdges[steps++] = edge;
                        var connection = Graph.Edges[edge];
                        result.Length += math.distance((double3)Graph.Nodes[connection.From].Position,
                            (double3)Graph.Nodes[connection.To].Position);
                        node = connection.From;
                    }
                    Nodes[steps] = Start;
                    ReversePrefix(Nodes, steps + 1);
                    ReversePrefix(SegmentEdges, steps);
                    result.PathCount = steps + 1;
                    break;
                }
                result.Expanded++;
                int first = Reverse ? Graph.Incoming[node] : Graph.Outgoing[node];
                int limit = Reverse ? Graph.Incoming[node + 1] : Graph.Outgoing[node + 1];
                for (int i = first; i < limit; i++)
                {
                    int edgeId = Reverse ? Graph.IncomingEdges[i] : i;
                    var edge = Graph.Edges[edgeId];
                    int neighbor = Reverse ? edge.From : edge.To;
                    double old = Costs[neighbor];
                    double candidate = Costs[node] + edge.Cost;
                    if (!(candidate < old)) continue;
                    Costs[neighbor] = candidate;
                    ParentEdges[neighbor] = edgeId;
                    var entry = new NavigationHeapEntry(neighbor, candidate, Reverse ? 0 : Graph.Heuristic(neighbor, Goal));
                    if (math.isfinite(old) && HeapIndices[neighbor] >= 0)
                    {
                        heap.DecreaseKey(entry);
                        result.Decreases++;
                    }
                    else
                    {
                        heap.Push(entry);
                        result.Pushes++;
                        if (math.isfinite(old)) result.Reopened++;
                    }
                }
            }
            if (Reverse)
            {
                result.Status = NavigationPathStatus.Success;
                for (int node = 0; node < ParentEdges.Length; node++)
                {
                    int edge = ParentEdges[node];
                    if (edge >= 0 && !(Costs[Graph.Edges[edge].To] < Costs[node]))
                    {
                        // A numerical plateau would invalidate the strict-descent field contract.
                        result.Status = NavigationPathStatus.Unreachable;
                        break;
                    }
                }
            }
            Result[0] = result;
        }

        private static void ReversePrefix(NativeArray<int> array, int length)
        {
            for (int i = 0; i < length / 2; i++)
            {
                int opposite = length - 1 - i;
                int item = array[i]; array[i] = array[opposite]; array[opposite] = item;
            }
        }
    }

    internal sealed class LayeredSearchWorkspace : IDisposable
    {
        private NativeArray<double> _costs;
        private NativeArray<int> _parents, _positions, _nodes, _edges;
        private NativeArray<NavigationHeapEntry> _heap;
        private NativeArray<NavigationPathResult> _output;
        private LayeredNavigationSnapshot _source;
        private JobHandle _job;
        private bool _pending;
        internal int Capacity => _costs.Length;
        internal static long Budget(int nodes) => (long)nodes * (sizeof(double) + 4 * sizeof(int) +
            UnsafeUtility.SizeOf<NavigationHeapEntry>()) + UnsafeUtility.SizeOf<NavigationPathResult>();

        internal LayeredSearchWorkspace(int count)
        {
            try
            {
                _costs = Allocate<double>(count); _parents = Allocate<int>(count);
                _positions = Allocate<int>(count); _nodes = Allocate<int>(count); _edges = Allocate<int>(count);
                _heap = Allocate<NavigationHeapEntry>(count); _output = Allocate<NavigationPathResult>(1);
            }
            catch { Dispose(); throw; }
        }

        internal static NativeArray<T> Allocate<T>(int count) where T : struct =>
            new NativeArray<T>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

        private LayerSearchJob Create(LayeredNavigationSnapshot graph, int start, int goal)
        {
            if (_pending || !graph.AcceptsQueries || graph.NodeCount != Capacity)
                throw new InvalidOperationException("Workspace busy, stale snapshot, or incorrect workspace capacity.");
            _source = graph;
            return new LayerSearchJob { Graph = graph.View(), Start = start, Goal = goal, Costs = _costs,
                ParentEdges = _parents, HeapIndices = _positions, Nodes = _nodes, SegmentEdges = _edges,
                HeapEntries = _heap, Result = _output };
        }

        internal NavigationPathResult Search(LayeredNavigationSnapshot snapshot, LayerNodeHandle start, LayerNodeHandle goal)
        {
            if (!snapshot.AcceptsQueries)
            {
                _source = snapshot;
                return new NavigationPathResult { Status = NavigationPathStatus.Invalidated };
            }
            Create(snapshot, snapshot.Matches(start) ? start.Id : -1, snapshot.Matches(goal) ? goal.Id : -1).Run();
            return _output[0];
        }

        internal JobHandle Schedule(LayeredNavigationSnapshot graph, int start, int goal)
        {
            _job = Create(graph, start, goal).Schedule();
            _pending = true;
            graph.RegisterReader(_job);
            return _job;
        }

        internal NavigationPathResult Finish()
        {
            if (_pending)
            {
                if (!_job.IsCompleted) throw new InvalidOperationException("Search is still running.");
                _job.Complete();
                _pending = false;
            }
            return _source != null && _source.AcceptsQueries ? _output[0]
                : new NavigationPathResult { Status = NavigationPathStatus.Invalidated };
        }

        internal void DiscardCompleted() { if (_pending) { _job.Complete(); _pending = false; } }
        private bool Readable => !_pending && _output.IsCreated && _source != null && _source.AcceptsQueries;
        internal int Node(int index)
        {
            if (!Readable || (uint)index >= (uint)_output[0].PathCount) throw new ArgumentOutOfRangeException(nameof(index));
            return _nodes[index];
        }

        internal int Edge(int index)
        {
            if (!Readable || (uint)index >= (uint)math.max(0, _output[0].PathCount - 1))
                throw new ArgumentOutOfRangeException(nameof(index));
            return _edges[index];
        }

        internal bool TryNode(int index, out LayerNodeHandle node)
        {
            node = default;
            if (!Readable || (uint)index >= (uint)_output[0].PathCount) return false;
            node = new LayerNodeHandle(_source.Generation, _nodes[index]);
            return true;
        }
        internal bool TryStep(int index, out LayerEdgeHandle edge)
        {
            edge = default;
            if (!Readable ||
                (uint)index >= (uint)math.max(0, _output[0].PathCount - 1)) return false;
            edge = new LayerEdgeHandle(_source.Generation, _edges[index]);
            return true;
        }

        public void Dispose()
        {
            DiscardCompleted();
            if (_costs.IsCreated) _costs.Dispose();
            if (_parents.IsCreated) _parents.Dispose();
            if (_positions.IsCreated) _positions.Dispose();
            if (_nodes.IsCreated) _nodes.Dispose();
            if (_edges.IsCreated) _edges.Dispose();
            if (_heap.IsCreated) _heap.Dispose();
            if (_output.IsCreated) _output.Dispose();
        }
    }

    internal readonly struct LayeredFlowView
    {
        private readonly LayerGraphView _graph;
        private readonly NativeArray<double>.ReadOnly _costs;
        private readonly NativeArray<int>.ReadOnly _next;
        private readonly long _generation;
        private readonly int _goal;

        internal LayeredFlowView(LayerGraphView graph, NativeArray<double>.ReadOnly costs,
            NativeArray<int>.ReadOnly next, long generation, int goal)
        {
            _graph = graph; _costs = costs; _next = next; _generation = generation; _goal = goal;
        }

        internal NavigationFlowState Query(LayerNodeHandle from, out LayerEdgeHandle edge, out double cost)
        {
            edge = default;
            cost = double.PositiveInfinity;
            if (from.Generation != _generation || (uint)from.Id >= (uint)_costs.Length)
                return NavigationFlowState.Invalid;
            if (!_graph.Valid(from.Id)) return NavigationFlowState.Blocked;
            cost = _costs[from.Id];
            if (from.Id == _goal) return NavigationFlowState.Goal;
            int id = _next[from.Id];
            if (id < 0) return NavigationFlowState.Unreachable;
            edge = new LayerEdgeHandle(_generation, id);
            return NavigationFlowState.Direction;
        }
    }

    internal sealed class LayeredFlowField : IDisposable
    {
        private readonly LayeredNavigationSnapshot _snapshot;
        private readonly int _goal;
        private NativeArray<double> _costs;
        private NativeArray<int> _next, _positions, _unused, _unusedEdges;
        private NativeArray<NavigationHeapEntry> _heap;
        private NativeArray<NavigationPathResult> _result;
        private JobHandle _job, _readers, _retirement;
        private bool _ready, _hasReaders;
        internal long PersistentBytes => _costs.IsCreated ? 12L * _costs.Length : 0;
        internal static long BuildBytes(int nodes) => 40L * nodes + UnsafeUtility.SizeOf<NavigationPathResult>();

        internal LayeredFlowField(LayeredNavigationSnapshot snapshot, LayerNodeHandle goal, bool asynchronous, long budget)
        {
            if (!snapshot.Matches(goal) || !snapshot.View().Valid(goal.Id)) throw new ArgumentException("Invalid field goal.");
            int count = snapshot.NodeCount;
            if (BuildBytes(count) > budget) throw new ArgumentException("Field exceeds budget.");
            _snapshot = snapshot;
            _goal = goal.Id;
            try
            {
                _costs = LayeredSearchWorkspace.Allocate<double>(count);
                _next = LayeredSearchWorkspace.Allocate<int>(count);
                _positions = LayeredSearchWorkspace.Allocate<int>(count);
                _unused = LayeredSearchWorkspace.Allocate<int>(0);
                _unusedEdges = LayeredSearchWorkspace.Allocate<int>(0);
                _heap = LayeredSearchWorkspace.Allocate<NavigationHeapEntry>(count);
                _result = LayeredSearchWorkspace.Allocate<NavigationPathResult>(1);
                var job = new LayerSearchJob { Graph = snapshot.View(), Start = goal.Id, Reverse = true,
                    Costs = _costs, ParentEdges = _next, HeapIndices = _positions, HeapEntries = _heap,
                    Nodes = _unused, SegmentEdges = _unusedEdges, Result = _result };
                if (asynchronous)
                {

                    _job = job.Schedule();
                    snapshot.RegisterReader(_job);
                    JobHandle.ScheduleBatchedJobs();
                }
                else job.Run();
            }
            catch { Dispose(); throw; }
        }

        internal bool TryFinish(out bool published)
        {
            published = false;
            if (!_result.IsCreated || !_job.IsCompleted) return false;
            _job.Complete();
            _ready = _snapshot.AcceptsQueries && _result[0].Status == NavigationPathStatus.Success;
            published = _ready;
            ReleaseScratch();
            return true;
        }

        internal NavigationFlowState Query(LayerNodeHandle from, out LayerEdgeHandle edge, out double cost)
        {
            if (TryGetView(_snapshot, out var view)) return view.Query(from, out edge, out cost);
            edge = default; cost = double.PositiveInfinity;
            return NavigationFlowState.Invalid;
        }

        // Acquire, schedule, and register on the owner thread before changing the snapshot.
        internal bool TryGetView(LayeredNavigationSnapshot current, out LayeredFlowView view)
        {
            view = default;
            if (!_ready || current != _snapshot || !_snapshot.AcceptsQueries)
                return false;
            view = new LayeredFlowView(_snapshot.View(), _costs.AsReadOnly(), _next.AsReadOnly(),
                _snapshot.Generation, _goal);
            return true;
        }

        internal void RegisterReader(LayeredNavigationSnapshot current, JobHandle reader)
        {
            if (!TryGetView(current, out _)) throw new InvalidOperationException("Flow snapshot expired.");
            _snapshot.RegisterReader(reader);
            if (_hasReaders && !_readers.IsCompleted) _readers = JobHandle.CombineDependencies(_readers, reader);
            else
            {
                if (_hasReaders) _readers.Complete();
                _readers = reader;
            }
            _hasReaders = true;
        }

        internal JobHandle Retire()
        {
            if (!_costs.IsCreated) return _retirement;
            if (!_next.IsCreated) { _costs.Dispose(); return default; }
            _ready = false;
            var fence = _hasReaders ? JobHandle.CombineDependencies(_readers, _job) : _job;
            _retirement = JobHandle.CombineDependencies(_costs.Dispose(fence), _next.Dispose(fence));
            if (_positions.IsCreated) _retirement = JobHandle.CombineDependencies(_retirement, _positions.Dispose(fence));
            if (_unused.IsCreated) _retirement = JobHandle.CombineDependencies(_retirement, _unused.Dispose(fence));
            if (_unusedEdges.IsCreated) _retirement = JobHandle.CombineDependencies(_retirement, _unusedEdges.Dispose(fence));
            if (_heap.IsCreated) _retirement = JobHandle.CombineDependencies(_retirement, _heap.Dispose(fence));
            if (_result.IsCreated) _retirement = JobHandle.CombineDependencies(_retirement, _result.Dispose(fence));
            JobHandle.ScheduleBatchedJobs();
            return _retirement;
        }

        private void ReleaseScratch()
        {
            if (_positions.IsCreated) _positions.Dispose();
            if (_unused.IsCreated) _unused.Dispose();
            if (_unusedEdges.IsCreated) _unusedEdges.Dispose();
            if (_heap.IsCreated) _heap.Dispose();
            if (_result.IsCreated) _result.Dispose();
        }

        public void Dispose()
        {
            Retire().Complete();
            ReleaseScratch();
        }
    }
}
