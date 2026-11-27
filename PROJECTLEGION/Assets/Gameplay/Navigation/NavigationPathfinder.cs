using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace ProjectLegion.Gameplay
{
    internal enum NavigationPathStatus { Unsearched, Success, InvalidStart, InvalidDestination, Unreachable }

    internal struct NavigationPathResult
    {
        internal NavigationPathStatus Status;
        internal double Cost, Length;
        internal int PathCount, Expanded, Recorded, Pushes, Pops, Decreases, Reopened;
    }

    internal sealed class NavigationPathfinder : IDisposable
    {
        private NativeArray<uint> _stamps;
        private NativeArray<double> _costs;
        private NativeArray<int> _parents, _positions, _path, _explored;
        private NativeArray<NavigationHeapEntry> _heap;
        private NativeArray<NavigationPathResult> _result;
        private uint _generation;

        internal int Capacity => _stamps.IsCreated ? _stamps.Length : 0;
        internal int ExplorationCapacity => _explored.IsCreated ? _explored.Length : 0;
        internal static int BytesPerNode => sizeof(uint) + sizeof(double) + 3 * sizeof(int)
            + UnsafeUtility.SizeOf<NavigationHeapEntry>();
        internal long PayloadBytes => (long)Capacity * BytesPerNode + (long)ExplorationCapacity * sizeof(int)
            + (_result.IsCreated ? UnsafeUtility.SizeOf<NavigationPathResult>() : 0);

        internal NavigationPathfinder(int capacity, int explorationCapacity = 0)
        {
            if (capacity <= 0 || explorationCapacity < 0)
                throw new ArgumentOutOfRangeException();
            try
            {
                _stamps = new NativeArray<uint>(capacity, Allocator.Persistent);
                _costs = Allocate<double>(capacity);
                _parents = Allocate<int>(capacity);
                _positions = Allocate<int>(capacity);
                _path = Allocate<int>(capacity);
                _heap = Allocate<NavigationHeapEntry>(capacity);
                _explored = Allocate<int>(explorationCapacity);
                _result = new NativeArray<NavigationPathResult>(1, Allocator.Persistent);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private static NativeArray<T> Allocate<T>(int capacity) where T : struct =>
            new NativeArray<T>(capacity, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);

        internal NavigationPathResult Search(NavigationGrid grid, int2 start, int2 destination, bool record = false)
        {
            if (!_result.IsCreated)
                throw new ObjectDisposedException(nameof(NavigationPathfinder));
            if ((long)grid.Size.x * grid.Size.y > Capacity)
                throw new ArgumentException("Search workspace is smaller than the graph.", nameof(grid));

            uint generation = unchecked(++_generation);
            new SearchJob
            {
                Graph = grid.AsReadOnly(),
                Start = start,
                Destination = destination,
                Generation = generation,
                Record = record,
                Stamps = _stamps,
                Costs = _costs,
                Parents = _parents,
                Positions = _positions,
                Entries = _heap,
                Path = _path,
                Explored = _explored,
                Output = _result
            }.Run();
            if (generation == 0)
                _generation = 1;
            return _result[0];
        }

        internal int PathNode(int index) => ResultNode(_path, index, _result[0].PathCount);
        internal int ExploredNode(int index) => ResultNode(_explored, index, _result[0].Recorded);

        private static int ResultNode(NativeArray<int> nodes, int index, int count)
        {
            if ((uint)index >= (uint)count)
                throw new ArgumentOutOfRangeException(nameof(index));
            return nodes[index];
        }

        public void Dispose()
        {
            if (_stamps.IsCreated) _stamps.Dispose();
            if (_costs.IsCreated) _costs.Dispose();
            if (_parents.IsCreated) _parents.Dispose();
            if (_positions.IsCreated) _positions.Dispose();
            if (_path.IsCreated) _path.Dispose();
            if (_explored.IsCreated) _explored.Dispose();
            if (_heap.IsCreated) _heap.Dispose();
            if (_result.IsCreated) _result.Dispose();
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct SearchJob : IJob
        {
            internal NavigationGraphView Graph;
            internal int2 Start, Destination;
            internal uint Generation;
            internal bool Record;
            internal NativeArray<uint> Stamps;
            internal NativeArray<double> Costs;
            internal NativeArray<int> Parents, Positions, Path, Explored;
            internal NativeArray<NavigationHeapEntry> Entries;
            internal NativeArray<NavigationPathResult> Output;

            public void Execute()
            {
                if (Generation == 0)
                {
                    for (int i = 0; i < Stamps.Length; i++)
                        Stamps[i] = 0;
                    Generation = 1;
                }

                Output[0] = FindPath();
            }

            private NavigationPathResult FindPath()
            {
                if (!Graph.IsWalkable(Start))
                    return new NavigationPathResult { Status = NavigationPathStatus.InvalidStart };
                if (!Graph.IsWalkable(Destination))
                    return new NavigationPathResult { Status = NavigationPathStatus.InvalidDestination };

                int start = Graph.Index(Start);
                int goal = Graph.Index(Destination);
                if (start == goal)
                {
                    Path[0] = start;
                    return new NavigationPathResult { Status = NavigationPathStatus.Success, PathCount = 1 };
                }

                var heap = new NavigationOpenHeap(Entries, Positions);
                var result = new NavigationPathResult { Status = NavigationPathStatus.Unreachable, Pushes = 1 };
                Stamps[start] = Generation;
                Costs[start] = 0;
                Parents[start] = -1;
                heap.Push(new NavigationHeapEntry(start, 0, Graph.Distance(start, goal)));

                while (heap.Count > 0)
                {
                    int node = heap.Pop().Node;
                    result.Pops++;
                    if (node == goal)
                    {
                        result.Status = NavigationPathStatus.Success;
                        Reconstruct(node, ref result);
                        return result;
                    }

                    result.Expanded++;
                    if (Record && result.Recorded < Explored.Length)
                        Explored[result.Recorded++] = node;

                    for (int direction = 0; direction < 8; direction++)
                    {
                        if (!Graph.TryGetNeighbor(node, direction, out int next, out double edgeCost))
                            continue;
                        double candidate = Costs[node] + edgeCost;
                        bool seen = Stamps[next] == Generation;
                        if (seen && candidate >= Costs[next])
                            continue;

                        var entry = new NavigationHeapEntry(next, candidate, Graph.Distance(next, goal));
                        Costs[next] = candidate;
                        Parents[next] = node;
                        Stamps[next] = Generation;
                        if (seen && Positions[next] >= 0)
                        {
                            heap.DecreaseKey(entry);
                            result.Decreases++;
                        }
                        else
                        {
                            if (seen)
                                result.Reopened++;
                            heap.Push(entry);
                            result.Pushes++;
                        }
                    }
                }

                return result;
            }

            private void Reconstruct(int goal, ref NavigationPathResult result)
            {
                result.Cost = Costs[goal];
                for (int node = goal; node >= 0; node = Parents[node])
                {
                    Path[result.PathCount++] = node;
                    int parent = Parents[node];
                    if (parent >= 0)
                        result.Length += Graph.Distance(parent, node);
                }

                for (int left = 0, right = result.PathCount - 1; left < right; left++, right--)
                {
                    int node = Path[left];
                    Path[left] = Path[right];
                    Path[right] = node;
                }
            }
        }
    }
}
