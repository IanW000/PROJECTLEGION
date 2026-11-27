using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using Unity.Mathematics;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsNavigationSchedulingTests
    {
        [TestCase(1)]
        [TestCase(4)]
        public void MatchesSynchronousResultsAndRetainsPathAcrossWorkspaceReuse(int concurrency)
        {
            using var grid = Grid(32, 16, true);
            using var scheduler = new NavigationPathScheduler(concurrency, 2_000_000, 32, 8, 64);
            using var synchronous = new NavigationPathfinder(32 * 16);
            using var nodes = new NativeArray<int>(512, Allocator.Persistent);
            scheduler.SetGrid(grid);
            var goals = new[] { new int2(8, 10), new int2(31, 15), new int2(-1), new int2(15, 15) };
            var starts = new[] { int2.zero, int2.zero, int2.zero, new int2(-1) };
            for (int i = 0; i < goals.Length; i++)
                Assert.That(scheduler.Submit(starts[i], goals[i], out _, i), Is.EqualTo(NavigationSubmit.Accepted));
            NavigationScheduledResult retained = default;
            for (int i = 0; i < goals.Length; i++)
            {
                var item = Take(scheduler);
                var expected = synchronous.Search(grid, starts[item.Tag], goals[item.Tag]);
                Assert.That(item.Path.Status, Is.EqualTo(expected.Status));
                Assert.That(item.Path.Cost, Is.EqualTo(expected.Cost));
                Assert.That(item.Path.PathCount, Is.EqualTo(expected.PathCount));
                if (item.Path.Status == NavigationPathStatus.Success)
                {
                    Assert.That(scheduler.CopyPath(item.Handle, nodes), Is.True);
                    for (int j = 0; j < expected.PathCount; j++)
                        Assert.That(nodes[j], Is.EqualTo(synchronous.PathNode(j)));
                    retained = item;
                }
                else
                    Assert.That(scheduler.Release(item.Handle), Is.True);
            }

            Assert.That(scheduler.Submit(int2.zero, new int2(1), out _), Is.EqualTo(NavigationSubmit.Accepted));
            var extra = Take(scheduler);
            Assert.That(scheduler.Release(extra.Handle), Is.True);
            Assert.That(scheduler.CopyPath(retained.Handle, nodes), Is.True);
            Assert.That(nodes[retained.Path.PathCount - 1], Is.EqualTo(10 * 32 + 8));
            Assert.That(scheduler.Release(retained.Handle), Is.True);
            Assert.That(scheduler.CopyPath(retained.Handle, nodes), Is.False);
        }

        [TestCase("cancel")]
        [TestCase("replace")]
        [TestCase("invalidate")]
        public void ReclaimsEntireReservationDuringSegmentedCopy(string operation)
        {
            using var oldGrid = Grid(70, 1, false);
            using var newGrid = Grid(80, 1, false);
            using var scheduler = new NavigationPathScheduler(2, 1_000_000, 4, 4, 3);
            scheduler.SetGrid(oldGrid);
            scheduler.Submit(int2.zero, new int2(69, 0), out var original);
            scheduler.Submit(int2.zero, new int2(69, 0), out var pending);
            scheduler.Tick(1, 0, 0);
            long deadline = Stopwatch.GetTimestamp() + 8 * Stopwatch.Frequency;
            While(() => scheduler.ResultBlocksUsed == 0, () => scheduler.Tick(0, 2, 1), deadline);
            scheduler.Tick(1, 0, 0);
            Assert.That(scheduler.ResultBlocksUsed, Is.EqualTo(3));
            Assert.That(scheduler.TryTake(out _), Is.False);

            if (operation == "invalidate")
            {
                var retired = oldGrid.Retire();
                scheduler.SetGrid(newGrid);
                While(() => !retired.IsCompleted, ThreadYield, deadline);
                retired.Complete();
            }
            else
            {
                bool inPlace = scheduler.Cancel(pending);
                Assert.That(inPlace, Is.True);
                if (operation == "replace")
                    Assert.That(scheduler.Replace(original, int2.zero, new int2(2, 0), out var replacement),
                        Is.EqualTo(NavigationSubmit.Accepted));
                else
                    Assert.That(scheduler.Cancel(original), Is.True);
            }

            Assert.That(scheduler.ResultBlocksUsed, Is.Zero);
            Assert.That(scheduler.Cancel(original), Is.False);
            if (operation != "replace")
                scheduler.Submit(int2.zero, new int2(2, 0), out _);
            var next = Take(scheduler);
            Assert.That(next.Path.PathCount, Is.EqualTo(3));
            Assert.That(scheduler.Release(next.Handle), Is.True);
            Assert.That(scheduler.ResultBlocksUsed, Is.Zero);
        }

        [Test]
        public void FullQueuePreservesActiveTokenAndReplacementsMoveToTheTail()
        {
            using var grid = Grid(128, 20, true);
            using var scheduler = new NavigationPathScheduler(1, 1_000_000, 2, 1, 4);
            scheduler.SetGrid(grid);
            Assert.That(scheduler.Submit(int2.zero, new int2(127, 19), out var active), Is.EqualTo(NavigationSubmit.Accepted));
            scheduler.Tick(1, 1, 128);
            Assert.That(scheduler.Active, Is.EqualTo(1));
            scheduler.Submit(int2.zero, new int2(1, 0), out var first, 1);
            scheduler.Submit(int2.zero, new int2(2, 0), out var second, 2);
            Assert.That(scheduler.Submit(int2.zero, int2.zero, out _), Is.EqualTo(NavigationSubmit.Full));
            Assert.That(scheduler.Replace(active, int2.zero, int2.zero, out _), Is.EqualTo(NavigationSubmit.Full));
            Assert.That(scheduler.Cancel(active), Is.True);
            Assert.That(scheduler.Replace(first, int2.zero, new int2(3, 0), out var revised, 3),
                Is.EqualTo(NavigationSubmit.Accepted));
            Assert.That(revised.Id, Is.EqualTo(first.Id));
            Assert.That(revised.Revision, Is.GreaterThan(first.Revision));
            Assert.That(scheduler.Cancel(first), Is.False);

            var earlier = Take(scheduler);
            Assert.That(earlier.Handle.Request.Equals(second), Is.True);
            scheduler.Release(earlier.Handle);
            var later = Take(scheduler);
            Assert.That(later.Handle.Request.Equals(revised), Is.True);
            Assert.That(scheduler.ActiveReleaseSamples, Is.EqualTo(1));
        }

        [Test]
        public void ResultBackpressureAndForeignOrStaleHandlesAreSafe()
        {
            using var grid = Grid(70, 1, false);
            using var first = new NavigationPathScheduler(2, 1_000_000, 4, 3, 1);
            using var foreign = new NavigationPathScheduler(1, 1_000_000, 4, 3, 1);
            using var nodes = new NativeArray<int>(32, Allocator.Persistent);
            first.SetGrid(grid);
            foreign.SetGrid(grid);
            first.Submit(int2.zero, new int2(40, 0), out _);
            var oversized = Take(first);
            Assert.That(oversized.Status, Is.EqualTo(NavigationCompletionStatus.PathExceedsResultCapacity));
            first.Release(oversized.Handle);
            first.Submit(int2.zero, new int2(10, 0), out _);
            var retained = Take(first);
            first.Submit(int2.zero, new int2(12, 0), out _);
            first.Tick(2, 2, 32);
            Assert.That(first.Active, Is.EqualTo(1));
            Assert.That(first.TryTake(out _), Is.False);
            Assert.That(foreign.CopyPath(retained.Handle, nodes), Is.False);
            Assert.That(first.Release(retained.Handle), Is.True);
            var latest = Take(first);
            Assert.That(first.CopyPath(retained.Handle, nodes), Is.False);
            Assert.That(first.CopyPath(latest.Handle, nodes), Is.True);
            first.SetGrid(null);
            Assert.That(first.CopyPath(latest.Handle, nodes), Is.False);
            Assert.That(first.ResultBlocksUsed, Is.Zero);
        }

        [Test]
        public void ReplacingUncollectedOrDeliveredResultsInvalidatesTheirHandles()
        {
            using var grid = Grid(16, 1, false);
            using var scheduler = new NavigationPathScheduler(1, 100_000, 4, 2, 4);
            scheduler.SetGrid(grid);
            scheduler.Submit(int2.zero, new int2(15, 0), out var original);
            While(() => scheduler.Completed == 0, () => scheduler.Tick(1, 1, 32),
                Stopwatch.GetTimestamp() + 8 * Stopwatch.Frequency);
            Assert.That(scheduler.Replace(original, int2.zero, new int2(7, 0), out var second),
                Is.EqualTo(NavigationSubmit.Accepted));
            Assert.That(scheduler.TryTake(out _), Is.False);
            Assert.That(scheduler.ResultBlocksUsed, Is.Zero);

            var published = Take(scheduler);
            Assert.That(published.Handle.Request.Equals(second), Is.True);
            Assert.That(scheduler.IsCurrent(published.Handle), Is.True);
            Assert.That(scheduler.Replace(second, int2.zero, new int2(1, 0), out var third),
                Is.EqualTo(NavigationSubmit.Accepted));
            Assert.That(scheduler.IsCurrent(published.Handle), Is.False);
            Assert.That(scheduler.Release(published.Handle), Is.False);
            var current = Take(scheduler);
            Assert.That(current.Handle.Request.Equals(third), Is.True);
            Assert.That(current.Path.PathCount, Is.EqualTo(2));
        }

        [Test]
        public void RetiringGridRejectsPreviousGenerationBeforeJobsDrain()
        {
            using var grid = Grid(256, 128, true);
            using var replacement = Grid(32, 16, false);
            using var scheduler = new NavigationPathScheduler(2, 8_000_000, 4, 2, 64);
            scheduler.SetGrid(grid);
            scheduler.Submit(int2.zero, new int2(255, 127), out var running);
            scheduler.Tick(2, 2, 4096);
            var retirement = grid.Retire();
            Assert.That(scheduler.Cancel(running), Is.False);
            scheduler.SetGrid(replacement);
            scheduler.Submit(int2.zero, new int2(9, 9), out var fresh);
            var result = Take(scheduler);
            Assert.That(fresh.GridGeneration, Is.GreaterThan(running.GridGeneration));
            Assert.That(result.Handle.Request.Equals(fresh), Is.True);
            Assert.That(scheduler.WorkspaceBytes, Is.EqualTo(2 * NavigationPathScheduler.WorkspaceSize(512)));
            While(() => !retirement.IsCompleted, ThreadYield, Stopwatch.GetTimestamp() + 8 * Stopwatch.Frequency);
            retirement.Complete();
        }

        [Test]
        public void WorkspaceBudgetCapsActualConcurrencyAndResourcesCanBeRepeatedlyDisposed()
        {
            using var grid = Grid(32, 8, false);
            long slotBytes = NavigationPathScheduler.WorkspaceSize(256);
            for (int i = 0; i < 5; i++)
            {
                using var scheduler = new NavigationPathScheduler(16, slotBytes * 2, 8, 4, 16);
                scheduler.SetGrid(grid);
                for (int j = 0; j < 5; j++) scheduler.Submit(int2.zero, new int2(31, 7), out _);
                scheduler.Tick(16, 16, 128);
                Assert.That(scheduler.Active, Is.EqualTo(2));
                Assert.That(scheduler.WorkspaceBytes, Is.EqualTo(2 * slotBytes));
                scheduler.Dispose();
                Assert.That(scheduler.FinalWaitTicks, Is.GreaterThanOrEqualTo(0));
            }
        }

        private static NavigationScheduledResult Take(NavigationPathScheduler scheduler)
        {
            NavigationScheduledResult result = default;
            While(() => !scheduler.TryTake(out result), () => scheduler.Tick(16, 16, 2048),
                Stopwatch.GetTimestamp() + 8 * Stopwatch.Frequency);
            return result;
        }

        private static void While(Func<bool> predicate, Action step, long deadline)
        {
            while (predicate())
            {
                if (Stopwatch.GetTimestamp() > deadline)
                    Assert.Fail("Timed out waiting for a scheduled navigation operation.");
                step();
                Thread.Yield();
            }
        }

        private static void ThreadYield() => Thread.Yield();

        internal static NavigationGrid Grid(int width, int depth, bool split)
        {
            var grid = new NavigationGrid(new int2(width, depth), float2.zero, 1);
            for (int z = 0; z < depth; z++)
            for (int x = 0; x < width; x++)
            {
                var from = new int2(x, z);
                grid.SetNode(from, 0, 1, 3);
                foreach (int direction in Directions)
                {
                    int2 to = from + NavigationGrid.Offset(direction);
                    if (grid.Contains(to) && (!split || (from.x < width / 2) == (to.x < width / 2)))
                        grid.Connect(from, to, direction);
                }
            }
            return grid;
        }

        private static readonly int[] Directions = { 0, 1, 4, 5 };
    }
}
