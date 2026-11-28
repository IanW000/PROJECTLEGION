using System;
using System.Diagnostics;
using NUnit.Framework;
using Unity.Jobs;
using Unity.Mathematics;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsDynamicNavigationPerformanceTests
    {
        [TestCase(24, TopologyStrategy.Full)]
        [TestCase(24, TopologyStrategy.LinkOnly)]
        [TestCase(24, TopologyStrategy.Partial)]
        [TestCase(64, TopologyStrategy.Full)]
        [TestCase(64, TopologyStrategy.LinkOnly)]
        [TestCase(64, TopologyStrategy.Partial)]
        public void MeasureRuntimeTransactions(int side, TopologyStrategy preference)
        {
            using var rig = new RtsDynamicNavigationTests.Fixture(preference, width: side, depth: side);
            Measure(rig, side, preference, "link", turn => rig.Controller.SetPresent(rig.Shortcut, turn));
            Measure(rig, side, preference, "obstacle", turn => rig.Controller.SetPresent(rig.Obstacle, turn));
            Measure(rig, side, preference, "batch", turn =>
            {
                rig.Controller.SetPresent(rig.Obstacle, turn);
                rig.Controller.SetPresent(rig.SecondObstacle, turn);
                rig.Controller.SetPresent(rig.Bridge, !turn);
            });
        }

        private static void Measure(RtsDynamicNavigationTests.Fixture rig, int side, TopologyStrategy preference,
            string scenario, Action<bool> edit)
        {
            var c = rig.Controller;
            for (int round = 0; round < 4; round++)
            {
                c.ClearRoutes();
                rig.Route(new float3(3.5f, 0, 8.5f), new float3(20.5f, 0, 8.5f));
                var timer = Stopwatch.StartNew();
                edit(round % 2 == 0);
                RtsDynamicNavigationTests.Ready(c);
                double elapsed = timer.Elapsed.TotalMilliseconds;
                var stats = c.LastSampling;
                if (round >= 1)
                {
                    timer.Restart();
                    rig.Search(new float3(3.5f, 0, 8.5f), new float3(20.5f, 0, 8.5f));
                    double query = timer.Elapsed.TotalMilliseconds;
                    TestContext.WriteLine(FormattableString.Invariant(
                        $"DYNAMIC,{side},{preference},{scenario},{round},{c.AppliedStrategy},{elapsed:F3},{c.LastPumpMs:F3},{c.LastDirtyMs:F3},{c.LastSyncMs:F3},{stats.ExtractionMs:F3},{stats.ConnectionsMs:F3},{stats.RemapMs:F3},{stats.PackMs:F3},{c.LastPublishMs:F3},{c.LastRevalidateMs:F3},{query:F3},{stats.ResampledColumns},{c.Dirty.RepairCount},{c.PeakBytes},{c.Layered.PayloadBytes},{c.LastReaderWaitMs:F3},{c.LastRetirementMs:F3}"));
                }
                rig.CheckFresh();
            }
        }

        [Test]
        public void SampleNavigationUpdateAlongsideScheduledGameplay()
        {
            using var rig = new RtsDynamicNavigationTests.Fixture(TopologyStrategy.Partial, width: 64, depth: 64);
            using var gameplay = new RtsOverlapResolutionTests.Simulation(withMovement: true, avoidance: true);
            gameplay.Configure(true);
            for (int i = 0; i < 1000; i++)
            {
                var position = new float2(i % 40, i / 40);
                gameplay.Create(position, target: position + new float2(18, 0));
            }
            for (int i = 0; i < 4; i++) gameplay.Step();
            for (int i = 0; i < 12; i++)
            {
                bool editing = i % 2 == 1;
                if (editing) rig.Controller.SetPresent(rig.Obstacle, i % 4 == 1);
                var timer = Stopwatch.StartNew();
                gameplay.BeginStep();
                JobHandle.ScheduleBatchedJobs();
                double schedule = timer.Elapsed.TotalMilliseconds;
                if (editing) RtsDynamicNavigationTests.Ready(rig.Controller);
                double beforeWait = timer.Elapsed.TotalMilliseconds;
                gameplay.FinishStep();
                TestContext.WriteLine(FormattableString.Invariant(
                    $"DYNAMIC_GAMEPLAY,{editing},{schedule:F3},{beforeWait:F3},{timer.Elapsed.TotalMilliseconds:F3},{rig.Controller.LastPumpMs:F3}"));
            }
        }
    }
}
