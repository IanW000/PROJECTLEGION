using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using NUnit.Framework;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;
using Random = System.Random;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsLayeredNavigationTests
    {
        [Test]
        public void CheapNonlocalAndParallelEdgesKeepOriginalIdentityInAllAlgorithms()
        {
            using var snapshot = Make(4, new List<LayerEdge>
            {
                Edge(0, 1, 10), Edge(1, 3, 10), Edge(0, 2, 1, LayerConnectionType.Nonlocal),
                Edge(0, 2, 15, LayerConnectionType.Bridge), Edge(2, 3, 1, LayerConnectionType.Drop),
                Edge(3, 2, 15)
            });
            Assert.That(snapshot.GeometricHeuristic, Is.False);
            var start = new LayerNodeHandle(snapshot.Generation, 0);
            var goal = new LayerNodeHandle(snapshot.Generation, 3);
            using var search = new LayeredSearchWorkspace(4);
            Assert.That(search.Search(snapshot, start, goal).Cost, Is.EqualTo(2));
            Assert.That(search.TryStep(0, out var first), Is.True);
            Assert.That(snapshot.TryGetEdge(first, out var step), Is.True);
            Assert.That(step.Type, Is.EqualTo(LayerConnectionType.Nonlocal));

            using var field = new LayeredFlowField(snapshot, goal, true, 4096);
            Wait(() => field.TryFinish(out bool published) && published);
            Assert.That(field.Query(start, out var firstFlow, out double cost), Is.EqualTo(NavigationFlowState.Direction));
            Assert.That(firstFlow.Id, Is.EqualTo(first.Id));
            Assert.That(cost, Is.EqualTo(2));

            using var scheduler = new NavigationPathScheduler(2, 65536, 4, 4, 2);
            scheduler.SetLayeredSnapshot(snapshot);
            Assert.That(scheduler.SubmitLayered(start, goal, out var token), Is.EqualTo(NavigationSubmit.Accepted));
            Assert.That(scheduler.ReplaceLayered(token, start, goal, out var replacement), Is.EqualTo(NavigationSubmit.Accepted));
            Assert.That(scheduler.Cancel(token), Is.False);
            NavigationScheduledResult result = default;
            Wait(() => { scheduler.Tick(2, 2, 1); return scheduler.TryTake(out result); });
            Assert.That(result.Handle.Request.Equals(replacement), Is.True);
            using var nodes = new NativeArray<LayerNodeHandle>(3, Allocator.Persistent);
            using var edges = new NativeArray<LayerEdgeHandle>(2, Allocator.Persistent);
            Assert.That(scheduler.CopyLayeredPath(result.Handle, nodes, edges), Is.True);
            Assert.That(nodes[2].Id, Is.EqualTo(3));
            Assert.That(edges[0].Id, Is.EqualTo(first.Id));
            using var hierarchy = new NavigationHierarchyBuilder();
            Assert.That(hierarchy.BeginLayered(snapshot), Is.EqualTo(NavigationHierarchyBuildStatus.UnsupportedTopology));
        }

        [Test]
        public void JointNodeAndEdgeCapacityAndSnapshotRetirementRejectStaleResults()
        {
            var connections = new List<LayerEdge>();
            for (int i = 0; i < 20; i++) connections.Add(Edge(i, i + 1, 1));
            using var snapshot = Make(21, connections);
            using var replacement = Make(21, connections);
            var start = new LayerNodeHandle(snapshot.Generation, 0);
            var goal = new LayerNodeHandle(snapshot.Generation, 20);
            using var scheduler = new NavigationPathScheduler(2, 65536, 8, 4, 1);
            scheduler.SetLayeredSnapshot(snapshot);
            scheduler.SubmitLayered(start, goal, out _);
            NavigationScheduledResult result = default;
            Wait(() => { scheduler.Tick(2, 2, 64); return scheduler.TryTake(out result); });
            Assert.That(result.Status, Is.EqualTo(NavigationCompletionStatus.PathExceedsResultCapacity));
            Assert.That(result.Path.PathCount, Is.Zero);
            scheduler.Release(result.Handle);
            scheduler.SubmitLayered(start, goal, out var running);
            scheduler.Tick(2, 2, 1);
            var retired = snapshot.Retire();
            scheduler.SetLayeredSnapshot(replacement);
            Assert.That(scheduler.Cancel(running), Is.False);
            Assert.That(scheduler.SubmitLayered(start, goal, out _), Is.EqualTo(NavigationSubmit.Stale));
            Assert.That(replacement.TryGetEdge(new LayerEdgeHandle(snapshot.Generation, 0), out _), Is.False);
            Wait(() => { scheduler.Tick(2, 2, 64); return scheduler.Active == 0; });
            retired.Complete();
        }

        [Test]
        public void RetainedAndPartlyCopiedResultsApplyBackpressureAndReleaseTheirReservation()
        {
            var connections = new List<LayerEdge>();
            for (int i = 0; i < 10; i++) connections.Add(Edge(i, i + 1, 1));
            using var graph = Make(11, connections);
            var start = new LayerNodeHandle(graph.Generation, 0);
            var goal = new LayerNodeHandle(graph.Generation, 10);
            using var scheduler = new NavigationPathScheduler(2, 65536, 4, 4, 1);
            scheduler.SetLayeredSnapshot(graph);
            scheduler.SubmitLayered(start, goal, out _);
            NavigationScheduledResult first = default;
            Wait(() => { scheduler.Tick(2, 2, 64); return scheduler.TryTake(out first); });
            scheduler.SubmitLayered(start, goal, out var second);
            scheduler.Tick(2, 2, 1);
            Assert.That(scheduler.Active, Is.EqualTo(1));
            Assert.That(scheduler.ResultBlocksUsed, Is.EqualTo(1));
            Assert.That(scheduler.TryTake(out _), Is.False);
            Assert.That(scheduler.Release(first.Handle), Is.True);
            Wait(() => { scheduler.Tick(0, 2, 1); return scheduler.ResultBlocksUsed == 1; });
            Assert.That(scheduler.TryTake(out _), Is.False);
            Assert.That(scheduler.Cancel(second), Is.True);
            Assert.That(scheduler.ResultBlocksUsed, Is.Zero);
        }

        [Test]
        public void FlowReadersFinishAfterRetirementWithoutPublishingStaleViews()
        {
            using var graph = Make(2, new List<LayerEdge> { Edge(0, 1, 3, LayerConnectionType.Bridge) });
            using var field = new LayeredFlowField(graph, new LayerNodeHandle(graph.Generation, 1), false, 4096);
            Assert.That(field.TryFinish(out bool ready) && ready, Is.True);
            Assert.That(field.TryGetView(graph, out var view), Is.True);
            using var first = new NativeArray<LayerEdgeHandle>(1, Allocator.Persistent);
            using var second = new NativeArray<LayerEdgeHandle>(1, Allocator.Persistent);
            var node = new LayerNodeHandle(graph.Generation, 0);
            field.RegisterReader(graph, new ReadField { Field = view, Start = node, Result = first }.Schedule());
            field.RegisterReader(graph, new ReadField { Field = view, Start = node, Result = second }.Schedule());
            var graphFence = graph.Retire();
            var flowFence = field.Retire();
            Assert.That(field.TryGetView(graph, out _), Is.False);
            JobHandle.CombineDependencies(graphFence, flowFence).Complete();
            Assert.That(first[0].Generation, Is.EqualTo(node.Generation));
            Assert.That(second[0].Id, Is.EqualTo(first[0].Id));
            Assert.That(graph.TryGetEdge(first[0], out _), Is.False);
        }

        private struct ReadField : IJob
        {
            internal LayeredFlowView Field;
            internal LayerNodeHandle Start;
            internal NativeArray<LayerEdgeHandle> Result;
            public void Execute()
            {
                Field.Query(Start, out var edge, out _);
                Result[0] = edge;
            }
        }

        [Test]
        public void RandomDirectedGraphsMatchIndependentFloydWarshall()
        {
            const int count = 14;
            var random = new Random(8601);
            for (int repeat = 0; repeat < 5; repeat++)
            {
                var edges = new List<LayerEdge>();
                var costs = new double[count, count];
                for (int from = 0; from < count; from++)
                for (int to = 0; to < count; to++)
                {
                    costs[from, to] = from == to ? 0 : double.PositiveInfinity;
                    if (from == to || random.Next(5) != 0) continue;
                    var edge = Edge(from, to, random.Next(1, 100) / 4d, LayerConnectionType.Nonlocal);
                    edges.Add(edge);
                    costs[from, to] = edge.Cost;
                }
                using var graph = Make(count, edges);
                using var workspace = new LayeredSearchWorkspace(count);
                for (int k = 0; k < count; k++)
                for (int from = 0; from < count; from++)
                for (int to = 0; to < count; to++)
                    costs[from, to] = Math.Min(costs[from, to], costs[from, k] + costs[k, to]);

                for (int to = 0; to < count; to++)
                {
                    var goal = new LayerNodeHandle(graph.Generation, to);
                    using var flow = new LayeredFlowField(graph, goal, false, 65536);
                    Assert.That(flow.TryFinish(out bool ready) && ready, Is.True);
                    for (int from = 0; from < count; from++)
                    {
                        var start = new LayerNodeHandle(graph.Generation, from);
                        var result = workspace.Search(graph, start, goal);
                        var state = flow.Query(start, out var fieldEdge, out double fieldCost);
                        double expected = costs[from, to];
                        if (double.IsPositiveInfinity(expected))
                        {
                            Assert.That(result.Status, Is.EqualTo(NavigationPathStatus.Unreachable));
                            Assert.That(state, Is.EqualTo(NavigationFlowState.Unreachable));
                            continue;
                        }
                        Assert.That(result.Cost, Is.EqualTo(expected).Within(1e-10));
                        Assert.That(fieldCost, Is.EqualTo(expected).Within(1e-10));
                        int node = from;
                        double sum = 0;
                        for (int i = 0; i < result.PathCount - 1; i++)
                        {
                            Assert.That(workspace.TryStep(i, out var id), Is.True);
                            Assert.That(graph.TryGetEdge(id, out var edge), Is.True);
                            Assert.That(edge.From, Is.EqualTo(node));
                            node = edge.To;
                            sum += edge.Cost;
                        }
                        Assert.That(node, Is.EqualTo(to));
                        Assert.That(sum, Is.EqualTo(expected).Within(1e-10));
                        if (from != to)
                        {
                            Assert.That(graph.TryGetEdge(fieldEdge, out var edge), Is.True);
                            Assert.That(edge.From, Is.EqualTo(from));
                            Assert.That(edge.Cost + costs[edge.To, to], Is.EqualTo(expected).Within(1e-10));
                        }
                    }
                }
            }
        }

        [Test]
        public void SortedEdgeIdentityIsIndependentOfInputOrder()
        {
            var edges = new List<LayerEdge>
            {
                new LayerEdge { From = 0, To = 1, Cost = 1, Owner = 23, StableKey = 4, Type = LayerConnectionType.Bridge },
                new LayerEdge { From = 0, To = 1, Cost = 1, Owner = 23, StableKey = 8, Type = LayerConnectionType.Bridge },
                Edge(1, 0, 2, LayerConnectionType.Drop)
            };
            using var first = Make(2, edges);
            edges.Reverse();
            using var second = Make(2, edges);
            Assert.That(first.Generation, Is.Not.EqualTo(second.Generation));
            for (int i = 0; i < first.EdgeCount; i++)
            {
                Assert.That(first.TryGetEdge(new LayerEdgeHandle(first.Generation, i), out var a), Is.True);
                Assert.That(second.TryGetEdge(new LayerEdgeHandle(second.Generation, i), out var b), Is.True);
                Assert.That(a.StableKey, Is.EqualTo(b.StableKey));
                Assert.That(a.From, Is.EqualTo(b.From));
                Assert.That(a.To, Is.EqualTo(b.To));
            }
        }

        [TestCase(2f, 4, 16, false)]
        [TestCase(.6f, 4, 16, true)]
        [TestCase(.01f, 4, 16, false)]
        [TestCase(2f, 1, 16, false)]
        [TestCase(2f, 4, 2, false)]
        public void BridgeExtractionClearanceAmbiguityAndLimits(float top, int layers, int hits, bool blockedBelow)
        {
            var scene = SceneManager.CreateScene("Layered fixture " + Guid.NewGuid(),
                new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            try
            {
                Surface(scene, 1, new Vector3(1, -.5f, .5f), new Vector3(4, 1, 3));
                Surface(scene, 2, new Vector3(1, top - .0025f, .5f), new Vector3(4, .005f, 3));
                Physics.SyncTransforms();
                var settings = LayerNavigationSettings.Default;
                settings.Grid.Size = new Vector2Int(2, 1);
                settings.Grid.SampleDensity = 1;
                settings.Layers = layers;
                settings.HitCapacity = hits;
                var builder = new LayeredNavigationBuilder(scene.GetPhysicsScene(), float3.zero, settings, 65536);
                if (top < .02f || layers == 1 || hits == 2)
                {
                    Assert.Throws<ArgumentException>(() => builder.Build(null, out _));
                    return;
                }
                using var graph = builder.Build(null, out _);
                Assert.That(graph.NodeCount, Is.EqualTo(4));
                Assert.That(graph.View().Valid(0), Is.EqualTo(!blockedBelow));
                Assert.That(graph.View().Valid(1), Is.True);
                for (int i = 0; i < graph.EdgeCount; i++)
                {
                    var edge = graph.View().Edges[i];
                    Assert.That(edge.From % 2, Is.EqualTo(edge.To % 2));
                }
            }
            finally
            {
                foreach (var root in scene.GetRootGameObjects()) UnityEngine.Object.DestroyImmediate(root);
                SceneManager.UnloadSceneAsync(scene);
            }
        }

        private static void Surface(Scene scene, int key, Vector3 position, Vector3 scale)
        {
            var obj = new GameObject("Stable Surface " + key);
            SceneManager.MoveGameObjectToScene(obj, scene);
            obj.transform.position = position;
            obj.AddComponent<BoxCollider>().size = scale;
            var tag = obj.AddComponent<LayerNavigationSurface>();
            typeof(LayerNavigationSurface).GetField("_stableKey", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic).SetValue(tag, key);
        }

        [Test]
        public void AdjacentHeightMismatchAndAmbiguousSupportAreConservative()
        {
            var scene = SceneManager.CreateScene("Layered conservative fixture " + Guid.NewGuid(),
                new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            try
            {
                Surface(scene, 1, new Vector3(.45f, -.5f, .5f), new Vector3(1.18f, 1, 3));
                Surface(scene, 2, new Vector3(1.55f, .1f, .5f), new Vector3(.92f, 1, 3));
                Surface(scene, 3, new Vector3(.45f, .175f, 2.5f), new Vector3(1.2f, .01f, 1.2f));
                Physics.SyncTransforms();
                var settings = LayerNavigationSettings.Default;
                settings.Grid.Size = new Vector2Int(2, 1);
                settings.Grid.AgentRadius = .18f;
                settings.Grid.SampleDensity = 1;
                using (var adjacent = new LayeredNavigationBuilder(scene.GetPhysicsScene(), float3.zero, settings, 65536)
                    .Build(null, out var stats))
                {
                    Assert.That(adjacent.View().Valid(0) && adjacent.View().Valid(1), Is.True);
                    Assert.That(adjacent.EdgeCount, Is.Zero);
                    Assert.That(stats.ContinuityRejections, Is.GreaterThan(0));
                }

                Surface(scene, 4, new Vector3(.45f, -.5f, 2.5f), new Vector3(1.2f, 1, 1.2f));
                Physics.SyncTransforms();
                settings.Grid.Size = new Vector2Int(1, 1);
                using var ambiguous = new LayeredNavigationBuilder(scene.GetPhysicsScene(), new float3(0, 0, 2), settings, 65536)
                    .Build(null, out var blockedStats);
                Assert.That(ambiguous.NodeCount, Is.EqualTo(2));
                Assert.That(ambiguous.View().Valid(1), Is.False);
                Assert.That(blockedStats.RejectedCandidates, Is.GreaterThan(0));
            }
            finally
            {
                foreach (var root in scene.GetRootGameObjects()) UnityEngine.Object.DestroyImmediate(root);
                SceneManager.UnloadSceneAsync(scene);
            }
        }

        [Test]
        public void AuthoringRejectsDuplicateSurfaceKeysAndUnresolvedLinkMarkers()
        {
            var scene = SceneManager.CreateScene("Layer stable keys " + Guid.NewGuid(),
                new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            try
            {
                Surface(scene, 1, new Vector3(1, -.5f, .5f), new Vector3(4, 1, 3));
                Surface(scene, 1, new Vector3(1, 1.95f, .5f), new Vector3(4, .1f, 3));
                Physics.SyncTransforms();
                var settings = LayerNavigationSettings.Default;
                settings.Grid.Size = new Vector2Int(2, 1);
                settings.Grid.SampleDensity = 1;
                var physics = scene.GetPhysicsScene();
                var duplicate = Assert.Throws<ArgumentException>(() =>
                    new LayeredNavigationBuilder(physics, float3.zero, settings, 65536).Build(null, out _));
                Assert.That(duplicate.Message, Does.Contain("Duplicate surface key"));
                foreach (var root in scene.GetRootGameObjects())
                    if (root.transform.position.y > 1) UnityEngine.Object.DestroyImmediate(root);

                var owner = new GameObject("Link");
                SceneManager.MoveGameObjectToScene(owner, scene);
                var link = owner.AddComponent<LayerNavigationLink>();
                var from = new GameObject("From").transform;
                from.SetParent(owner.transform);
                from.position = new Vector3(.5f, 0, .5f);
                var to = new GameObject("Outside").transform;
                to.SetParent(owner.transform);
                to.position = new Vector3(99, 0, .5f);
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance |
                    System.Reflection.BindingFlags.NonPublic;
                typeof(LayerNavigationLink).GetField("_source", flags).SetValue(link, from);
                typeof(LayerNavigationLink).GetField("_destination", flags).SetValue(link, to);
                Physics.SyncTransforms();
                var invalid = Assert.Throws<ArgumentException>(() =>
                    new LayeredNavigationBuilder(physics, float3.zero, settings, 65536).Build(new[] { link }, out _));
                Assert.That(invalid.Message, Does.Contain("Unresolved"));
            }
            finally
            {
                foreach (var root in scene.GetRootGameObjects()) UnityEngine.Object.DestroyImmediate(root);
                SceneManager.UnloadSceneAsync(scene);
            }
        }

        internal static LayerEdge Edge(int from, int to, double cost, LayerConnectionType type = LayerConnectionType.Surface) =>
            new LayerEdge { From = from, To = to, Cost = cost, Type = type };

        internal static LayeredNavigationSnapshot Make(int count, List<LayerEdge> edges)
        {
            var nodes = new LayerNode[count];
            var columns = new int[count + 1];
            for (int i = 0; i < count; i++)
            {
                nodes[i] = new LayerNode { Position = new float3(i + .5f, 0, .5f), Cost = 1, Walkable = true };
                columns[i] = i;
            }
            columns[count] = count;
            return new LayeredNavigationSnapshot(new int2(count, 1), float2.zero, 1, nodes, columns, edges, 2_000_000);
        }

        internal static void Wait(Func<bool> finished)
        {
            long deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 12;
            while (!finished())
            {
                if (Stopwatch.GetTimestamp() > deadline) Assert.Fail("Layered job timed out.");
                Thread.Yield();
            }
        }
    }
}
