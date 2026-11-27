using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsNavigationGridTests
    {
        [Test]
        public void FlatGridSupportsCostsAndBothConnectivityModes()
        {
            using var geometry = new Geometry();
            var left = geometry.Surface(0, 1, -1, 3, 0);
            var right = geometry.Surface(1, 3, -1, 3, 0);
            left.gameObject.AddComponent<NavigationSurfaceCost>().TraversalCost = 2;
            var material = right.gameObject.AddComponent<NavigationSurfaceCost>();
            material.TraversalCost = 4;
            var settings = Settings(2, 2);
            using var grid = geometry.Build(settings, out _);
            Assert.That(grid.TryWorldToCell(new float2(0.5f), out var origin), Is.True);
            Assert.That(grid.TryWorldToCell(new float2(-0.001f, 0f), out _), Is.False);
            Assert.That(grid.TryWorldToCell(new float2(2f, 0f), out _), Is.False);
            Assert.That(grid.TryWorldToCell(new float2(float.NaN), out _), Is.False);
            Assert.That(grid.TryGetNeighbor(origin, 0, out var east, out double cost), Is.True);
            Assert.That(east, Is.EqualTo(new int2(1, 0)));
            Assert.That(cost, Is.EqualTo(3).Within(0.00001));
            Assert.That(grid.TryGetNeighbor(east, 2, out _, out double reverse), Is.True);
            Assert.That(reverse, Is.EqualTo(cost));
            Assert.That(grid.TryGetNeighbor(origin, 4, out _, out double diagonal), Is.True);
            Assert.That(diagonal, Is.EqualTo(3 * Math.Sqrt(2)).Within(0.00001));
            Assert.That(grid.PayloadBytes, Is.EqualTo(40));
            settings.Diagonals = false;
            using var cardinal = geometry.Build(settings, out _);
            Assert.That(cardinal.IsConnected(origin, 4), Is.False);
            Assert.That(Reachable(cardinal, origin, new int2(1, 1)), Is.True);
            material.TraversalCost = float.NaN;
            Assert.That(material.TraversalCost, Is.EqualTo(1));
            material.TraversalCost = 200;
            Assert.That(material.TraversalCost, Is.EqualTo(64));
            material.TraversalCost = -2;
            Assert.That(material.TraversalCost, Is.EqualTo(1));
        }

        [TestCase(0.5f, true)]
        [TestCase(1f, false)]
        public void SlopeLimitAndHeightAwareEdgeCosts(float grade, bool expected)
        {
            using var geometry = new Geometry();
            geometry.Ramp(-1, 3, -1, 2, grade);
            var settings = Settings(2, 1);
            using var grid = geometry.Build(settings, out _);
            Assert.That(grid.TryGetNode(int2.zero, out var node), Is.True);
            Assert.That(node.HasSurface, Is.True);
            Assert.That(node.Walkable, Is.EqualTo(expected));
            Assert.That(node.Height, Is.EqualTo(0.5f * grade).Within(0.0001f));
            Assert.That(grid.TryGetNeighbor(int2.zero, 0, out _, out double cost), Is.EqualTo(expected));
            if (expected)
                Assert.That(cost, Is.EqualTo(Math.Sqrt(1 + grade * grade)).Within(0.0001));
        }

        [TestCase(0.2f, true)]
        [TestCase(0.35f, false)]
        [TestCase(-0.35f, false)]
        public void WalkableEndpointsDoNotImplyAnEdge(float height, bool connected)
        {
            using var geometry = new Geometry();
            geometry.Surface(-1, 1, -1, 2, 0);
            geometry.Surface(1, 3, -1, 2, height);
            using var grid = geometry.Build(Settings(2, 1), out _);
            Assert.That(grid.TryGetNode(int2.zero, out var a) && a.Walkable, Is.True);
            Assert.That(grid.TryGetNode(new int2(1, 0), out var b) && b.Walkable, Is.True);
            Assert.That(Reachable(grid, int2.zero, new int2(1, 0)), Is.EqualTo(connected));
        }

        [Test]
        public void FloodFillReachesAnUpperPlateauViaTheRampWithoutCrossingTheCliff()
        {
            using var geometry = new Geometry();
            geometry.Surface(-1, 1, -1, 3, 0);
            geometry.Surface(1, 3, -1, 1, 0.5f);
            geometry.Ramp(1, 2, 1, 3, 0.5f, -0.5f);
            geometry.Surface(2, 4, -1, 3, 0.5f);
            using var grid = geometry.Build(Settings(3, 2), out _);
            Assert.That(grid.IsConnected(int2.zero, 0), Is.False);
            Assert.That(grid.IsConnected(new int2(0, 1), 0), Is.True);
            Assert.That(Reachable(grid, int2.zero, new int2(2, 0)), Is.True);
            using var search = new NavigationPathfinder(6);
            var path = RtsNavigationPathfindingTests.AssertOptimal(grid, search, int2.zero, new int2(2, 0));
            Assert.That(path.Length, Is.GreaterThan(2));
            Assert.That(search.PathNode(1), Is.EqualTo(3));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CoarseSamplingCanMissAFissureOrThinBlocker(bool blocker)
        {
            using var geometry = new Geometry();
            if (blocker)
            {
                geometry.Surface(-1, 3, -1, 2, 0);
                geometry.Box(0.735f, 0.765f, -1, 2, 0, 1, true);
            }
            else
            {
                geometry.Surface(-1, 0.72f, -1, 2, 0);
                geometry.Surface(0.78f, 3, -1, 2, 0);
            }
            var settings = Settings(2, 1);
            settings.AgentRadius = 0.02f;
            settings.SampleDensity = 1;
            using var coarse = geometry.Build(settings, out var coarseMetrics);
            Assert.That(Reachable(coarse, int2.zero, new int2(1, 0)), Is.True);
            settings.SampleDensity = 2;
            using var dense = geometry.Build(settings, out var denseMetrics);
            Assert.That(Reachable(dense, int2.zero, new int2(1, 0)), Is.False);
            // The finer build can reject the edge early and therefore cast fewer rays.
            Assert.That(denseMetrics.SampleBufferBytes, Is.GreaterThan(coarseMetrics.SampleBufferBytes));
            settings.SampleDensity = 1;
            settings.CellSize = 0.5f;
            settings.Size = new Vector2Int(4, 1);
            using var smaller = geometry.Build(settings, out _);
            Assert.That(Reachable(smaller, int2.zero, new int2(3, 0)), Is.False);
        }

        [Test]
        public void DiagonalRequiresBothOrthogonalRoutes()
        {
            using var geometry = new Geometry();
            geometry.Surface(-1, 3, -1, 3, 0);
            geometry.Box(0.98f, 1.02f, 0.38f, 0.62f, 0, 1, true);
            using var grid = geometry.Build(Settings(2, 2), out _);
            Assert.That(grid.TryGetNode(new int2(1, 0), out var east) && east.Walkable, Is.True);
            Assert.That(grid.TryGetNode(new int2(1, 1), out var diagonal) && diagonal.Walkable, Is.True);
            Assert.That(grid.IsConnected(int2.zero, 0), Is.False);
            Assert.That(grid.IsConnected(int2.zero, 4), Is.False);
            Assert.That(Reachable(grid, int2.zero, new int2(1, 1)), Is.True);
            using var search = new NavigationPathfinder(4);
            var path = RtsNavigationPathfindingTests.AssertOptimal(grid, search, int2.zero, new int2(1, 1));
            Assert.That(path.PathCount, Is.EqualTo(3));
        }

        [Test]
        public void RadiusAndHeightSelectDifferentClearances()
        {
            using var geometry = new Geometry();
            geometry.Surface(-1, 3, -1, 2, 0);
            geometry.Box(-1, 3, 0, 0.25f, 0, 2, true);
            geometry.Box(-1, 3, 0.75f, 1, 0, 2, true);
            geometry.Box(-1, 3, 0.25f, 0.75f, 0.7f, 1, true);
            var settings = Settings(2, 1);
            settings.AgentHeight = 0.6f;
            using var small = geometry.Build(settings, out _);
            Assert.That(Reachable(small, int2.zero, new int2(1, 0)), Is.True);
            settings.AgentHeight = 0.8f;
            using var tall = geometry.Build(settings, out _);
            Assert.That(tall.TryGetNode(int2.zero, out var node) && node.Walkable, Is.False);
            settings.AgentRadius = 0.29f;
            settings.AgentHeight = 0.6f;
            using var wide = geometry.Build(settings, out _);
            Assert.That(wide.TryGetNode(int2.zero, out node) && node.Walkable, Is.False);
            using var search = new NavigationPathfinder(2);
            Assert.That(RtsNavigationPathfindingTests.AssertOptimal(small, search, int2.zero, new int2(1, 0))
                .PathCount, Is.EqualTo(2));
            Assert.That(search.Search(wide, int2.zero, new int2(1, 0)).Status,
                Is.EqualTo(NavigationPathStatus.InvalidStart));
            Assert.That(search.Search(small, int2.zero, new int2(2, 0)).Status,
                Is.EqualTo(NavigationPathStatus.InvalidDestination));
        }

        [TestCase(0.6f, false)]
        [TestCase(2f, true)]
        public void MultipleSurfacesKeepLowestAndDiagnoseOverhead(float upper, bool expected)
        {
            using var geometry = new Geometry();
            geometry.Surface(-1, 3, -1, 2, 0);
            geometry.Surface(-1, 3, -1, 2, upper);
            using var grid = geometry.Build(Settings(2, 1), out var metrics);
            Assert.That(grid.TryGetNode(int2.zero, out var node), Is.True);
            Assert.That(node.Height, Is.Zero.Within(0.0001f));
            Assert.That(node.Walkable, Is.EqualTo(expected));
            Assert.That(metrics.AmbiguousProbes, Is.GreaterThan(0));
        }

        [Test]
        public void InvalidSettingsAreRejectedBeforeAllocating()
        {
            using var geometry = new Geometry();
            var settings = Settings(400, 400);
            settings.SampleDensity = 8;
            Assert.Throws<ArgumentOutOfRangeException>(() => geometry.Build(settings, out _));
            settings = Settings(2, 1);
            settings.Blockers = settings.Surfaces;
            Assert.Throws<ArgumentException>(() => geometry.Build(settings, out _));
        }

        [TestCase(48, 1)]
        [TestCase(48, 2)]
        [TestCase(48, 4)]
        [TestCase(96, 1)]
        [TestCase(96, 2)]
        [TestCase(96, 4)]
        public void ReportConstructionCosts(int side, int density)
        {
            using var geometry = new Geometry();
            geometry.Surface(-2, 50, -2, 50, 0);
            for (int i = 1; i < 8; i++)
                geometry.Box(i * 6 - 0.25f, i * 6 + 0.25f, 10, 28, 0, 2, true);
            var settings = Settings(side, side);
            settings.CellSize = 48f / side;
            settings.AgentRadius = 0.35f;
            settings.SampleDensity = density;
            using (geometry.Build(settings, out _)) { }
            var times = new double[3];
            long persistent = 0;
            NavigationBuildMetrics metrics = default;
            for (int i = 0; i < times.Length; i++)
            {
                long start = Stopwatch.GetTimestamp();
                using var grid = geometry.Build(settings, out metrics);
                times[i] = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
                persistent = grid.PayloadBytes;
            }
            Array.Sort(times);
            Assert.That(persistent, Is.EqualTo(10L * side * side));
            Assert.That(metrics.SaturatedProbes, Is.Zero);
            UnityEngine.Debug.Log($"Navigation build {side}x{side} density={density}: " +
                $"cell={settings.CellSize:F3} pitch={settings.CellSize / (2 * density):F4} " +
                $"min/median/max={times[0]:F3}/{times[1]:F3}/{times[2]:F3} ms " +
                $"persistent={persistent}B temporary={metrics.SampleBufferBytes + metrics.QueryBufferPayloadBytes}B " +
                $"samples={metrics.Samples} rays={metrics.SurfaceRaycasts}");
        }

        private static NavigationGridSettings Settings(int width, int depth)
        {
            var settings = NavigationGridSettings.Default;
            settings.Size = new Vector2Int(width, depth);
            settings.AgentRadius = 0.15f;
            settings.Surfaces = 1 << 0;
            settings.Blockers = 1 << 2;
            return settings;
        }

        private static bool Reachable(NavigationGrid grid, int2 start, int2 goal)
        {
            if (!grid.TryGetNode(start, out var node) || !node.Walkable)
                return false;
            var frontier = new Queue<int2>();
            var visited = new HashSet<int2> { start };
            frontier.Enqueue(start);
            while (frontier.Count > 0)
            {
                var current = frontier.Dequeue();
                if (current.Equals(goal))
                    return true;
                for (int direction = 0; direction < 8; direction++)
                    if (grid.TryGetNeighbor(current, direction, out var next, out _) && visited.Add(next))
                        frontier.Enqueue(next);
            }
            return false;
        }

        private sealed class Geometry : IDisposable
        {
            private readonly Scene _scene = SceneManager.CreateScene("Navigation fixture " + Guid.NewGuid(),
                new CreateSceneParameters(LocalPhysicsMode.Physics3D));
            private readonly List<Object> _objects = new List<Object>();

            internal BoxCollider Surface(float x0, float x1, float z0, float z1, float height) =>
                Box(x0, x1, z0, z1, height - 0.2f, height, false);

            internal BoxCollider Box(float x0, float x1, float z0, float z1, float y0, float y1, bool blocker)
            {
                GameObject obj = CreateObject(blocker ? 2 : 0);
                obj.transform.position = new Vector3((x0 + x1) * 0.5f, (y0 + y1) * 0.5f, (z0 + z1) * 0.5f);
                var collider = obj.AddComponent<BoxCollider>();
                collider.size = new Vector3(x1 - x0, y1 - y0, z1 - z0);
                return collider;
            }

            internal void Ramp(float x0, float x1, float z0, float z1, float grade, float intercept = 0)
            {
                GameObject obj = CreateObject(0);
                var mesh = new Mesh
                {
                    vertices = new[]
                    {
                        new Vector3(x0, x0 * grade + intercept, z0), new Vector3(x1, x1 * grade + intercept, z0),
                        new Vector3(x1, x1 * grade + intercept, z1), new Vector3(x0, x0 * grade + intercept, z1)
                    },
                    triangles = new[] { 0, 2, 1, 0, 3, 2 }
                };
                _objects.Add(mesh);
                obj.AddComponent<MeshCollider>().sharedMesh = mesh;
            }

            private GameObject CreateObject(int layer)
            {
                var obj = new GameObject("Navigation probe geometry") { layer = layer };
                SceneManager.MoveGameObjectToScene(obj, _scene);
                _objects.Add(obj);
                return obj;
            }

            internal NavigationGrid Build(NavigationGridSettings settings, out NavigationBuildMetrics metrics)
            {
                Physics.SyncTransforms();
                return NavigationGridBuilder.Build(_scene.GetPhysicsScene(), Vector3.zero, settings, out metrics);
            }

            public void Dispose()
            {
                foreach (Object obj in _objects)
                    Object.DestroyImmediate(obj);
                SceneManager.UnloadSceneAsync(_scene);
            }
        }
    }
}
