using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;
using Random = System.Random;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsNavigationHierarchyTests
    {
        internal const long Budget = 256L * 1024 * 1024;

        [TestCase(1)]
        [TestCase(2)]
        [TestCase(3)]
        [TestCase(4)]
        public void AllDirectedPairsMatchIndependentReachabilityAndExhaustiveCost(int seed)
        {
            using var grid = new NavigationGrid(new int2(6, 5), float2.zero, 1);
            using var costs = new NativeArray<double>(30 * 8, Allocator.Persistent);
            var writable = costs;
            var random = new Random(seed);
            var original = grid.AsReadOnly();
            for (int index = 0; index < 30; index++)
                grid.SetNode(original.Cell(index), random.Next(3), 1, 3);
            for (int index = 0; index < 30; index++)
            for (int direction = 0; direction < 8; direction++)
            {
                int2 from = original.Cell(index);
                if (!grid.Contains(from + NavigationGrid.Offset(direction)) || random.Next(100) >= 43) continue;
                grid.ConnectDirected(from, direction);
                writable[index * 8 + direction] = 0.2 + random.Next(1, 40) * 0.25;
            }

            double[,] expected = Floyd(grid, costs);
            foreach (var mode in new[] { NavigationPortalMode.Exhaustive, NavigationPortalMode.Representative })
            {
                using var hierarchy = Build(grid, new int2(2, 3), mode, costs: costs);
                using var query = new NavigationHierarchyPathfinder(hierarchy, 90, Budget);
                Assert.That(hierarchy.Stats.Clusters, Is.EqualTo(6));
                for (int start = 0; start < 30; start++)
                for (int goal = 0; goal < 30; goal++)
                {
                    var result = Run(query, grid, hierarchy, original.Cell(start), original.Cell(goal), costs: costs);
                    if (double.IsInfinity(expected[start, goal]))
                        Assert.That(result.Status, Is.EqualTo(NavigationHierarchyStatus.Unreachable), $"Pair {start}→{goal}");
                    else
                    {
                        Assert.That(result.Status, Is.EqualTo(NavigationHierarchyStatus.Success), $"Pair {start}→{goal}");
                        AssertLegal(grid, query, result, start, goal, costs);
                        if (mode == NavigationPortalMode.Exhaustive)
                            Assert.That(result.Cost, Is.EqualTo(expected[start, goal]).Within(1e-9));
                        else
                            Assert.That(result.Cost + 1e-9, Is.GreaterThanOrEqualTo(expected[start, goal]));
                    }
                }
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SameClusterCanLeaveAndReenterWhenNecessaryOrCheaper(bool directExpensive)
        {
            using var grid = new NavigationGrid(new int2(3, 2), float2.zero, 1);
            using var costs = new NativeArray<double>(48, Allocator.Persistent);
            var writable = costs;
            var low = grid.AsReadOnly();
            for (int i = 0; i < 6; i++) grid.SetNode(low.Cell(i), 0, 1, 3);
            void Link(int node, int dir, double cost)
            {
                grid.ConnectDirected(low.Cell(node), dir);
                writable[node * 8 + dir] = cost;
            }
            Link(0, 0, 1); Link(1, 0, 1); Link(2, 1, 1); Link(5, 2, 1); Link(4, 2, 1);
            if (directExpensive) Link(0, 1, 50);
            using var hierarchy = Build(grid, new int2(2), NavigationPortalMode.Exhaustive, costs: costs);
            using var query = new NavigationHierarchyPathfinder(hierarchy, 6, Budget);
            var result = Run(query, grid, hierarchy, int2.zero, new int2(0, 1), costs: costs);
            Assert.That(result.Status, Is.EqualTo(NavigationHierarchyStatus.Success));
            Assert.That(result.Cost, Is.EqualTo(5));
            Assert.That(query.PathNode(2), Is.EqualTo(2));
            AssertLegal(grid, query, result, 0, 3, costs);
        }

        [TestCase("open")]
        [TestCase("weighted")]
        public void ReportsRepresentativeDetourNearLongBoundary(string layout)
        {
            using var grid = RtsNavigationFlowFieldTests.MakeGraph(24, layout);
            var start = new int2(11, 22);
            var goal = new int2(12, 22);
            using var astar = new NavigationPathfinder(24 * 24);
            double baseline = astar.Search(grid, start, goal).Cost;
            using var exhaustive = Build(grid, new int2(12, 24), NavigationPortalMode.Exhaustive);
            using var representative = Build(grid, new int2(12, 24), NavigationPortalMode.Representative);
            Assert.That(exhaustive.Stats.CrossEdges, Is.GreaterThan(representative.Stats.CrossEdges));
            using var full = new NavigationHierarchyPathfinder(exhaustive, 576, Budget);
            using var reduced = new NavigationHierarchyPathfinder(representative, 576, Budget);
            var exact = Run(full, grid, exhaustive, start, goal);
            var approximate = Run(reduced, grid, representative, start, goal);
            Assert.That(exact.Status, Is.EqualTo(NavigationHierarchyStatus.Success));
            Assert.That(approximate.Status, Is.EqualTo(NavigationHierarchyStatus.Success));
            Assert.That(exact.Cost, Is.EqualTo(baseline).Within(1e-9));
            Assert.That(approximate.Cost, Is.GreaterThan(baseline));
            Assert.That(approximate.Fallback, Is.EqualTo(HierarchyFallback.None));
            AssertLegal(grid, reduced, approximate, grid.AsReadOnly().Index(start), grid.AsReadOnly().Index(goal));
            TestContext.WriteLine(FormattableString.Invariant(
                $"PH85_QUALITY layout={layout} baseline={baseline:F6} representative={approximate.Cost:F6} extra={approximate.Cost-baseline:F6} relativePct={(approximate.Cost/baseline-1)*100:F4} portals={exhaustive.Stats.Portals}/{representative.Stats.Portals}"));
        }

        [Test]
        public void DiagonalConnectionBetweenCornerClustersAndPartialClustersIsRetained()
        {
            using var grid = new NavigationGrid(new int2(5, 5), float2.zero, 1);
            grid.SetNode(new int2(1, 1), 0, 1, 3);
            grid.SetNode(new int2(2, 2), 0.1f, 1, 3);
            grid.ConnectDirected(new int2(1, 1), 4);
            using var hierarchy = Build(grid, new int2(2), NavigationPortalMode.Representative);
            using var query = new NavigationHierarchyPathfinder(hierarchy, 2, Budget);
            Assert.That(hierarchy.Stats.Clusters, Is.EqualTo(9));
            Assert.That(hierarchy.Stats.CrossEdges, Is.EqualTo(1));
            Assert.That(hierarchy.Partition.Region(8).Size, Is.EqualTo(new int2(1)));
            var result = Run(query, grid, hierarchy, new int2(1), new int2(2));
            Assert.That(result.PathCount, Is.EqualTo(2));
            Assert.That(result.Cost, Is.EqualTo(Math.Sqrt(2.01)).Within(1e-8));
            Assert.That(Run(query, grid, hierarchy, new int2(-1), new int2(2)).Status, Is.EqualTo(NavigationHierarchyStatus.InvalidStart));
            Assert.That(Run(query, grid, hierarchy, new int2(2), int2.zero).Status, Is.EqualTo(NavigationHierarchyStatus.InvalidDestination));
            var reverse = Run(query, grid, hierarchy, new int2(2), new int2(1), fallback: true);
            Assert.That(reverse.Status, Is.EqualTo(NavigationHierarchyStatus.Unreachable));
            Assert.That(reverse.Fallback, Is.EqualTo(HierarchyFallback.FullAStar));
        }

        [Test]
        public void OutputCapacityIsExplicitAndConcurrentReadersRejectRetirement()
        {
            using var grid = RtsNavigationFlowFieldTests.MakeGraph(32, "open");
            using var hierarchy = Build(grid, new int2(8, 12), NavigationPortalMode.Exhaustive, async: true);
            using var first = new NavigationHierarchyPathfinder(hierarchy, 1, Budget);
            using var second = new NavigationHierarchyPathfinder(hierarchy, 1024, Budget);
            var small = Run(first, grid, hierarchy, int2.zero, new int2(31));
            Assert.That(small.Status, Is.EqualTo(NavigationHierarchyStatus.OutputCapacity));
            Assert.That(small.PathCount, Is.Zero);
            Assert.That(first.TrySetOutputCapacity(1024), Is.True);
            Assert.That(first.Begin(grid, hierarchy, int2.zero, new int2(31), true), Is.True);
            Assert.That(second.Begin(grid, hierarchy, new int2(31, 0), new int2(0, 31), true), Is.True);
            Assert.That(first.TrySetOutputCapacity(100), Is.False);
            var a = Finish(first, grid, hierarchy);
            var b = Finish(second, grid, hierarchy);
            Assert.That(a.Status, Is.EqualTo(NavigationHierarchyStatus.Success));
            Assert.That(b.Status, Is.EqualTo(NavigationHierarchyStatus.Success));
            AssertLegal(grid, first, a, 0, 1023);
            AssertLegal(grid, second, b, 31, 992);
            Assert.That(first.Begin(grid, hierarchy, int2.zero, new int2(31), true), Is.True);
            var retiredHierarchy = hierarchy.Retire();
            var retiredGrid = grid.Retire();
            Assert.That(Finish(first, grid, hierarchy).Status, Is.EqualTo(NavigationHierarchyStatus.Invalidated));
            retiredHierarchy.Complete();
            retiredGrid.Complete();
            Assert.Throws<ArgumentOutOfRangeException>(() => second.PathNode(0));
        }

        [Test]
        public void BudgetRejectionAndOldBuildInvalidationDoNotPublishPartialGraphs()
        {
            using var grid = RtsNavigationFlowFieldTests.MakeGraph(24, "open");
            using var builder = new NavigationHierarchyBuilder();
            Assert.That(builder.Begin(grid, new int2(8), NavigationPortalMode.Exhaustive, 2, 1, true),
                Is.EqualTo(NavigationHierarchyBuildStatus.BudgetExceeded));
            Assert.That(builder.Begin(grid, new int2(8), NavigationPortalMode.Exhaustive, 2, 24_000, false),
                Is.EqualTo(NavigationHierarchyBuildStatus.Success));
            Assert.That(builder.TryFinish(grid, out var tooLarge, out var partial), Is.True);
            Assert.That(tooLarge, Is.EqualTo(NavigationHierarchyBuildStatus.BudgetExceeded));
            Assert.That(partial, Is.Null);
            Assert.That(builder.PayloadBytes, Is.Zero);
            Assert.That(builder.Begin(grid, new int2(8), NavigationPortalMode.Exhaustive, 2, Budget, true),
                Is.EqualTo(NavigationHierarchyBuildStatus.Success));
            var retired = grid.Retire();
            Wait(() => builder.TryFinish(null, out var status, out var graph) &&
                status == NavigationHierarchyBuildStatus.Invalidated && graph == null);
            retired.Complete();
            Assert.That(builder.PayloadBytes, Is.Zero);
        }

        [Test]
        public void MissingAbstractConnectionRecoversOnlyWithExplicitFallback()
        {
            using var grid = RtsNavigationFlowFieldTests.MakeGraph(4, "open");
            var partition = new HierarchyPartition(grid.Size, new int2(2));
            // Deliberately malformed test fixture. Builders must never publish this on the open grid.
            using var hierarchy = new NavigationHierarchyGraph(grid, partition, NavigationPortalMode.Representative,
                new NativeArray<HierarchyCluster>(4, Allocator.Persistent),
                new NativeArray<int>(0, Allocator.Persistent), new NativeArray<int>(1, Allocator.Persistent),
                new NativeArray<HierarchyEdge>(0, Allocator.Persistent), default, 0);
            using var query = new NavigationHierarchyPathfinder(hierarchy, 32, Budget);
            Assert.That(Run(query, grid, hierarchy, int2.zero, new int2(3)).Status, Is.EqualTo(NavigationHierarchyStatus.Unreachable));
            var rescued = Run(query, grid, hierarchy, int2.zero, new int2(3), fallback: true, async: true);
            Assert.That(rescued.Status, Is.EqualTo(NavigationHierarchyStatus.Success));
            Assert.That(rescued.Fallback, Is.EqualTo(HierarchyFallback.FullAStar));
            AssertLegal(grid, query, rescued, 0, 15);
        }

        [TestCase(77)]
        [TestCase(88)]
        public void DeterministicPortalAndEdgeOrderAcrossBuildScheduling(int seed)
        {
            using var grid = RtsNavigationFlowFieldTests.MakeGraph(15, "weighted", true, seed);
            using var a = Build(grid, new int2(4, 6), NavigationPortalMode.Representative);
            using var b = Build(grid, new int2(4, 6), NavigationPortalMode.Representative, async: true);
            a.TryGetView(grid, out var av); b.TryGetView(grid, out var bv);
            CollectionAssert.AreEqual(av.Portals.ToArray(), bv.Portals.ToArray());
            CollectionAssert.AreEqual(av.Offsets.ToArray(), bv.Offsets.ToArray());
            Assert.That(av.Edges.Length, Is.EqualTo(bv.Edges.Length));
            for (int i = 0; i < av.Edges.Length; i++)
            {
                Assert.That(av.Edges[i].To, Is.EqualTo(bv.Edges[i].To));
                Assert.That(av.Edges[i].Cost, Is.EqualTo(bv.Edges[i].Cost));
                Assert.That(av.Edges[i].Crossing, Is.EqualTo(bv.Edges[i].Crossing));
            }
            using var aq = new NavigationHierarchyPathfinder(a, 225, Budget);
            using var bq = new NavigationHierarchyPathfinder(b, 225, Budget);
            var ar = Run(aq, grid, a, int2.zero, new int2(14));
            var br = Run(bq, grid, b, int2.zero, new int2(14), async: true);
            Assert.That(ar.Status, Is.EqualTo(NavigationHierarchyStatus.Success));
            Assert.That(br.PathCount, Is.EqualTo(ar.PathCount));
            for (int i = 0; i < ar.PathCount; i++) Assert.That(aq.PathNode(i), Is.EqualTo(bq.PathNode(i)));
            using var exhaustive = Build(grid, new int2(4, 6), NavigationPortalMode.Exhaustive, async: true);
            using var exact = new NavigationHierarchyPathfinder(exhaustive, 225, Budget);
            using var astar = new NavigationPathfinder(225);
            var random = new Random(seed);
            for (int sample = 0; sample < 12; sample++)
            {
                var start = new int2(random.Next(15), random.Next(15));
                var goal = new int2(random.Next(15), random.Next(15));
                var expected = astar.Search(grid, start, goal);
                var result = Run(exact, grid, exhaustive, start, goal);
                Assert.That(result.Status, Is.EqualTo(expected.Status == NavigationPathStatus.Success
                    ? NavigationHierarchyStatus.Success : NavigationHierarchyStatus.Unreachable));
                if (result.Status == NavigationHierarchyStatus.Success)
                    Assert.That(result.Cost, Is.EqualTo(expected.Cost).Within(1e-9 * Math.Max(1, expected.Cost)));
            }
        }

        internal static NavigationHierarchyGraph Build(NavigationGrid grid, int2 size, NavigationPortalMode mode,
            bool async = false, NativeArray<double> costs = default, int workers = 4)
        {
            using var builder = new NavigationHierarchyBuilder();
            Assert.That(builder.Begin(grid, size, mode, workers, Budget, async, costs), Is.EqualTo(NavigationHierarchyBuildStatus.Success));
            NavigationHierarchyGraph graph = null;
            NavigationHierarchyBuildStatus status = default;
            Wait(() => builder.TryFinish(grid, out status, out graph));
            Assert.That(status, Is.EqualTo(NavigationHierarchyBuildStatus.Success));
            return graph;
        }

        internal static NavigationHierarchyResult Run(NavigationHierarchyPathfinder query, NavigationGrid grid,
            NavigationHierarchyGraph hierarchy, int2 start, int2 goal, NativeArray<double> costs = default,
            bool fallback = false, bool async = false)
        {
            Assert.That(query.Begin(grid, hierarchy, start, goal, async, fallback, costs), Is.True);
            return Finish(query, grid, hierarchy);
        }

        private static NavigationHierarchyResult Finish(NavigationHierarchyPathfinder query, NavigationGrid grid,
            NavigationHierarchyGraph hierarchy)
        {
            NavigationHierarchyResult output = default;
            Wait(() => query.TryFinish(grid, hierarchy, out output));
            return output;
        }

        internal static void Wait(Func<bool> done)
        {
            long end = Stopwatch.GetTimestamp() + 45 * Stopwatch.Frequency;
            while (!done())
            {
                if (Stopwatch.GetTimestamp() > end) Assert.Fail("Hierarchy job did not settle.");
                Thread.Yield();
            }
        }

        internal static void AssertLegal(NavigationGrid grid, NavigationHierarchyPathfinder query,
            NavigationHierarchyResult result, int start, int goal, NativeArray<double> costs = default)
        {
            Assert.That(query.PathNode(0), Is.EqualTo(start));
            Assert.That(query.PathNode(result.PathCount - 1), Is.EqualTo(goal));
            double actual = 0;
            var low = grid.AsReadOnly();
            for (int i = 1; i < result.PathCount; i++)
            {
                int from = query.PathNode(i - 1);
                int to = query.PathNode(i);
                bool found = false;
                for (int direction = 0; direction < 8; direction++)
                    if (grid.TryGetNeighbor(low.Cell(from), direction, out var neighbor, out double cost) && low.Index(neighbor) == to)
                    {
                        actual += costs.IsCreated ? costs[from * 8 + direction] : cost;
                        found = true;
                        break;
                    }
                Assert.That(found, Is.True);
            }
            Assert.That(result.Cost, Is.EqualTo(actual).Within(1e-9 * Math.Max(1, actual)));
        }

        private static double[,] Floyd(NavigationGrid grid, NativeArray<double> costs)
        {
            int n = grid.Size.x * grid.Size.y;
            var low = grid.AsReadOnly();
            var result = new double[n, n];
            for (int a = 0; a < n; a++)
            {
                for (int b = 0; b < n; b++) result[a, b] = a == b ? 0 : double.PositiveInfinity;
                for (int direction = 0; direction < 8; direction++)
                    if (grid.TryGetNeighbor(low.Cell(a), direction, out var next, out _))
                        result[a, low.Index(next)] = costs[a * 8 + direction];
            }
            for (int middle = 0; middle < n; middle++)
            for (int a = 0; a < n; a++)
            for (int b = 0; b < n; b++)
                result[a, b] = Math.Min(result[a, b], result[a, middle] + result[middle, b]);
            return result;
        }
    }
}
