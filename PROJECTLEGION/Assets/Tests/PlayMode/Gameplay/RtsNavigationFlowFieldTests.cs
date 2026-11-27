using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using Random = System.Random;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsNavigationFlowFieldTests
    {
        [TestCase(false)]
        [TestCase(true)]
        public void RandomWeightedHeightGraphsMatchForwardReferenceAndAStar(bool diagonals)
        {
            const int side = 7;
            using var builder = new NavigationFlowFieldBuilder(side * side);
            for (int seed = 0; seed < 8; seed++)
            {
                using var grid = MakeGraph(side, "weighted", diagonals, seed);
                using var first = Build(builder, grid, new int2(side - 1));
                using var second = Build(builder, grid, new int2(side - 1), true);
                AssertOptimal(grid, first);
                Assert.That(first.TryGetView(grid, out var a), Is.True);
                Assert.That(second.TryGetView(grid, out var b), Is.True);
                for (int i = 0; i < side * side; i++)
                {
                    int2 cell = grid.AsReadOnly().Cell(i);
                    Assert.That(a.Query(cell, out var an, out var ac), Is.EqualTo(b.Query(cell, out var bn, out var bc)));
                    Assert.That(an, Is.EqualTo(bn));
                    Assert.That(ac, Is.EqualTo(bc));
                }
            }
        }

        [Test]
        public void UsesIncomingDirectedEdgesAndOriginalAsymmetricCosts()
        {
            using var grid = new NavigationGrid(new int2(3, 2), float2.zero, 1);
            for (int i = 0; i < 6; i++) grid.SetNode(grid.AsReadOnly().Cell(i), 0, 1, 3);
            using var costs = new NativeArray<double>(48, Allocator.Persistent);
            var writable = costs;
            void Edge(int index, int direction, double cost)
            {
                grid.ConnectDirected(grid.AsReadOnly().Cell(index), direction);
                writable[index * 8 + direction] = cost;
            }

            Edge(0, 0, 19); Edge(1, 2, 1.5); Edge(1, 0, 1);
            Edge(0, 1, 2); Edge(3, 0, 2); Edge(4, 0, 2); Edge(5, 3, 2);
            Edge(2, 1, 40);

            using var builder = new NavigationFlowFieldBuilder(6);
            using var field = Build(builder, grid, new int2(2, 0), true, costs);
            AssertOptimal(grid, field, costs);
            Assert.That(field.TryGetView(grid, out var view), Is.True);
            Assert.That(view.Query(int2.zero, out int2 next, out double total), Is.EqualTo(NavigationFlowState.Direction));
            Assert.That(next, Is.EqualTo(new int2(0, 1)));
            Assert.That(total, Is.EqualTo(8));

            using var reverse = Build(builder, grid, int2.zero, false, costs);
            AssertOptimal(grid, reverse, costs);
            Assert.That(reverse.TryGetView(grid, out var reverseView), Is.True);
            Assert.That(reverseView.Query(new int2(2, 0), out _, out _), Is.EqualTo(NavigationFlowState.Unreachable));
        }

        [Test]
        public void WorkspaceReusePreservesBothFieldsAndReaderLifetime()
        {
            using var source = MakeGraph(96, "open");
            using var replacement = MakeGraph(96, "open");
            using var builder = new NavigationFlowFieldBuilder(96 * 96);
            using var first = Build(builder, source, int2.zero, true);
            using var second = Build(builder, source, new int2(95), true);
            Assert.That(first.TryGetView(source, out var a), Is.True);
            Assert.That(second.TryGetView(source, out var b), Is.True);
            Assert.That(a.Query(int2.zero, out _, out _), Is.EqualTo(NavigationFlowState.Goal));
            Assert.That(b.Query(new int2(95), out _, out _), Is.EqualTo(NavigationFlowState.Goal));
            using var firstRead = new NativeArray<int2>(4096, Allocator.Persistent);
            using var otherRead = new NativeArray<int2>(4096, Allocator.Persistent);
            using var secondRead = new NativeArray<int2>(4096, Allocator.Persistent);
            first.RegisterReader(source, new ReadJob { View = a, Output = firstRead }.ScheduleParallel(4096, 16, default));
            first.RegisterReader(source, new ReadJob { View = a, Output = otherRead }.ScheduleParallel(4096, 16, default));
            var secondFence = new ReadJob { View = b, Output = secondRead }.ScheduleParallel(4096, 16, default);
            second.RegisterReader(source, secondFence);

            builder.Begin(source, new int2(90), true);
            var gridFence = source.Retire();
            Assert.That(first.TryGetView(source, out _), Is.False);
            Assert.That(second.TryGetView(replacement, out _), Is.False);
            var firstFence = first.Retire();
            firstFence.Complete();
            secondFence.Complete();
            gridFence.Complete();
            Assert.That(firstRead[0], Is.EqualTo(new int2(46)));
            Assert.That(otherRead[4095], Is.EqualTo(firstRead[0]));
            Assert.That(secondRead[0], Is.EqualTo(new int2(48)));
            Assert.That(builder.TryFinish(replacement, out var status, out var stale), Is.True);
            Assert.That(status, Is.EqualTo(NavigationFlowBuildStatus.Invalidated));
            Assert.That(stale, Is.Null);
            Assert.That(first.PayloadBytes, Is.Zero);
            using var fresh = Build(builder, replacement, new int2(10), true);
            Assert.That(fresh.GridGeneration, Is.Not.EqualTo(first.GridGeneration));
            builder.Dispose();
            Assert.That(fresh.TryGetView(replacement, out var freshView), Is.True);
            Assert.That(freshView.Query(new int2(10), out _, out _), Is.EqualTo(NavigationFlowState.Goal));
        }

        [Test]
        public void DistinguishesGoalBlockedUnreachableAndInvalid()
        {
            using var grid = MakeGraph(6, "split");
            grid.SetNode(new int2(0, 5), 0, 1, 1);
            using var builder = new NavigationFlowFieldBuilder(36);
            Assert.That(builder.Begin(grid, new int2(0, 5), true), Is.EqualTo(NavigationFlowBuildStatus.InvalidGoal));
            Assert.That(builder.Begin(grid, new int2(-1), false), Is.EqualTo(NavigationFlowBuildStatus.InvalidGoal));
            using var field = Build(builder, grid, int2.zero);
            Assert.That(field.TryGetView(grid, out var view), Is.True);
            Assert.That(view.Query(int2.zero, out _, out double goal), Is.EqualTo(NavigationFlowState.Goal));
            Assert.That(goal, Is.Zero);
            Assert.That(view.Query(new int2(0, 5), out _, out _), Is.EqualTo(NavigationFlowState.Blocked));
            Assert.That(view.Query(new int2(5), out _, out _), Is.EqualTo(NavigationFlowState.Unreachable));
            Assert.That(view.Query(new int2(-1), out _, out _), Is.EqualTo(NavigationFlowState.Invalid));
        }

        [Test]
        public void EqualOptimalNeighborsHaveAStableDirection()
        {
            using var grid = MakeGraph(3, "open", false);
            using var builder = new NavigationFlowFieldBuilder(9);
            using var field = Build(builder, grid, new int2(2), true);
            AssertOptimal(grid, field);
            Assert.That(field.TryGetView(grid, out var view), Is.True);
            Assert.That(view.TryGetNext(int2.zero, out var next), Is.True);
            Assert.That(next, Is.EqualTo(new int2(1, 0)));
        }

        [Test]
        public void RoundedPlateauIsRejectedRatherThanPublishingANonDescendingDirection()
        {
            using var grid = new NavigationGrid(new int2(3, 1), float2.zero, 1);
            for (int i = 0; i < 3; i++) grid.SetNode(new int2(i, 0), 0, 1, 3);
            grid.ConnectDirected(int2.zero, 0);
            grid.ConnectDirected(new int2(1, 0), 0);
            using var costs = new NativeArray<double>(24, Allocator.Persistent);
            var writable = costs;
            writable[0] = 1;
            writable[8] = 1e20;
            using var builder = new NavigationFlowFieldBuilder(3);
            Assert.That(builder.Begin(grid, new int2(2, 0), false, costs), Is.EqualTo(NavigationFlowBuildStatus.Success));
            Assert.That(builder.TryFinish(grid, out var status, out var field), Is.True);
            Assert.That(status, Is.EqualTo(NavigationFlowBuildStatus.NumericFailure));
            Assert.That(field, Is.Null);
        }

        [BurstCompile]
        private struct ReadJob : IJobFor
        {
            internal NavigationFlowView View;
            internal NativeArray<int2> Output;
            public void Execute(int index)
            {
                View.TryGetNext(new int2(47), out var next);
                Output[index] = next;
            }
        }

        internal static NavigationFlowField Build(NavigationFlowFieldBuilder builder, NavigationGrid grid, int2 goal,
            bool async = false, NativeArray<double> edgeCosts = default)
        {
            Assert.That(builder.Begin(grid, goal, async, edgeCosts), Is.EqualTo(NavigationFlowBuildStatus.Success));
            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 15;
            NavigationFlowBuildStatus status;
            NavigationFlowField field;
            while (!builder.TryFinish(grid, out status, out field))
            {
                if (Stopwatch.GetTimestamp() > deadline) Assert.Fail("Flow Field did not complete.");
                Thread.Yield();
            }
            Assert.That(status, Is.EqualTo(NavigationFlowBuildStatus.Success));
            return field;
        }

        internal static NavigationGrid MakeGraph(int side, string layout, bool diagonals = true, int randomSeed = -1)
        {
            var grid = new NavigationGrid(new int2(side), float2.zero, 1);
            var random = new Random(randomSeed);
            bool weighted = layout == "weighted";
            for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
                grid.SetNode(new int2(x, z), randomSeed < 0 ? 0 : random.Next(5) * 0.125f,
                    weighted ? 1 + (x * 7 + z * 13) % 9 : 1, 3);
            for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
            {
                var from = new int2(x, z);
                for (int i = 0; i < (diagonals ? 4 : 2); i++)
                {
                    int direction = i < 2 ? i : i + 2;
                    int2 to = from + NavigationGrid.Offset(direction);
                    if (!grid.Contains(to)) continue;
                    if ((x < side / 2) != (to.x < side / 2) &&
                        (layout == "split" || (layout == "gate" && (z != side / 3 || to.y != z)))) continue;
                    if (randomSeed < 0 || random.Next(5) != 0) grid.Connect(from, to, direction);
                }
            }
            return grid;
        }

        internal static void AssertOptimal(NavigationGrid grid, NavigationFlowField field, NativeArray<double> costs = default)
        {
            Assert.That(field.TryGetView(grid, out var view), Is.True);
            int count = grid.Size.x * grid.Size.y;
            var graph = grid.AsReadOnly();
            using var astar = new NavigationPathfinder(count);
            for (int i = 0; i < count; i++)
            {
                int2 cell = graph.Cell(i);
                var state = view.Query(cell, out int2 next, out double cost);
                if (!graph.IsWalkable(cell))
                {
                    Assert.That(state, Is.EqualTo(NavigationFlowState.Blocked));
                    continue;
                }

                double reference = ForwardDijkstra(grid, i, graph.Index(field.Goal), costs);
                CompareCost(cost, reference);
                if (!costs.IsCreated)
                {
                    var result = astar.Search(grid, cell, field.Goal);
                    Assert.That(result.Status, Is.EqualTo(double.IsInfinity(cost)
                        ? NavigationPathStatus.Unreachable : NavigationPathStatus.Success));
                    if (result.Status == NavigationPathStatus.Success) CompareCost(cost, result.Cost);
                }

                if (double.IsInfinity(cost))
                {
                    Assert.That(state, Is.EqualTo(NavigationFlowState.Unreachable));
                    continue;
                }
                if (cell.Equals(field.Goal))
                {
                    Assert.That(state, Is.EqualTo(NavigationFlowState.Goal));
                    continue;
                }

                Assert.That(state, Is.EqualTo(NavigationFlowState.Direction));
                double bellman = double.PositiveInfinity;
                double chosen = double.NaN;
                for (int d = 0; d < 8; d++)
                {
                    if (!Edge(grid, costs, cell, d, out var to, out double weight)) continue;
                    view.Query(to, out _, out double remainder);
                    bellman = Math.Min(bellman, weight + remainder);
                    if (to.Equals(next))
                    {
                        Assert.That(remainder, Is.LessThan(cost));
                        chosen = weight + remainder;
                    }
                }

                Assert.That(cost, Is.EqualTo(bellman));
                Assert.That(cost, Is.EqualTo(chosen));
                int2 cursor = cell;
                double sum = 0;
                for (int step = 0; !cursor.Equals(field.Goal) && step < count; step++)
                {
                    Assert.That(view.TryGetNext(cursor, out var following), Is.True);
                    for (int d = 0; d < 8; d++)
                        if (Edge(grid, costs, cursor, d, out var neighbor, out double weight) && neighbor.Equals(following))
                            sum += weight;
                    cursor = following;
                }
                Assert.That(cursor, Is.EqualTo(field.Goal));
                CompareCost(sum, reference);
            }
        }

        private static void CompareCost(double cost, double expected)
        {
            if (double.IsInfinity(expected)) Assert.That(cost, Is.EqualTo(expected));
            else Assert.That(cost, Is.EqualTo(expected).Within(1e-12 * Math.Max(1, expected)));
        }

        private static bool Edge(NavigationGrid grid, NativeArray<double> costs, int2 from,
            int direction, out int2 to, out double cost)
        {
            if (!grid.TryGetNeighbor(from, direction, out to, out cost)) return false;
            if (!grid.TryGetNode(to, out var node) || !node.Walkable) return false;
            if (costs.IsCreated) cost = costs[(from.y * grid.Size.x + from.x) * 8 + direction];
            return true;
        }

        private static double ForwardDijkstra(NavigationGrid grid, int start, int goal, NativeArray<double> costs)
        {
            var graph = grid.AsReadOnly();
            int count = graph.Size.x * graph.Size.y;
            var visited = new bool[count];
            var distance = new double[count];
            for (int i = 0; i < count; i++) distance[i] = double.PositiveInfinity;
            distance[start] = 0;
            while (true)
            {
                int best = -1;
                double cost = double.PositiveInfinity;
                for (int i = 0; i < count; i++)
                    if (!visited[i] && distance[i] < cost) { best = i; cost = distance[i]; }
                if (best < 0 || best == goal) return cost;
                visited[best] = true;
                for (int d = 0; d < 8; d++)
                {
                    if (!Edge(grid, costs, graph.Cell(best), d, out var target, out double edge)) continue;
                    int next = graph.Index(target);
                    distance[next] = Math.Min(distance[next], cost + edge);
                }
            }
        }
    }
}
