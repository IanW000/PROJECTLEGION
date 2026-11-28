using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsDynamicNavigationTests
    {
        [TestCase(TopologyStrategy.Full)]
        [TestCase(TopologyStrategy.LinkOnly)]
        [TestCase(TopologyStrategy.Partial)]
        public void ObstaclesBridgeAndLinksMatchFreshFullAfterEachEdit(TopologyStrategy preference)
        {
            using var world = new Fixture(preference);
            var c = world.Controller;
            var route = world.Route(new float3(3.5f, 0, 8.5f), new float3(20.5f, 0, 8.5f));
            long old = c.SnapshotGeneration;
            c.SetPresent(world.Obstacle, true);
            Assert.That(c.State, Is.EqualTo(TopologyState.Updating));
            Assert.That(c.Layered, Is.Null);
            Assert.That(c.TryRouteNode(route, 0, out _), Is.False);
            Ready(c);
            Assert.That(c.SnapshotGeneration, Is.Not.EqualTo(old));
            Assert.That(route.Status, Is.EqualTo(RouteValidity.Invalid));
            Assert.That(c.AppliedStrategy, Is.EqualTo(preference == TopologyStrategy.Partial ? TopologyStrategy.Partial : TopologyStrategy.Full));
            world.CheckFresh();
            var detour = world.Search(new float3(3.5f, 0, 8.5f), new float3(20.5f, 0, 8.5f)).Cost;
            Assert.That(detour, Is.GreaterThan(17));
            c.SetPresent(world.Obstacle, false);
            Ready(c);
            world.CheckFresh();
            Assert.That(world.Search(new float3(3.5f, 0, 8.5f), new float3(20.5f, 0, 8.5f)).Cost, Is.EqualTo(17));

            for (int i = 0; i < 2; i++)
            {
                var above = world.Route(new float3(10.5f, 2, 5.5f), new float3(13.5f, 2, 5.5f));
                var below = world.Route(new float3(10.5f, 0, 5.5f), new float3(13.5f, 0, 5.5f));
                c.SetPresent(world.Bridge, false);
                c.SetPresent(world.Shortcut, true);
                Ready(c);
                Assert.That(above.Status, Is.EqualTo(RouteValidity.Invalid));
                Assert.That(below.Status, Is.EqualTo(RouteValidity.ValidUnverified));
                world.CheckFresh();
                Assert.That(world.Search(new float3(3.5f, 0, 8.5f), new float3(20.5f, 0, 8.5f)).Cost, Is.EqualTo(.5));
                Assert.That(world.Search(new float3(20.5f, 0, 8.5f), new float3(3.5f, 0, 8.5f)).Cost,
                    Is.EqualTo(17));
                c.SetPresent(world.Bridge, true);
                c.SetPresent(world.Shortcut, false);
                Ready(c);
                world.CheckFresh();
                Assert.That(world.Search(new float3(10.5f, 2, 5.5f), new float3(13.5f, 2, 5.5f)).Cost, Is.EqualTo(3));
            }
        }

        [Test]
        public void LinkOnlySeparatesTraversabilityAndOptimalityAndChecksExternalEdits()
        {
            using var world = new Fixture(TopologyStrategy.LinkOnly);
            var c = world.Controller;
            var from = new float3(3.5f, 0, 8.5f); var goal = new float3(20.5f, 0, 8.5f);
            var original = world.Route(from, goal);
            c.SetPresent(world.Shortcut, true);
            Ready(c);
            Assert.That(c.AppliedStrategy, Is.EqualTo(TopologyStrategy.LinkOnly));
            Assert.That(c.LastSampling.Rays, Is.Zero);
            Assert.That(original.Status, Is.EqualTo(RouteValidity.ValidUnverified));
            Assert.That(c.TryRouteNode(original, 0, out var remapped), Is.True);
            Assert.That(remapped.Generation, Is.EqualTo(c.SnapshotGeneration));
            Assert.That(c.TryRouteEdge(original, 0, out var remappedEdge), Is.True);
            Assert.That(remappedEdge.Generation, Is.EqualTo(c.SnapshotGeneration));
            var independent = world.Route(new float3(2.5f, 0, 2.5f), new float3(4.5f, 0, 2.5f));
            var approximate = world.Route(new float3(2.5f, 0, 3.5f), new float3(4.5f, 0, 3.5f), false);
            c.SetPresent(world.Shortcut, false);
            Ready(c);
            Assert.That(independent.Status, Is.EqualTo(RouteValidity.ValidOptimal));
            Assert.That(approximate.Status, Is.EqualTo(RouteValidity.ValidUnverified));
            world.CheckFresh();
            world.Obstacle.Colliders[0].enabled = true; // observable external mutation preceding the command
            c.SetPresent(world.Shortcut, true);
            Ready(c);
            Assert.That(c.AppliedStrategy, Is.EqualTo(TopologyStrategy.Full));
            world.CheckFresh();
        }

        [TestCase(6.999f)]
        [TestCase(7.001f)]
        public void PartialRepairMatchesFreshAtACrossCellProbeBoundary(float obstacleZ)
        {
            using var world = new Fixture(TopologyStrategy.Partial, obstaclePosition: new Vector3(12f, .5f, obstacleZ),
                density: 4, obstacleSize: .08f);
            var c = world.Controller;
            int originalEdges = c.Layered.EdgeCount;
            c.SetPresent(world.Obstacle, true);
            Ready(c);
            Assert.That(c.AppliedStrategy, Is.EqualTo(TopologyStrategy.Partial));
            Assert.That(c.Layered.EdgeCount, Is.LessThan(originalEdges));
            Assert.That(c.Dirty.RepairCount, Is.GreaterThan(c.Dirty.SampleCount));
            world.CheckFresh();
            c.SetPresent(world.Obstacle, false);
            Ready(c);
            Assert.That(c.Layered.EdgeCount, Is.EqualTo(originalEdges));
            world.CheckFresh();
        }

        [Test]
        public void BuildFailureAfterColliderMutationIsClosedAndRollbackOrRetryRecovers()
        {
            using var world = new Fixture(TopologyStrategy.Partial);
            var c = world.Controller;
            var old = c.Layered;
            c.BudgetOverride = c.AccountedBytes + 5000;
            c.SetPresent(world.Bridge, false);
            c.Pump();
            Assert.That(c.State, Is.EqualTo(TopologyState.Blocked), c.Diagnostic);
            Assert.That(world.Bridge.Colliders[0].enabled, Is.False);
            Assert.That(c.Layered, Is.Null);
            Assert.That(old.AcceptsQueries, Is.False);
            Assert.That(c.Diagnostic, Does.Contain("Retry"));
            c.BudgetOverride = 0;
            c.Rollback();
            Ready(c);
            Assert.That(world.Bridge.Colliders[0].enabled, Is.True);
            world.CheckFresh();

            c.BudgetOverride = c.AccountedBytes + 5000;
            c.SetPresent(world.Bridge, false);
            c.Pump();
            Assert.That(c.State, Is.EqualTo(TopologyState.Blocked));
            c.BudgetOverride = 0;
            c.Retry();
            Ready(c);
            Assert.That(world.Bridge.Colliders[0].enabled, Is.False);
            world.CheckFresh();
        }

        [Test]
        public void PendingLayeredAStarFlowAndSchedulerCannotPublishAcrossRevision()
        {
            using var world = new Fixture(TopologyStrategy.Partial);
            var c = world.Controller;
            var old = c.Layered;
            Assert.That(old.Resolve(new float3(10.5f, 2, 5.5f), .1f, out var from), Is.True);
            Assert.That(old.Resolve(new float3(13.5f, 2, 5.5f), .1f, out var to), Is.True);
            using var search = new LayeredSearchWorkspace(old.NodeCount);
            var query = search.Schedule(old, from.Id, to.Id);
            using var field = new LayeredFlowField(old, to, true, 256 * 1024);
            using var scheduler = new NavigationPathScheduler(2, 256 * 1024, 8, 4, 16);
            c.BindScheduler(scheduler);
            try
            {
                scheduler.SubmitLayered(from, to, out _);
                scheduler.Tick(2, 2, 64);
                c.SetPresent(world.Bridge, false);
                RtsLayeredNavigationTests.Wait(() => query.IsCompleted);
                Assert.That(search.Finish().Status, Is.EqualTo(NavigationPathStatus.Invalidated));
                bool ready = true;
                RtsLayeredNavigationTests.Wait(() => field.TryFinish(out ready));
                Assert.That(ready, Is.False);
                scheduler.Tick(2, 2, 64);
                Assert.That(scheduler.TryTake(out _), Is.False);
                Ready(c);
                Assert.That(c.Layered.Matches(from), Is.False);
                world.CheckFresh();
            }
            finally { c.UnbindScheduler(scheduler); }
        }

        [Test]
        public void PendingSingleLayerAStarFlowAndHierarchyRejectBeforePublication()
        {
            using var world = new Fixture(TopologyStrategy.Full, layered: false);
            var c = world.Controller;
            var grid = c.Single;
            using var search = new NavigationPathfinder(grid.Size.x * grid.Size.y);
            var searchJob = search.Schedule(grid, new int2(2, 2), new int2(22, 14));
            using var flow = new NavigationFlowFieldBuilder(grid.Size.x * grid.Size.y);
            flow.Begin(grid, new int2(22, 14), true);
            using var hierarchy = new NavigationHierarchyBuilder();
            hierarchy.Begin(grid, new int2(8), NavigationPortalMode.Exhaustive, 2, 8 * 1024 * 1024, true);
            using var completedHierarchy = RtsNavigationHierarchyTests.Build(grid, new int2(8),
                NavigationPortalMode.Representative);
            using var hierarchicalSearch = new NavigationHierarchyPathfinder(completedHierarchy,
                grid.Size.x * grid.Size.y, 8 * 1024 * 1024);
            Assert.That(hierarchicalSearch.Begin(grid, completedHierarchy, new int2(2, 2), new int2(22, 14), true),
                Is.True);
            c.SetPresent(world.Obstacle, true);
            JobHandle.ScheduleBatchedJobs();
            RtsLayeredNavigationTests.Wait(() => searchJob.IsCompleted);
            searchJob.Complete();
            Assert.That(search.ReadResult().Status, Is.EqualTo(NavigationPathStatus.Invalidated));
            NavigationFlowBuildStatus flowStatus = default;
            RtsLayeredNavigationTests.Wait(() => flow.TryFinish(grid, out flowStatus, out _));
            Assert.That(flowStatus, Is.EqualTo(NavigationFlowBuildStatus.Invalidated));
            NavigationHierarchyBuildStatus hierarchyStatus = default;
            RtsLayeredNavigationTests.Wait(() => hierarchy.TryFinish(grid, out hierarchyStatus, out _));
            Assert.That(hierarchyStatus, Is.EqualTo(NavigationHierarchyBuildStatus.Invalidated));
            NavigationHierarchyResult hierarchicalResult = default;
            RtsLayeredNavigationTests.Wait(() => hierarchicalSearch.TryFinish(grid, completedHierarchy,
                out hierarchicalResult));
            Assert.That(hierarchicalResult.Status, Is.EqualTo(NavigationHierarchyStatus.Invalidated));
            Ready(c);
            Assert.That(c.Single.Generation, Is.Not.EqualTo(grid.Generation));
        }

        [Test]
        public void BoundedRetirementCoalescesAnEditAndUnloadsAfterConsumers()
        {
            using var world = new Fixture(TopologyStrategy.Partial);
            var c = world.Controller;
            var snapshot = c.Layered;
            var slow = new SlowReader().Schedule();
            snapshot.RegisterReader(slow);
            JobHandle.ScheduleBatchedJobs();
            c.SetPresent(world.Obstacle, true);
            Ready(c);
            c.SetPresent(world.Obstacle, false);
            c.SetPresent(world.Obstacle, true);
            c.SetPresent(world.Obstacle, false);
            Ready(c);
            Assert.That(c.Coalesced, Is.GreaterThanOrEqualTo(2));
            Assert.That(world.Obstacle.Colliders[0].enabled, Is.False);
            Assert.That(c.Published, Is.EqualTo(3));
            Assert.That(slow.IsCompleted, Is.True);
            world.CheckFresh();
        }

        [UnityTest]
        public IEnumerator SceneUnloadRetiresAnOutstandingReaderAndCancelsEdits()
        {
            using var world = new Fixture(TopologyStrategy.Partial);
            var c = world.Controller;
            var graph = c.Layered;
            var reader = new SlowReader().Schedule();
            graph.RegisterReader(reader);
            JobHandle.ScheduleBatchedJobs();
            c.SetPresent(world.Obstacle, true);
            yield return SceneManager.UnloadSceneAsync(world.Scene);
            Assert.That(graph.AcceptsQueries, Is.False);
            Assert.That(reader.IsCompleted, Is.True);
        }

        private struct SlowReader : IJob
        {
            public void Execute() => Thread.Sleep(120);
        }

        internal static void Ready(NavigationTopologyController controller)
        {
            RtsLayeredNavigationTests.Wait(() =>
            {
                controller.Pump();
                if (controller.State == TopologyState.Blocked) Assert.Fail(controller.Diagnostic);
                return controller.State == TopologyState.Ready;
            });
        }

        internal sealed class Fixture : IDisposable
        {
            internal readonly Scene Scene;
            internal readonly NavigationTopologyController Controller;
            internal readonly NavigationDynamicElement Obstacle, Bridge, Shortcut, SecondObstacle;
            internal readonly LayerNavigationSettings Settings;
            internal readonly LayerNavigationLink[] Links;
            internal Fixture(TopologyStrategy preference, bool layered = true, int width = 24, int depth = 16,
                Vector3? obstaclePosition = null, int density = 1, float obstacleSize = .65f)
            {
                Scene = SceneManager.CreateScene("Dynamic navigation " + Guid.NewGuid(),
                    new CreateSceneParameters(LocalPhysicsMode.Physics3D));
                Box("Floor", 1, 0, new Vector3(width / 2f, -.5f, depth / 2f), new Vector3(width + 2, 1, depth + 2));
                var deck = Box("Deck", 2, 2, new Vector3(12, 1.9f, 5), new Vector3(7, .2f, 3));
                var obstruction = Box("Obstacle", 0, 0, obstaclePosition ?? new Vector3(12.5f, .5f, 8.5f),
                    new Vector3(obstacleSize, 1, obstacleSize));
                var second = Box("Second obstacle", 0, 0, new Vector3(width - 4.13f, .5f, depth - 3.12f), new Vector3(.62f, 1, .62f));
                obstruction.enabled = second.enabled = false;
                var bridgeAccess = Link(12, 2, new Vector3(9.5f, 0, 3.5f), new Vector3(9.5f, 2, 5.5f), 4);
                var shortcut = Link(15, 3, new Vector3(3.5f, 0, 8.5f), new Vector3(20.5f, 0, 8.5f), .5f);
                shortcut.enabled = false;
                Obstacle = Element(1, new[] { obstruction }, Array.Empty<LayerNavigationLink>());
                Bridge = Element(2, new[] { deck }, new[] { bridgeAccess });
                Shortcut = Element(3, Array.Empty<Collider>(), new[] { shortcut });
                SecondObstacle = Element(4, new[] { second }, Array.Empty<LayerNavigationLink>());
                Links = new[] { bridgeAccess, shortcut };
                var settings = LayerNavigationSettings.Default;
                settings.Grid.Size = new Vector2Int(width, depth);
                settings.Grid.Blockers = 1 << 2;
                settings.Grid.SampleDensity = density;
                settings.Grid.AgentRadius = .18f;
                Settings = settings;
                Controller = GameObject("Controller").AddComponent<NavigationTopologyController>();
                Set(Controller, "_elements", new[] { Obstacle, Bridge, Shortcut, SecondObstacle });
                Set(Controller, "_links", Links);
                Set(Controller, "_settings", Settings);
                Set(Controller, "_strategy", preference);
                Set(Controller, "_layered", layered);
                Controller.BuildFull();
                Ready(Controller);
            }

            internal void CheckFresh()
            {
                using var fresh = new LayeredNavigationBuilder(Scene.GetPhysicsScene(), float3.zero, Settings,
                    96L * 1024 * 1024).Build((LayerNavigationLink[])Links.Clone(), out _);
                var actual = Controller.Layered;
                Assert.That(actual.NodeCount, Is.EqualTo(fresh.NodeCount));
                Assert.That(actual.EdgeCount, Is.EqualTo(fresh.EdgeCount));
                var a = actual.View(); var b = fresh.View();
                Assert.That(a.Outgoing[a.Nodes.Length], Is.EqualTo(b.Outgoing[b.Nodes.Length]));
                Assert.That(a.Incoming[a.Nodes.Length], Is.EqualTo(b.Incoming[b.Nodes.Length]));
                for (int i = 0; i <= Settings.Grid.Size.x * Settings.Grid.Size.y; i++)
                    Assert.That(actual.ColumnStart(i), Is.EqualTo(fresh.ColumnStart(i)));
                for (int i = 0; i < a.Nodes.Length; i++)
                {
                    Assert.That(a.Nodes[i], Is.EqualTo(b.Nodes[i]));
                    Assert.That(a.Outgoing[i], Is.EqualTo(b.Outgoing[i]));
                    Assert.That(a.Incoming[i], Is.EqualTo(b.Incoming[i]));
                }
                for (int i = 0; i < a.Edges.Length; i++)
                {
                    Assert.That(a.Edges[i], Is.EqualTo(b.Edges[i]));
                    Assert.That(a.IncomingEdges[i], Is.EqualTo(b.IncomingEdges[i]));
                }
            }

            internal NavigationPathResult Search(float3 from, float3 goal)
            {
                var graph = Controller.Layered;
                Assert.That(graph.Resolve(from, .1f, out var a), Is.True);
                Assert.That(graph.Resolve(goal, .1f, out var b), Is.True);
                using var search = new LayeredSearchWorkspace(graph.NodeCount);
                return search.Search(graph, a, b);
            }

            internal NavigationRouteCertificate Route(float3 from, float3 goal, bool optimal = true)
            {
                var graph = Controller.Layered;
                Assert.That(graph.Resolve(from, .1f, out var a), Is.True);
                Assert.That(graph.Resolve(goal, .1f, out var b), Is.True);
                using var search = new LayeredSearchWorkspace(graph.NodeCount);
                var result = search.Search(graph, a, b);
                var route = Controller.TrackPath(search, result, Controller.Revision, graph.Generation, optimal);
                Assert.That(route, Is.Not.Null);
                return route;
            }

            private GameObject GameObject(string name)
            {
                var obj = new GameObject(name);
                SceneManager.MoveGameObjectToScene(obj, Scene);
                return obj;
            }

            private Collider Box(string name, int key, int owner, Vector3 position, Vector3 size)
            {
                var obj = GameObject(name);
                obj.transform.position = position;
                var collider = obj.AddComponent<BoxCollider>(); collider.size = size;
                if (key == 0) obj.layer = 2;
                else
                {
                    var surface = obj.AddComponent<LayerNavigationSurface>();
                    Set(surface, "_stableKey", key); Set(surface, "_owner", owner);
                }
                return collider;
            }

            private LayerNavigationLink Link(int key, int owner, Vector3 from, Vector3 to, float cost)
            {
                var link = GameObject("Link " + key).AddComponent<LayerNavigationLink>();
                var source = GameObject("Source"); source.transform.position = from;
                var goal = GameObject("Goal"); goal.transform.position = to;
                Set(link, "_source", source.transform); Set(link, "_destination", goal.transform);
                Set(link, "_stableKey", key); Set(link, "_owner", owner); Set(link, "_cost", cost);
                return link;
            }

            private NavigationDynamicElement Element(int owner, Collider[] colliders, LayerNavigationLink[] links)
            {
                var element = GameObject("Owner " + owner).AddComponent<NavigationDynamicElement>();
                Set(element, "_owner", owner); Set(element, "_colliders", colliders); Set(element, "_links", links);
                return element;
            }

            internal static void Set(object value, string field, object data) =>
                value.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(value, data);

            public void Dispose()
            {
                if (!Scene.isLoaded) return;
                foreach (var root in Scene.GetRootGameObjects()) UnityEngine.Object.DestroyImmediate(root);
                SceneManager.UnloadSceneAsync(Scene);
            }
        }
    }
}
