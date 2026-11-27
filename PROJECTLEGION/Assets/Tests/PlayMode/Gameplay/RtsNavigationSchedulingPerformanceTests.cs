using System;
using System.Collections;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine.TestTools;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsNavigationSchedulingPerformanceTests
    {
        private const long WorkspaceBudget = 64L * 1024 * 1024;

        [TestCase(100, 1)] [TestCase(100, 2)] [TestCase(100, 4)] [TestCase(100, 8)] [TestCase(100, 16)]
        [TestCase(1000, 1)] [TestCase(1000, 2)] [TestCase(1000, 4)] [TestCase(1000, 8)] [TestCase(1000, 16)]
        [TestCase(10000, 1)] [TestCase(10000, 2)] [TestCase(10000, 4)] [TestCase(10000, 8)] [TestCase(10000, 16)]
        public void ThroughputScale(int requests, int workers) => Throughput(64, requests, workers, true);

        [TestCase(128, 4, false)]
        [TestCase(128, 8, true)]
        [TestCase(256, 4, true)]
        [TestCase(256, 16, true)]
        public void ThroughputDistanceProfiles(int side, int workers, bool split) =>
            Throughput(side, 1000, workers, split);

        [UnityTest]
        public IEnumerator GameplayAt60Hz() => FrameComparison(60);

        [UnityTest]
        public IEnumerator GameplayAt120Hz() => FrameComparison(120);

        [Test]
        public void SupersessionStormKeepsFixedMemoryAndLatestResultOnly()
        {
            using var grid = RtsNavigationSchedulingTests.Grid(256, 256, true);
            using var scheduler = new NavigationPathScheduler(4, WorkspaceBudget, 8, 4, 256);
            scheduler.SetGrid(grid);
            scheduler.Submit(int2.zero, new int2(255, 127), out var token);
            scheduler.Tick(4, 4, 1024);
            long payload = scheduler.PayloadBytes;
            long begin = Stopwatch.GetTimestamp();
            for (int i = 0; i < 5000; i++)
            {
                int2 goal = i == 4999 ? new int2(7, 3) : new int2(255, 127);
                Assert.That(scheduler.Replace(token, int2.zero, goal, out var next), Is.EqualTo(NavigationSubmit.Accepted));
                Assert.That(scheduler.Cancel(token), Is.False);
                token = next;
                if (i % 250 == 0)
                    scheduler.Tick(4, 4, 1024);
            }

            NavigationScheduledResult result;
            while (!scheduler.TryTake(out result))
            {
                Assert.That(Ms(Stopwatch.GetTimestamp() - begin), Is.LessThan(10000));
                scheduler.Tick(4, 4, 1024);
                Thread.Yield();
            }

            Assert.That(result.Handle.Request.Equals(token), Is.True);
            Assert.That(scheduler.Cancelled, Is.EqualTo(5000));
            Assert.That(scheduler.PayloadBytes, Is.EqualTo(payload));
            Assert.That(scheduler.Queued, Is.Zero);
            scheduler.Release(result.Handle);
            while (scheduler.Active > 0)
            {
                Assert.That(Ms(Stopwatch.GetTimestamp() - begin), Is.LessThan(10000));
                scheduler.Tick(4, 4, 1024);
                Thread.Yield();
            }

            TestContext.WriteLine(FormattableString.Invariant(
                $"PH83_REPLACE n=5000 payload={payload} validLatency={Ms(result.LatencyTicks):F4} validQueue={Ms(result.QueuedTicks):F4} logicalMax={Ms(scheduler.MaxLogicalCancelTicks):F4} releaseMean={Ms(scheduler.SumActiveReleaseTicks) / Math.Max(1, scheduler.ActiveReleaseSamples):F4} releaseMax={Ms(scheduler.MaxActiveReleaseTicks):F4} releaseSamples={scheduler.ActiveReleaseSamples}"));
        }

        [Test]
        public void ReportFinalSynchronizationSeparately()
        {
            using var grid = RtsNavigationSchedulingTests.Grid(256, 256, true);
            using var scheduler = new NavigationPathScheduler(8, WorkspaceBudget, 8, 8, 32);
            scheduler.SetGrid(grid);
            for (int i = 0; i < 8; i++)
                scheduler.Submit(int2.zero, new int2(255, 127), out _);
            scheduler.Tick(8, 8, 64);
            scheduler.Dispose();
            TestContext.WriteLine(FormattableString.Invariant($"PH83_TEARDOWN active=8 wait={Ms(scheduler.FinalWaitTicks):F4}ms"));
        }

        private static void Throughput(int side, int requests, int workers, bool split)
        {
            long start = Stopwatch.GetTimestamp();
            using var grid = RtsNavigationSchedulingTests.Grid(side, side, split);
            double build = Ms(Stopwatch.GetTimestamp() - start);
            using var run = new Batch(grid, requests, workers, split);
            using var recorder = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Navigation.AStar.Search");
            long searchBefore = recorder.Valid ? recorder.CurrentValue : -1;
            long garbageBefore = GC.GetAllocatedBytesForCurrentThread();
            int pumps = 0;
            start = Stopwatch.GetTimestamp();
            while (run.Done < requests)
            {
                run.Step();
                if ((++pumps & 255) == 0 && Ms(Stopwatch.GetTimestamp() - start) > 30000)
                    Assert.Fail("Batch throughput measurement exceeded 30 seconds.");
                Thread.Yield();
            }

            double elapsed = Ms(Stopwatch.GetTimestamp() - start);
            long garbage = GC.GetAllocatedBytesForCurrentThread() - garbageBefore;
            long workerNs = recorder.Valid ? recorder.CurrentValue - searchBefore : -1;
            Assert.That(run.WrongResults, Is.Zero);
            TestContext.WriteLine(FormattableString.Invariant(
                $"PH83_FAST grid={side} requests={requests} workers={workers} actual={run.Scheduler.Concurrency} split={split} elapsed={elapsed:F3} rps={requests * 1000 / elapsed:F1} workerMs={workerNs / 1e6:F3} workerNsValid={workerNs > 0} build={build:F3} pumps={pumps} mainTotal={Ms(run.MainTicks):F4} mainAvg={Ms(run.MainTicks) / pumps:F5} dispatchAvg={Ms(run.DispatchTicks) / pumps:F5} collectAvg={Ms(run.CollectTicks) / pumps:F5} mainMax={Ms(run.MainMax):F4} payload={run.Scheduler.PeakPayloadBytes + grid.PayloadBytes} gc={garbage} shortLatency={Describe(run.ShortLatency)} longLatency={Describe(run.LongLatency)} shortQueue={Describe(run.ShortQueue)} longQueue={Describe(run.LongQueue)}"));
        }

        private static IEnumerator FrameComparison(int hz)
        {
            foreach (bool mixed in new[] { false, true })
            {
                const int requests = 600;
                using var grid = RtsNavigationSchedulingTests.Grid(128, 128, true);
                using var batch = new Batch(grid, requests, 8, true);
                using var simulation = new RtsOverlapResolutionTests.Simulation(withMovement: true, avoidance: true);
                simulation.Configure(true);
                for (int i = 0; i < 1000; i++)
                {
                    var position = new float2((i % 40) * 0.8f, (i / 40) * 0.8f);
                    simulation.Create(position, true, position + new float2(200, 0));
                }

                for (int i = 0; i < 6; i++)
                {
                    simulation.Step(1f / hz);
                    yield return null;
                }

                const int frames = 96;
                var gameplay = new long[frames];
                var pump = new long[frames];
                var dispatch = new long[frames];
                var collect = new long[frames];
                var periods = new long[frames];
                using var worker = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Navigation.AStar.Search", frames + 4);
                long start = Stopwatch.GetTimestamp(), previousFrame = start;
                long step = Stopwatch.Frequency / hz;
                long mainAllocations = 0;
                for (int frame = 0; frame < frames; frame++)
                {
                    long deadline = start + frame * step;
                    WaitUntil(deadline);
                    long frameStart = Stopwatch.GetTimestamp();
                    periods[frame] = frame == 0 ? step : frameStart - previousFrame;
                    previousFrame = frameStart;
                    if (mixed)
                    {
                        long before = GC.GetAllocatedBytesForCurrentThread();
                        long begin = Stopwatch.GetTimestamp();
                        batch.Step();
                        pump[frame] = Stopwatch.GetTimestamp() - begin;
                        dispatch[frame] = batch.Scheduler.SchedulingTicks;
                        collect[frame] = batch.Scheduler.CollectionTicks;
                        mainAllocations += GC.GetAllocatedBytesForCurrentThread() - before;
                    }

                    long gameStart = Stopwatch.GetTimestamp();
                    simulation.Step(1f / hz);
                    gameplay[frame] = Stopwatch.GetTimestamp() - gameStart;
                    yield return null;
                }

                double elapsed = Ms(Stopwatch.GetTimestamp() - start);
                long workerNs = -1;
                if (worker.Valid)
                {
                    workerNs = 0;
                    foreach (var sample in worker.ToArray())
                        workerNs += sample.Value;
                }

                if (mixed)
                {
                    Assert.That(batch.Done, Is.EqualTo(requests));
                    Assert.That(batch.WrongResults, Is.Zero);
                }

                TestContext.WriteLine(FormattableString.Invariant(
                    $"PH83_FRAME hz={hz} mixed={mixed} frames={frames} requests={batch.Done} actual={batch.Scheduler.Concurrency} elapsed={elapsed:F3} workerMs={workerNs / 1e6:F4} workerNsValid={workerNs > 0} period={Describe(periods)} gameplay={Describe(gameplay)} main={Describe(pump)} dispatch={Describe(dispatch)} collect={Describe(collect)} payload={batch.Scheduler.PeakPayloadBytes + grid.PayloadBytes} gc={mainAllocations} shortLatency={Describe(batch.ShortLatency)} longLatency={Describe(batch.LongLatency)} shortQueue={Describe(batch.ShortQueue)} longQueue={Describe(batch.LongQueue)}"));
            }
        }

        private static void WaitUntil(long deadline)
        {
            while (true)
            {
                long remaining = deadline - Stopwatch.GetTimestamp();
                if (remaining <= 0)
                    return;
                if (remaining > Stopwatch.Frequency / 500)
                    Thread.Sleep(1);
                else
                    Thread.Yield();
            }
        }

        private static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;

        // mean / P95 / max, in milliseconds
        private static string Describe(long[] ticks)
        {
            if (ticks.Length == 0)
                return "0/0/0";
            Array.Sort(ticks);
            long total = 0;
            foreach (long value in ticks) total += value;
            return FormattableString.Invariant(
                $"{Ms(total) / ticks.Length:F4}/{Ms(ticks[(int)Math.Ceiling(ticks.Length * 0.95) - 1]):F4}/{Ms(ticks[ticks.Length - 1]):F4}");
        }

        private sealed class Batch : IDisposable
        {
            private readonly NavigationGrid _grid;
            private readonly int _total;
            private readonly bool _split;
            private int _submitted, _short, _long;
            internal readonly NavigationPathScheduler Scheduler;
            internal readonly long[] ShortLatency, LongLatency, ShortQueue, LongQueue;
            internal int Done => _short + _long;
            internal int WrongResults;
            internal long MainTicks, MainMax, DispatchTicks, CollectTicks;

            internal Batch(NavigationGrid grid, int count, int workers, bool split)
            {
                _grid = grid;
                _total = count;
                _split = split;
                LongLatency = new long[(count + 9) / 10];
                LongQueue = new long[LongLatency.Length];
                ShortLatency = new long[count - LongLatency.Length];
                ShortQueue = new long[ShortLatency.Length];
                Scheduler = new NavigationPathScheduler(workers, WorkspaceBudget, 128, 32, 2048);
                Scheduler.SetGrid(grid);
                Scheduler.Submit(int2.zero, new int2(8, 4), out _);
                NavigationScheduledResult warm;
                long deadline = Stopwatch.GetTimestamp() + 8 * Stopwatch.Frequency;
                while (!Scheduler.TryTake(out warm))
                {
                    if (Stopwatch.GetTimestamp() > deadline) Assert.Fail("Timed out warming the search.");
                    Scheduler.Tick(workers, workers, 8192);
                    Thread.Yield();
                }
                Scheduler.Release(warm.Handle);
            }

            internal void Step()
            {
                long begin = Stopwatch.GetTimestamp();
                for (int i = 0; i < 32 && _submitted < _total; i++)
                {
                    int tag = _submitted % 10 == 0 ? 1 : 0;
                    int2 start = new int2(0, _submitted % 5);
                    int2 goal = tag == 0 ? start + new int2(8, 4) : new int2(_grid.Size.x - 1, _grid.Size.y / 2);
                    if (Scheduler.Submit(start, goal, out _, tag) != NavigationSubmit.Accepted)
                        break;
                    _submitted++;
                }

                Scheduler.Tick(16, 16, 8192);
                DispatchTicks += Scheduler.SchedulingTicks;
                CollectTicks += Scheduler.CollectionTicks;
                while (Scheduler.TryTake(out var result))
                {
                    var expected = _split && result.Tag != 0 ? NavigationPathStatus.Unreachable : NavigationPathStatus.Success;
                    if (result.Path.Status != expected)
                        WrongResults++;
                    if (result.Tag == 0)
                    {
                        ShortLatency[_short] = result.LatencyTicks;
                        ShortQueue[_short++] = result.QueuedTicks;
                    }
                    else
                    {
                        LongLatency[_long] = result.LatencyTicks;
                        LongQueue[_long++] = result.QueuedTicks;
                    }
                    Scheduler.Release(result.Handle);
                }
                long ticks = Stopwatch.GetTimestamp() - begin;
                MainTicks += ticks;
                MainMax = Math.Max(MainMax, ticks);
            }

            public void Dispose() => Scheduler.Dispose();
        }
    }
}
