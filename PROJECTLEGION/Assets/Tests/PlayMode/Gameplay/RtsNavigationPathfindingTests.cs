using System;
using System.Diagnostics;
using System.Reflection;
using NUnit.Framework;
using Unity.Mathematics;
using Random = System.Random;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsNavigationPathfindingTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void FlatRoutesUseExistingDirections(bool diagonal)
        {
            using var grid = BuildGraph(5, 0, diagonal, "open");
            using var search = new NavigationPathfinder(25);
            var result = AssertOptimal(grid, search, int2.zero, new int2(4, 4));
            Assert.That(result.PathCount, Is.EqualTo(diagonal ? 5 : 9));
            Assert.That(result.Length, Is.EqualTo(diagonal ? 4 * Math.Sqrt(2) : 8).Within(1e-12));
        }

        [Test]
        public void CostlyCenterIsAvoided()
        {
            using var grid = BuildGraph(5, 0, false, "open");
            grid.SetNode(new int2(2, 2), 0, 64, 3);
            using var search = new NavigationPathfinder(25);
            var result = AssertOptimal(grid, search, new int2(0, 2), new int2(4, 2));
            Assert.That(result.Cost, Is.EqualTo(6));
            for (int i = 0; i < result.PathCount; i++)
                Assert.That(search.PathNode(i), Is.Not.EqualTo(12));
        }

        [Test]
        public void WorkspaceReusesBuffersAndDiscardsOldResultCounts()
        {
            using var grid = BuildGraph(9, 0, true, "split");
            var search = new NavigationPathfinder(81, 3);
            using (search)
            {
                var start = new int2(0, 0);
                var end = new int2(2, 7);
                var expected = AssertOptimal(grid, search, start, end);
                int[] nodes = new int[expected.PathCount];
                for (int i = 0; i < nodes.Length; i++) nodes[i] = search.PathNode(i);

                long bytes = search.PayloadBytes;
                for (int iteration = 0; iteration < 12; iteration++)
                {
                    var same = search.Search(grid, end, end, true);
                    Assert.That(same.Status, Is.EqualTo(NavigationPathStatus.Success));
                    Assert.That(same.PathCount, Is.EqualTo(1));
                    Assert.That(same.Cost + same.Length, Is.Zero);
                    Assert.That(search.PathNode(0), Is.EqualTo(65));
                    AssertFailure(search.Search(grid, new int2(-1), end), NavigationPathStatus.InvalidStart);
                    AssertFailure(search.Search(grid, start, new int2(9)), NavigationPathStatus.InvalidDestination);
                    AssertFailure(search.Search(grid, start, new int2(8), true), NavigationPathStatus.Unreachable);
                    if (iteration == 5)
                        typeof(NavigationPathfinder).GetField("_generation", BindingFlags.NonPublic | BindingFlags.Instance)
                            .SetValue(search, uint.MaxValue);
                    var repeated = search.Search(grid, start, end, true);
                    Assert.That(repeated.Recorded, Is.EqualTo(3));
                    Assert.That(repeated.Cost, Is.EqualTo(expected.Cost));
                    Assert.That(repeated.PathCount, Is.EqualTo(nodes.Length));
                    Assert.That(search.PayloadBytes, Is.EqualTo(bytes));
                    for (int i = 0; i < nodes.Length; i++) Assert.That(search.PathNode(i), Is.EqualTo(nodes[i]));
                }
            }
            search.Dispose();
            Assert.That(search.PayloadBytes, Is.Zero);
            Assert.Throws<ObjectDisposedException>(() => search.Search(grid, int2.zero, int2.zero));
        }

        [Test]
        public void RandomHeightAndCostGraphsMatchDijkstraAndExerciseDecreaseKey()
        {
            int decreases = 0;
            using var search = new NavigationPathfinder(100);
            var queries = new Random(8102);
            for (int seed = 0; seed < 16; seed++)
            {
                using var grid = BuildGraph(10, seed, seed % 2 == 0, "random");
                for (int query = 0; query < 18; query++)
                {
                    var start = new int2(queries.Next(10), queries.Next(10));
                    var end = new int2(queries.Next(10), queries.Next(10));
                    decreases += AssertOptimal(grid, search, start, end).Decreases;
                }
            }
            Assert.That(decreases, Is.GreaterThan(0));
        }

        [TestCase(64, "open")]
        [TestCase(64, "gate")]
        [TestCase(64, "split")]
        [TestCase(128, "open")]
        [TestCase(128, "gate")]
        [TestCase(128, "split")]
        [TestCase(256, "open")]
        [TestCase(256, "gate")]
        [TestCase(256, "split")]
        public void ReportSingleQueryCosts(int size, string layout)
        {
            const int count = 30;
            long begin = Stopwatch.GetTimestamp();
            using var grid = BuildGraph(size, 0, true, layout);
            double build = Elapsed(begin);
            begin = Stopwatch.GetTimestamp();
            using var search = new NavigationPathfinder(size * size, 512);
            double allocate = Elapsed(begin);
            foreach (int distance in new[] { 8, size / 3, size - 1 })
            {
                foreach (bool record in new[] { false, true })
                {
                    var goal = new int2(distance, distance / 2);
                    for (int warmup = 0; warmup < 3; warmup++)
                        search.Search(grid, int2.zero, goal, record);
                    var times = new double[count];
                    NavigationPathResult result = default;
                    long managed = GC.GetAllocatedBytesForCurrentThread();
                    for (int i = 0; i < count; i++)
                    {
                        begin = Stopwatch.GetTimestamp();
                        result = search.Search(grid, int2.zero, goal, record);
                        times[i] = Elapsed(begin);
                    }
                    managed = GC.GetAllocatedBytesForCurrentThread() - managed;
                    Array.Sort(times);
                    TestContext.WriteLine(FormattableString.Invariant(
                        $"ASTAR {size} {layout} distance={distance} record={record} status={result.Status} median={times[count / 2]:F4}ms p95={times[28]:F4}ms expanded={result.Expanded} pushes={result.Pushes} pops={result.Pops} decrease={result.Decreases} reopened={result.Reopened} recorded={result.Recorded} bytes={search.PayloadBytes} perNode={NavigationPathfinder.BytesPerNode} managed30={managed} graphBytes={grid.PayloadBytes} graphBuild={build:F3}ms workspaceInit={allocate:F3}ms"));
                    Assert.That(result.Status, Is.EqualTo(layout == "split" && distance >= size / 2
                        ? NavigationPathStatus.Unreachable : NavigationPathStatus.Success));
                }
            }
        }

        private static double Elapsed(long begin) => (Stopwatch.GetTimestamp() - begin) * 1000d / Stopwatch.Frequency;

        private static NavigationGrid BuildGraph(int side, int seed, bool diagonal, string layout)
        {
            var random = new Random(seed);
            var grid = new NavigationGrid(new int2(side), float2.zero, 1);
            for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
                grid.SetNode(new int2(x, z), layout == "random" ? random.Next(8) * 0.125f : 0,
                    layout == "random" ? random.Next(1, 8) : 1, 3);

            for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
            {
                var from = new int2(x, z);
                for (int i = 0; i < (diagonal ? 4 : 2); i++)
                {
                    int direction = i < 2 ? i : i + 2;
                    int2 to = from + NavigationGrid.Offset(direction);
                    if (!grid.Contains(to))
                        continue;
                    bool crosses = (x < side / 2) != (to.x < side / 2);
                    if (crosses && (layout == "split" || (layout == "gate" && z < side * 3 / 4)))
                        continue;
                    if (layout != "random" || random.Next(3) != 0)
                        grid.Connect(from, to, direction);
                }
            }

            return grid;
        }

        internal static NavigationPathResult AssertOptimal(NavigationGrid grid, NavigationPathfinder search, int2 start, int2 end)
        {
            double expected = Dijkstra(grid, start, end);
            var result = search.Search(grid, start, end);
            if (double.IsPositiveInfinity(expected))
            {
                AssertFailure(result, NavigationPathStatus.Unreachable);
                return result;
            }

            Assert.That(result.Status, Is.EqualTo(NavigationPathStatus.Success));
            Assert.That(result.PathCount, Is.GreaterThan(0));
            int2 Cell(int index) => new int2(index % grid.Size.x, index / grid.Size.x);
            Assert.That(Cell(search.PathNode(0)), Is.EqualTo(start));
            Assert.That(Cell(search.PathNode(result.PathCount - 1)), Is.EqualTo(end));
            double cost = 0;
            for (int i = 1; i < result.PathCount; i++)
            {
                int2 from = Cell(search.PathNode(i - 1)), to = Cell(search.PathNode(i));
                bool found = false;
                for (int direction = 0; direction < 8; direction++)
                {
                    if (!grid.TryGetNeighbor(from, direction, out var candidate, out double edge) || !candidate.Equals(to))
                        continue;
                    cost += edge;
                    found = true;
                    break;
                }
                Assert.That(found, Is.True);
            }
            double tolerance = 1e-12 * Math.Max(1, expected);
            Assert.That(result.Cost, Is.EqualTo(cost).Within(tolerance));
            Assert.That(result.Cost, Is.EqualTo(expected).Within(tolerance));
            return result;
        }

        private static void AssertFailure(NavigationPathResult result, NavigationPathStatus expected)
        {
            Assert.That(result.Status, Is.EqualTo(expected));
            Assert.That(result.PathCount, Is.Zero);
        }

        // Deliberately uses an O(V²) linear scan and no A* data structures.
        private static double Dijkstra(NavigationGrid grid, int2 start, int2 end)
        {
            int width = grid.Size.x;
            var distances = new double[width * grid.Size.y];
            var settled = new bool[distances.Length];
            for (int i = 0; i < distances.Length; i++)
                distances[i] = double.PositiveInfinity;
            distances[start.y * width + start.x] = 0;
            while (true)
            {
                int best = -1;
                double value = double.PositiveInfinity;
                for (int i = 0; i < distances.Length; i++)
                {
                    if (!settled[i] && distances[i] < value)
                    {
                        best = i;
                        value = distances[i];
                    }
                }
                if (best < 0 || best == end.y * width + end.x)
                    return value;
                settled[best] = true;
                for (int direction = 0; direction < 8; direction++)
                {
                    if (!grid.TryGetNeighbor(new int2(best % width, best / width), direction, out var to, out double weight))
                        continue;
                    int index = to.y * width + to.x;
                    distances[index] = Math.Min(distances[index], value + weight);
                }
            }
        }
    }
}
