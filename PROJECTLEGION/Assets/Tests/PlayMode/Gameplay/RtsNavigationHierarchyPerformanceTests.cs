using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Profiling;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsNavigationHierarchyPerformanceTests
    {
        private const int Samples = 12;
        private static int _outputChecksum;

        [OneTimeSetUp]
        public void WarmBuildJobs()
        {
            using var grid = RtsNavigationFlowFieldTests.MakeGraph(16, "open");
            using var hierarchy = RtsNavigationHierarchyTests.Build(grid, new int2(8),
                NavigationPortalMode.Exhaustive, async: true);
        }

        [TestCase(64)]
        [TestCase(128)]
        [TestCase(256)]
        [TestCase(512)]
        public void ReportOpenGridMatrix(int size)
        {
            using var grid = RtsNavigationFlowFieldTests.MakeGraph(size, "open");
            foreach (int cluster in new[] { 8, 16, 32, 64 })
            foreach (var mode in new[] { NavigationPortalMode.Exhaustive, NavigationPortalMode.Representative })
                Measure(grid, size, "open", cluster, mode);
        }

        [TestCase("weighted")]
        [TestCase("gate")]
        [TestCase("split")]
        public void ReportObstacleAndCostScenarios(string layout)
        {
            const int side = 128;
            using var grid = RtsNavigationFlowFieldTests.MakeGraph(side, layout);
            foreach (var mode in new[] { NavigationPortalMode.Exhaustive, NavigationPortalMode.Representative })
                Measure(grid, side, layout, 16, mode);
        }

        [Test]
        public void ReportPractical1024Configurations()
        {
            using var grid = RtsNavigationFlowFieldTests.MakeGraph(1024, "open");
            Measure(grid, 1024, "open", 32, NavigationPortalMode.Representative);
            Measure(grid, 1024, "open", 64, NavigationPortalMode.Representative);
        }

        [Test]
        public void MeasureAmortizationWithCompleteFarPathBatches()
        {
            const int side = 256;
            using var grid = RtsNavigationFlowFieldTests.MakeGraph(side, "open");
            using var astar = new NavigationPathfinder(side * side);
            foreach (var mode in new[] { NavigationPortalMode.Exhaustive, NavigationPortalMode.Representative })
            {
                long start = Stopwatch.GetTimestamp();
                using var hierarchy = RtsNavigationHierarchyTests.Build(grid, new int2(16), mode, async: true);
                double buildMs = Elapsed(start);
                using var query = new NavigationHierarchyPathfinder(hierarchy, side * side, RtsNavigationHierarchyTests.Budget);
                var warmStart = new int2(1, 1);
                var warmGoal = new int2(side - 2, side / 2);
                RunUntimed(query, grid, hierarchy, warmStart, warmGoal);
                astar.Search(grid, warmStart, warmGoal);
                foreach (int count in new[] { 4, 16, 64, 256, 512 })
                {
                    long before = Stopwatch.GetTimestamp();
                    for (int i = 0; i < count; i++)
                    {
                        var result = RunUntimed(query, grid, hierarchy, new int2(1, 1 + i % 6),
                            new int2(side - 2, side / 2 - i % 3));
                        if (result.Status != NavigationHierarchyStatus.Success) throw new InvalidOperationException();
                        for (int n = 0; n < result.PathCount; n++) _outputChecksum ^= query.PathNode(n);
                    }
                    double hierarchical = Elapsed(before);
                    before = Stopwatch.GetTimestamp();
                    for (int i = 0; i < count; i++)
                    {
                        var result = astar.Search(grid, new int2(1, 1 + i % 6), new int2(side - 2, side / 2 - i % 3));
                        if (result.Status != NavigationPathStatus.Success) throw new InvalidOperationException();
                        for (int n = 0; n < result.PathCount; n++) _outputChecksum ^= astar.PathNode(n);
                    }
                    double baseline = Elapsed(before);
                    TestContext.WriteLine(FormattableString.Invariant(
                        $"PH85_AMORTIZE mode={mode} count={count} build={buildMs:F4} batch={hierarchical:F4} total={buildMs+hierarchical:F4} astar={baseline:F4}"));
                }
            }
        }

        private static void Measure(NavigationGrid grid, int side, string layout, int cluster, NavigationPortalMode mode)
        {
            using var builder = new NavigationHierarchyBuilder();
            using var topology = Record("Topology");
            using var distances = Record("Precompute");
            using var count = Record("Count");
            using var pack = Record("Pack");
            long beforeBuild = Read(topology) + Read(distances) + Read(count) + Read(pack);
            long started = Stopwatch.GetTimestamp();
            var state = builder.Begin(grid, new int2(cluster), mode, 4, RtsNavigationHierarchyTests.Budget, true);
            NavigationHierarchyGraph built = null;
            if (state == NavigationHierarchyBuildStatus.Success)
            {
                while (!builder.TryFinish(grid, out state, out built))
                {
                    if (Elapsed(started) > 90000) Assert.Fail("Hierarchy benchmark exceeded 90 seconds.");
                    Thread.Yield();
                }
            }
            double buildMs = Elapsed(started);
            double buildCpu = (Read(topology) + Read(distances) + Read(count) + Read(pack) - beforeBuild) / 1e6;
            if (state == NavigationHierarchyBuildStatus.BudgetExceeded)
            {
                TestContext.WriteLine(FormattableString.Invariant(
                    $"PH85_BUILD size={side} layout={layout} cluster={cluster} mode={mode} rejected=BudgetExceeded build={buildMs:F4}"));
                return;
            }
            Assert.That(state, Is.EqualTo(NavigationHierarchyBuildStatus.Success));
            using var hierarchy = built;
            var stats = hierarchy.Stats;
            using var query = new NavigationHierarchyPathfinder(hierarchy, side * side, RtsNavigationHierarchyTests.Budget);
            using var astar = new NavigationPathfinder(side * side);
            TestContext.WriteLine(FormattableString.Invariant(
                $"PH85_BUILD size={side} layout={layout} cluster={cluster} mode={mode} build={buildMs:F4} cpu={buildCpu:F4} clusters={stats.Clusters} entrances={stats.Entrances} portal={stats.Portals} edges={stats.Edges} crossings={stats.CrossEdges}/{stats.CrossCandidates} graph={hierarchy.PayloadBytes} temp={stats.TemporaryBytes} peak={stats.PeakBytes + grid.PayloadBytes} workspace={query.PayloadBytes} maxPortals={stats.MaxPortals} maxClusterWork={stats.MaxClusterExpanded} laneMin={stats.MinLaneExpanded} laneMax={stats.MaxLaneExpanded} expanded={stats.Expanded}"));

            using var connect = Record("Connect");
            using var high = Record("Abstract");
            using var refine = Record("Refine");
            using var stitch = Record("Stitch");
            using var validate = Record("Validate");

            foreach (var workload in new[] { "near", "medium", "far" })
            {
                int dx = workload == "near" ? 4 : workload == "medium" ? side / 3 : side - 3;
                int dz = workload == "near" ? 2 : workload == "medium" ? side / 6 : side / 2;
                var endToEnd = new double[Samples];
                var baselineTimes = new double[Samples];
                var quality = new double[Samples];
                int qualityCount = 0, fallbacks = 0, expandConnect = 0, expandHigh = 0, expandRefine = 0;
                double extra = 0, fallbackMs = 0;
                double normalWall = 0;
                int normalCount = 0;
                int startX = workload == "near" ? side / 4 : 1;
                var warmStart = new int2(startX, 1);
                var warmGoal = warmStart + new int2(dx, dz);
                RunUntimed(query, grid, hierarchy, warmStart, warmGoal);
                astar.Search(grid, warmStart, warmGoal);
                long connect0 = Read(connect), high0 = Read(high), refine0 = Read(refine);
                long stitch0 = Read(stitch), validate0 = Read(validate);
                for (int i = 0; i < Samples; i++)
                {
                    var start = new int2(startX, 1 + i % 6);
                    var goal = start + new int2(dx, dz - i % Math.Min(3, dz));
                    long baselineStart = Stopwatch.GetTimestamp();
                    var reference = astar.Search(grid, start, goal);
                    for (int n = 0; n < reference.PathCount; n++) _outputChecksum ^= astar.PathNode(n);
                    baselineTimes[i] = Elapsed(baselineStart);

                    long queryStart = Stopwatch.GetTimestamp();
                    var result = RunUntimed(query, grid, hierarchy, start, goal);
                    for (int n = 0; n < result.PathCount; n++) _outputChecksum ^= query.PathNode(n);
                    endToEnd[i] = Elapsed(queryStart);

                    expandConnect += result.ConnectExpanded;
                    expandHigh += result.HighExpanded;
                    expandRefine += result.RefineExpanded;
                    if (result.Fallback == HierarchyFallback.FullAStar)
                    {
                        fallbacks++;
                        fallbackMs += result.FallbackMilliseconds;
                    }
                    else
                    {
                        normalWall += endToEnd[i];
                        normalCount++;
                    }

                    if (reference.Status == NavigationPathStatus.Unreachable)
                    {
                        Assert.That(result.Status, Is.EqualTo(NavigationHierarchyStatus.Unreachable));
                        continue;
                    }
                    Assert.That(result.Status, Is.EqualTo(NavigationHierarchyStatus.Success));
                    if (mode == NavigationPortalMode.Exhaustive)
                        Assert.That(result.Cost, Is.EqualTo(reference.Cost).Within(1e-9 * Math.Max(1, reference.Cost)));
                    double difference = Math.Max(0, result.Cost - reference.Cost);
                    extra = Math.Max(extra, difference);
                    quality[qualityCount++] = reference.Cost > 0 ? 100 * difference / reference.Cost : 0;
                }

                if (qualityCount > 0) Array.Sort(quality, 0, qualityCount);
                double hpa = Median(endToEnd), a = Median(baselineTimes);
                double delta = a - hpa;
                double breakEven = delta > 0 ? Math.Ceiling(buildMs / delta) : -1;
                TestContext.WriteLine(FormattableString.Invariant(
                    $"PH85_QUERY size={side} layout={layout} cluster={cluster} mode={mode} distance={workload} hpa={hpa:F5} astar={a:F5} normalMean={normalWall/Math.Max(1,normalCount):F5} connect={(Read(connect)-connect0)/1e6/Samples:F5} high={(Read(high)-high0)/1e6/Samples:F5} refine={(Read(refine)-refine0)/1e6/Samples:F5} stitch={(Read(stitch)-stitch0)/1e6/Samples:F5} validate={(Read(validate)-validate0)/1e6/Samples:F5} expanded={expandConnect/Samples}/{expandHigh/Samples}/{expandRefine/Samples} quality={At(0.5) :F4}/{At(0.95):F4}/{At(1):F4} maxExtra={extra:F6} fallback={fallbacks}/{Samples} fallbackMean={fallbackMs/Math.Max(1,fallbacks):F5} breakEven={breakEven:F0} checksum={_outputChecksum}"));
                double At(double percentile) => qualityCount == 0 ? 0 :
                    quality[Math.Max(0, (int)Math.Ceiling(qualityCount * percentile) - 1)];
            }
        }

        private static NavigationHierarchyResult RunUntimed(NavigationHierarchyPathfinder query,
            NavigationGrid grid, NavigationHierarchyGraph hierarchy, int2 start, int2 goal)
        {
            if (!query.Begin(grid, hierarchy, start, goal, false))
                throw new InvalidOperationException("Unexpected stale hierarchy.");
            if (!query.TryFinish(grid, hierarchy, out var result))
                throw new InvalidOperationException("Synchronous query did not finish.");
            return result;
        }

        private static ProfilerRecorder Record(string name) =>
            ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Navigation.HPA." + name);

        private static long Read(ProfilerRecorder recorder) => recorder.Valid ? recorder.CurrentValue : 0;
        private static double Elapsed(long started) => (Stopwatch.GetTimestamp() - started) * 1000d / Stopwatch.Frequency;

        private static double Median(double[] data)
        {
            Array.Sort(data);
            return (data[(data.Length - 1) / 2] + data[data.Length / 2]) * 0.5;
        }
    }
}
