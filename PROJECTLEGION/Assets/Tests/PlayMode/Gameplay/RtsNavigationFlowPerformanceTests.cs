using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsNavigationFlowPerformanceTests
    {
        private enum Delivery { Next, Continuous, Full }
        private const int Repeats = 5;

        [TestCase("open")]
        [TestCase("weighted")]
        [TestCase("gate")]
        [TestCase("split")]
        public void ReportConstructionBySize(string layout)
        {
            foreach (int side in new[] { 64, 128, 256, 512 })
            {
                long begin = Stopwatch.GetTimestamp();
                using var grid = RtsNavigationFlowFieldTests.MakeGraph(side, layout);
                double graphMs = Elapsed(begin);
                using var builder = new NavigationFlowFieldBuilder(side * side);
                var goal = new int2(side - 1);
                using (RtsNavigationFlowFieldTests.Build(builder, grid, goal, true)) { }
                using var integration = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Navigation.Flow.Integration");
                using var directions = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Navigation.Flow.Direction");
                var wall = new double[Repeats];
                var dispatch = new double[Repeats];
                var publish = new double[Repeats];
                var integrationCpu = new double[Repeats];
                var directionCpu = new double[Repeats];
                int reachable = 0;
                long fieldBytes = 0;
                long garbageBefore = GC.GetAllocatedBytesForCurrentThread();
                for (int iteration = 0; iteration < Repeats; iteration++)
                {
                    long cpuIntegration = integration.Valid ? integration.CurrentValue : -1;
                    long cpuDirection = directions.Valid ? directions.CurrentValue : -1;
                    begin = Stopwatch.GetTimestamp();
                    builder.Begin(grid, goal, true);
                    dispatch[iteration] = Elapsed(begin);
                    WaitForBuild(builder);
                    long publishStart = Stopwatch.GetTimestamp();
                    bool done = builder.TryFinish(grid, out var status, out var field);
                    publish[iteration] = Elapsed(publishStart);
                    wall[iteration] = Elapsed(begin);
                    integrationCpu[iteration] = cpuIntegration >= 0 ? (integration.CurrentValue - cpuIntegration) * 1e-6 : -1;
                    directionCpu[iteration] = cpuDirection >= 0 ? (directions.CurrentValue - cpuDirection) * 1e-6 : -1;
                    if (!done || status != NavigationFlowBuildStatus.Success) Assert.Fail("Flow build failed.");
                    reachable = field.Stats.Reachable;
                    fieldBytes = field.PayloadBytes;
                    field.Dispose();
                }

                long garbage = GC.GetAllocatedBytesForCurrentThread() - garbageBefore;
                TestContext.WriteLine(FormattableString.Invariant(
                    $"PH84_BUILD size={side} layout={layout} graph={graphMs:F4} wall={Median(wall):F4} integrationCpu={Median(integrationCpu):F4} directionCpu={Median(directionCpu):F4} schedule={Median(dispatch):F4} publish={Median(publish):F4} reachable={reachable} fieldBytes={fieldBytes} workspaceBytes={builder.PayloadBytes} managedBuilds={garbage} nativeOutputPerBuild={fieldBytes}"));
            }
        }

        [Test]
        public void ReportDirectionQueriesFlowsAndMaterializedPaths()
        {
            const int side = 128;
            using var grid = RtsNavigationFlowFieldTests.MakeGraph(side, "gate");
            using var builder = new NavigationFlowFieldBuilder(side * side);
            using var field = RtsNavigationFlowFieldTests.Build(builder, grid, new int2(side - 1), true);
            field.TryGetView(grid, out var view);
            using var starts = new NativeArray<int2>(10000, Allocator.Persistent);
            using var scratch = new NativeArray<int>(side * side, Allocator.Persistent);
            using var written = new NativeArray<long>(1, Allocator.Persistent);
            Fill(starts, false, side);
            foreach (int count in new[] { 100, 1000, 10000 })
            foreach (Delivery delivery in Enum.GetValues(typeof(Delivery)))
            {
                var job = new QueryJob { View = view, Starts = starts, Count = count, Result = written, Path = scratch, Mode = delivery };
                job.Run();
                var repetitions = new double[Repeats];
                long garbageBefore = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < Repeats; i++)
                {
                    long begin = Stopwatch.GetTimestamp();
                    job.Run();
                    repetitions[i] = Elapsed(begin);
                }
                long garbage = GC.GetAllocatedBytesForCurrentThread() - garbageBefore;
                double ms = Median(repetitions);
                Assert.That(written[0], Is.GreaterThan(0));
                TestContext.WriteLine(FormattableString.Invariant(
                    $"PH84_QUERY count={count} mode={delivery} median={ms:F4} consumersPerSec={count * 1000 / ms:F0} steps={written[0]} managed={garbage} nativeNew=0"));
            }
        }

        [TestCase(true)]
        [TestCase(false)]
        public void CompareSharedDestinations(bool near)
        {
            const int side = 128;
            using var grid = RtsNavigationFlowFieldTests.MakeGraph(side, "open");
            using var builder = new NavigationFlowFieldBuilder(side * side);
            using var synchronous = new NavigationPathfinder(side * side);
            using var scheduler = new NavigationPathScheduler(4, 32L * 1024 * 1024, 64, 8, 512);
            scheduler.SetGrid(grid);
            var goal = new int2(side - 1);
            using var starts = new NativeArray<int2>(4096, Allocator.Persistent);
            Fill(starts, near, side);
            using var scratch = new NativeArray<int>(side * side, Allocator.Persistent);
            using var written = new NativeArray<long>(1, Allocator.Persistent);
            var firstStep = new int[2];
            using (RtsNavigationFlowFieldTests.Build(builder, grid, goal, true)) { }
            synchronous.Search(grid, starts[0], goal);
            ParallelBatch(scheduler, starts, 8, goal, scratch, firstStep, Delivery.Full);

            var buildTimes = new double[Repeats];
            for (int i = 0; i < Repeats; i++)
            {
                long begin = Stopwatch.GetTimestamp();
                using var temp = CompleteBuild(builder, grid, goal);
                buildTimes[i] = Elapsed(begin);
            }
            double build = Median(buildTimes);
            using var field = CompleteBuild(builder, grid, goal);
            field.TryGetView(grid, out var view);
            using var searchCpu = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Navigation.AStar.Search");
            TestContext.WriteLine(FormattableString.Invariant(
                $"PH84_SHARED_MEMORY near={near} flowPeak={grid.PayloadBytes + field.PayloadBytes + builder.PayloadBytes} syncPeak={grid.PayloadBytes + synchronous.PayloadBytes} parallelPeak={grid.PayloadBytes + scheduler.PeakPayloadBytes} callerScratch={scratch.Length * sizeof(int)}"));

            foreach (int count in near ? new[] { 1, 4, 16, 64, 256, 1024, 4096 } : new[] { 1, 4, 16, 64, 256 })
            foreach (Delivery delivery in Enum.GetValues(typeof(Delivery)))
            {
                var query = new QueryJob { View = view, Starts = starts, Count = count, Result = written, Path = scratch, Mode = delivery };
                query.Run();
                var flow = new double[3];
                var serial = new double[3];
                var parallel = new double[3];
                var serialCpu = new double[3];
                var parallelCpu = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    long begin = Stopwatch.GetTimestamp();
                    query.Run();
                    flow[i] = Elapsed(begin);
                    long before = searchCpu.Valid ? searchCpu.CurrentValue : -1;
                    begin = Stopwatch.GetTimestamp();
                    long serialCheck = SerialBatch(synchronous, grid, starts, count, goal, scratch, delivery);
                    serial[i] = Elapsed(begin);
                    serialCpu[i] = before < 0 ? -1 : (searchCpu.CurrentValue - before) * 1e-6;
                    before = searchCpu.Valid ? searchCpu.CurrentValue : -1;
                    begin = Stopwatch.GetTimestamp();
                    long parallelCheck = ParallelBatch(scheduler, starts, count, goal, scratch, firstStep, delivery);
                    parallel[i] = Elapsed(begin);
                    parallelCpu[i] = before < 0 ? -1 : (searchCpu.CurrentValue - before) * 1e-6;
                    if (serialCheck <= 0 || parallelCheck <= 0 || written[0] <= 0)
                        Assert.Fail("Workload produced an empty result.");
                }
                double queryMedian = Median(flow);
                TestContext.WriteLine(FormattableString.Invariant(
                    $"PH84_SHARED near={near} count={count} mode={delivery} flowBuild={build:F4} flowUse={queryMedian:F4} flowTotal={build + queryMedian:F4} serial={Median(serial):F4} parallel={Median(parallel):F4} serialJobCpu={Median(serialCpu):F4} parallelWorkerCpu={Median(parallelCpu):F4}"));
            }
        }

        private static NavigationFlowField CompleteBuild(NavigationFlowFieldBuilder builder, NavigationGrid grid, int2 goal)
        {
            builder.Begin(grid, goal, true);
            WaitForBuild(builder);
            if (builder.TryFinish(grid, out var status, out var field) && status == NavigationFlowBuildStatus.Success)
                return field;
            throw new InvalidOperationException("Could not build shared field.");
        }

        private static void WaitForBuild(NavigationFlowFieldBuilder builder)
        {
            long deadline = Stopwatch.GetTimestamp() + 15 * Stopwatch.Frequency;
            while (!builder.IsCompleted)
            {
                if (Stopwatch.GetTimestamp() > deadline) Assert.Fail("Flow Field measurement timed out.");
                Thread.Yield();
            }
        }

        private static long SerialBatch(NavigationPathfinder astar, NavigationGrid grid, NativeArray<int2> starts,
            int count, int2 goal, NativeArray<int> scratch, Delivery mode)
        {
            long checksum = 0;
            for (int i = 0; i < count; i++)
            {
                var result = astar.Search(grid, starts[i], goal);
                if (result.Status != NavigationPathStatus.Success) throw new InvalidOperationException("Unexpected serial failure.");
                int length = mode == Delivery.Next ? Math.Min(2, result.PathCount) : result.PathCount;
                for (int n = mode == Delivery.Next ? 1 : 0; n < length; n++)
                {
                    int node = astar.PathNode(n);
                    if (mode == Delivery.Full) scratch[n] = node;
                    checksum += node + 1;
                }
            }
            return checksum;
        }

        private static long ParallelBatch(NavigationPathScheduler scheduler, NativeArray<int2> starts, int count,
            int2 goal, NativeArray<int> scratch, int[] first, Delivery mode)
        {
            int submitted = 0, completed = 0;
            long checksum = 0;
            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 15;
            while (completed < count)
            {
                while (submitted < count && scheduler.Submit(starts[submitted], goal, out _) == NavigationSubmit.Accepted)
                    submitted++;
                scheduler.Tick(4, 4, 8192);
                while (scheduler.TryTake(out var result))
                {
                    if (result.Path.Status != NavigationPathStatus.Success ||
                        result.Status != NavigationCompletionStatus.SearchFinished)
                        throw new InvalidOperationException("Unexpected parallel failure.");
                    if (mode == Delivery.Next)
                    {
                        int length = scheduler.CopyPathPrefix(result.Handle, first);
                        if (length > 1) checksum += first[1] + 1;
                    }
                    else
                    {
                        if (!scheduler.CopyPath(result.Handle, scratch)) throw new InvalidOperationException("Missing parallel path.");
                        for (int n = 0; n < result.Path.PathCount; n++) checksum += scratch[n] + 1;
                    }
                    scheduler.Release(result.Handle);
                    completed++;
                }
                if (Stopwatch.GetTimestamp() > deadline) Assert.Fail("Parallel measurement did not finish.");
                Thread.Yield();
            }
            return checksum;
        }

        private static void Fill(NativeArray<int2> starts, bool near, int side)
        {
            var random = new Unity.Mathematics.Random(8401);
            for (int i = 0; i < starts.Length; i++)
            {
                starts[i] = near
                    ? new int2(side - 1 - random.NextInt(1, 9), side - 1 - random.NextInt(1, 9))
                    : new int2(random.NextInt(0, side / 4), random.NextInt(0, side));
            }
        }

        [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
        private struct QueryJob : IJob
        {
            internal NavigationFlowView View;
            [ReadOnly] internal NativeArray<int2> Starts;
            internal NativeArray<int> Path;
            internal NativeArray<long> Result;
            internal int Count;
            internal Delivery Mode;

            public void Execute()
            {
                long steps = 0, checksum = 0;
                for (int request = 0; request < Count; request++)
                {
                    int2 cell = Starts[request];
                    int length = 0;
                    if (Mode == Delivery.Full) Path[length++] = cell.y * View.Size.x + cell.x;
                    while (View.TryGetNext(cell, out var next))
                    {
                        steps++;
                        checksum += next.y * View.Size.x + next.x + 1;
                        if (Mode == Delivery.Full) Path[length++] = next.y * View.Size.x + next.x;
                        cell = next;
                        if (Mode == Delivery.Next) break;
                    }
                }
                Result[0] = steps;
                // Consume the query output while keeping a separate count of actual lookups along the paths.
                if (checksum == 0) Result[0] = 0;
            }
        }

        private static double Elapsed(long start) => (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;

        private static double Median(double[] samples)
        {
            Array.Sort(samples);
            return samples[samples.Length / 2];
        }
    }
}
