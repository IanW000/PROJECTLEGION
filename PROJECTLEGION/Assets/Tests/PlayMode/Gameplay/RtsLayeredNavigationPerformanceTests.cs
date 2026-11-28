using System;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using ProjectLegion.Gameplay;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Profiling;
using System.Collections.Generic;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsLayeredNavigationPerformanceTests
    {
        [TestCase(64, 0)] [TestCase(64, 1)] [TestCase(64, 2)]
        [TestCase(128, 0)] [TestCase(128, 1)] [TestCase(128, 2)]
        [TestCase(256, 0)] [TestCase(256, 1)] [TestCase(256, 2)]
        public void ReportCsrPathsAndField(int side, int density)
        {
            long start = Stopwatch.GetTimestamp();
            using var graph = Make(side, density, out double topology, out double pack);
            double construction = Elapsed(start);
            var from = new LayerNodeHandle(graph.Generation, 0);
            var to = new LayerNodeHandle(graph.Generation, graph.NodeCount - 1);
            using var workspace = new LayeredSearchWorkspace(graph.NodeCount);
            var expected = workspace.Search(graph, from, to);
            Assert.That(expected.Status, Is.EqualTo(NavigationPathStatus.Success));
            using var referenceGrid = density == 0 ? RtsNavigationFlowFieldTests.MakeGraph(side, "open", false) : null;
            using var oldWorkspace = density == 0 ? new NavigationPathfinder(side * side) : null;
            oldWorkspace?.Search(referenceGrid, int2.zero, new int2(side - 1));
            var samples = new double[5];
            var old = new double[5];
            int checksum = 0;
            using var gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC.Alloc", 1,
                ProfilerRecorderOptions.CollectOnlyOnCurrentThread | ProfilerRecorderOptions.SumAllSamplesInFrame);
            long gcStart = gc.Valid ? gc.CurrentValue : -1;
            for (int i = 0; i < samples.Length; i++)
            {
                start = Stopwatch.GetTimestamp();
                var result = workspace.Search(graph, from, to);
                for (int n = 0; n < result.PathCount; n++) checksum ^= workspace.Node(n);
                for (int n = 0; n < result.PathCount - 1; n++) checksum ^= workspace.Edge(n);
                samples[i] = Elapsed(start);
                if (oldWorkspace == null) continue;
                start = Stopwatch.GetTimestamp();
                var baseline = oldWorkspace.Search(referenceGrid, int2.zero, new int2(side - 1));
                for (int n = 0; n < baseline.PathCount; n++) checksum ^= oldWorkspace.PathNode(n);
                old[i] = Elapsed(start);
                if (baseline.Cost != result.Cost) throw new InvalidOperationException("Single-layer costs differ.");
            }
            long searchGc = gcStart < 0 ? -1 : gc.CurrentValue - gcStart;
            gcStart = gc.Valid ? gc.CurrentValue : -1;
            var control = new byte[16384];
            long calibration = gcStart < 0 ? -1 : gc.CurrentValue - gcStart;
            GC.KeepAlive(control);

            using var scheduler = new NavigationPathScheduler(4, 64L * 1024 * 1024, 32, 8, 1024);
            scheduler.SetLayeredSnapshot(graph);
            scheduler.SubmitLayered(from, to, out _);
            Drain(scheduler, 1, expected.PathCount);
            const int batch = 16;
            using var pathCpu = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Navigation.Layered.AStar");
            long previousPathCpu = pathCpu.Valid ? pathCpu.CurrentValue : -1;
            start = Stopwatch.GetTimestamp();
            for (int i = 0; i < batch; i++) scheduler.SubmitLayered(from, to, out _);
            var overhead = Drain(scheduler, batch, expected.PathCount);
            double parallel = Elapsed(start);
            double parallelCpu = previousPathCpu < 0 ? -1 : (pathCpu.CurrentValue - previousPathCpu) / 1e6;

            using (var warm = new LayeredFlowField(graph, to, false, 64L * 1024 * 1024))
                Assert.That(warm.TryFinish(out bool warmed) && warmed, Is.True);
            using var flowCpu = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "Navigation.Layered.Flow");
            long previousFlowCpu = flowCpu.Valid ? flowCpu.CurrentValue : -1;
            start = Stopwatch.GetTimestamp();
            using var flow = new LayeredFlowField(graph, to, true, 64L * 1024 * 1024);
            double flowSchedule = Elapsed(start);
            RtsLayeredNavigationTests.Wait(() => flow.TryFinish(out bool ready) && ready);
            double flowWall = Elapsed(start);
            double flowWorker = previousFlowCpu < 0 ? -1 : (flowCpu.CurrentValue - previousFlowCpu) / 1e6;
            start = Stopwatch.GetTimestamp();
            int count = 10000;
            for (int i = 0; i < count; i++)
                if (flow.Query(new LayerNodeHandle(graph.Generation, i % graph.NodeCount), out var edge, out _)
                    == NavigationFlowState.Direction) checksum ^= edge.Id;
            double lookup = Elapsed(start);
            Array.Sort(samples); Array.Sort(old);
            using var replacement = Make(side, density, out _, out _);
            TestContext.WriteLine(FormattableString.Invariant(
                $"PH86 size={side} density={density} nodes={graph.NodeCount} edges={graph.EdgeCount} topology={topology:F3} pack={pack:F3} build={construction:F3} native={graph.PayloadBytes} overlap={graph.PayloadBytes+replacement.PayloadBytes} oldSingle={old[2]:F4} astar={samples[2]:F4} parallel16={parallel:F4} parallelCpu={parallelCpu:F4} dispatch={overhead.Dispatch:F4} collect={overhead.Collect:F4} flow={flowWall:F4} flowSchedule={flowSchedule:F4} flowCpu={flowWorker:F4} lookup10000={lookup:F4} workspace={LayeredSearchWorkspace.Budget(graph.NodeCount)} flowNative={flow.PersistentBytes} schedulerPeak={scheduler.PeakPayloadBytes} expanded={expected.Expanded} gcMarkerNs={searchGc} gcControlNs={calibration} checksum={checksum}"));
        }

        private static LayeredNavigationSnapshot Make(int side, int density, out double topology, out double pack)
        {
            long start = Stopwatch.GetTimestamp();
            var columns = new int[side * side + 1];
            var nodes = new List<LayerNode>();
            var edges = new List<LayerEdge>();
            for (int z = 0; z < side; z++)
            for (int x = 0; x < side; x++)
            {
                columns[z * side + x] = nodes.Count;
                nodes.Add(new LayerNode { Position = new float3(x + .5f, 0, z + .5f), Walkable = true, Cost = 1 });
                if (density == 2 || density == 1 && x >= side / 2)
                    nodes.Add(new LayerNode { Position = new float3(x + .5f, 3, z + .5f), Walkable = true, Cost = 1 });
            }
            columns[columns.Length - 1] = nodes.Count;
            for (int i = 0; i < side * side; i++)
            {
                int layers = columns[i + 1] - columns[i];
                for (int level = 0; level < layers; level++)
                {
                    int from = columns[i] + level;
                    for (int d = 0; d < 4; d++)
                    {
                        int2 offset = NavigationGrid.Offset(d);
                        int2 next = new int2(i % side, i / side) + offset;
                        if (math.any(next < 0 | next >= side)) continue;
                        int col = next.y * side + next.x;
                        if (columns[col] + level < columns[col + 1])
                            edges.Add(RtsLayeredNavigationTests.Edge(from, columns[col] + level, 1));
                    }
                }
                if (layers == 2 && i % 16 == 0)
                {
                    edges.Add(RtsLayeredNavigationTests.Edge(columns[i], columns[i] + 1, 3, LayerConnectionType.Ramp));
                    edges.Add(RtsLayeredNavigationTests.Edge(columns[i] + 1, columns[i], 4, LayerConnectionType.Drop));
                }
            }
            var array = nodes.ToArray();
            topology = Elapsed(start);
            start = Stopwatch.GetTimestamp();
            var result = new LayeredNavigationSnapshot(new int2(side), float2.zero, 1, array, columns, edges, 128L * 1024 * 1024);
            pack = Elapsed(start);
            return result;
        }

        private static (double Dispatch, double Collect) Drain(NavigationPathScheduler scheduler, int remaining, int pathCount)
        {
            using var nodes = new NativeArray<LayerNodeHandle>(pathCount, Allocator.Persistent);
            using var edges = new NativeArray<LayerEdgeHandle>(pathCount - 1, Allocator.Persistent);
            long dispatch = 0, collect = 0;
            RtsLayeredNavigationTests.Wait(() =>
            {
                scheduler.Tick(4, 4, 4096);
                dispatch += scheduler.SchedulingTicks;
                collect += scheduler.CollectionTicks;
                while (scheduler.TryTake(out var result))
                {
                    if (result.Path.Status != NavigationPathStatus.Success || !scheduler.CopyLayeredPath(result.Handle, nodes, edges))
                        throw new InvalidOperationException("Incomplete measured path.");
                    scheduler.Release(result.Handle);
                    remaining--;
                }
                return remaining <= 0;
            });
            return (dispatch * 1000d / Stopwatch.Frequency, collect * 1000d / Stopwatch.Frequency);
        }

        private static double Elapsed(long time) => (Stopwatch.GetTimestamp() - time) * 1000d / Stopwatch.Frequency;
    }
}
