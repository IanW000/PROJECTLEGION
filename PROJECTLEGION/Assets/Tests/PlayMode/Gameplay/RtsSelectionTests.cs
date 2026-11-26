using NUnit.Framework;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsSelectionTests
    {
        [Test]
        public void ClickSelectsNearestTogglesAndClearsOnMiss()
        {
            using var world = new World("RTS click selection test");
            var manager = world.EntityManager;
            var selection = world.GetOrCreateSystemManaged<UnitSelectionSystem>();
            Entity far = CreateUnit(manager, new float3(0, 0, 12), 1);
            Entity near = CreateUnit(manager, new float3(0, 0, 6), 2);
            Entity side = CreateUnit(manager, new float3(3, 0, 6), 3);
            using var selected = manager.CreateEntityQuery(typeof(UnitTag), typeof(UnitSelected));
            Assert.That(selected.CalculateEntityCount(), Is.Zero);

            var centerRay = new Ray(Vector3.zero, Vector3.forward);
            var sideRay = new Ray(Vector3.right * 3, Vector3.forward);
            selection.SelectSingle(centerRay, 100f, false);
            Assert.That(selected.CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(manager.IsComponentEnabled<UnitSelected>(near), Is.True);
            Assert.That(manager.IsComponentEnabled<UnitSelected>(far), Is.False);

            selection.SelectSingle(sideRay, 100f, true);
            Assert.That(selected.CalculateEntityCount(), Is.EqualTo(2));
            selection.SelectSingle(centerRay, 100f, true);
            Assert.That(selected.CalculateEntityCount(), Is.EqualTo(1));
            Assert.That(manager.IsComponentEnabled<UnitSelected>(side), Is.True);
            selection.SelectSingle(centerRay, 3f, false);
            Assert.That(selected.CalculateEntityCount(), Is.Zero);

            selection.SelectSingle(sideRay, 100f, false);
            selection.SelectSingle(new Ray(Vector3.up * 4, Vector3.forward), 100f, true);
            Assert.That(selected.CalculateEntityCount(), Is.Zero);
            Assert.That(manager.HasComponent<UnitSelected>(side), Is.True);
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void BoxSelectsInAllDirectionsAndExcludesUnitsBehindCamera(bool reverseX, bool reverseY)
        {
            using var world = new World("RTS box selection test");
            var manager = world.EntityManager;
            var selection = world.GetOrCreateSystemManaged<UnitSelectionSystem>();
            var cameraObject = new GameObject("Selection camera");
            var target = new RenderTexture(320, 240, 0);
            try
            {
                var camera = cameraObject.AddComponent<Camera>();
                camera.enabled = false;
                camera.targetTexture = target;
                Entity inside = CreateUnit(manager, new float3(0, 0, 10), 1);
                Entity outside = CreateUnit(manager, new float3(3, 0, 10), 2);
                Entity behind = CreateUnit(manager, new float3(0, 0, -10), 3);
                using var selected = manager.CreateEntityQuery(typeof(UnitTag), typeof(UnitSelected));

                Vector2 center = camera.WorldToScreenPoint(new Vector3(0, 0, 10));
                Vector2 offset = new Vector2(reverseX ? -10 : 10, reverseY ? -10 : 10);
                Rect rect = RtsSelectionController.RectangleBetween(center - offset, center + offset);
                manager.SetComponentEnabled<UnitSelected>(outside, true);
                selection.SelectBox(camera, rect, true);
                Assert.That(selected.CalculateEntityCount(), Is.EqualTo(2));
                Assert.That(manager.IsComponentEnabled<UnitSelected>(behind), Is.False);

                selection.SelectBox(camera, rect, false);
                Assert.That(selected.CalculateEntityCount(), Is.EqualTo(1));
                Assert.That(manager.IsComponentEnabled<UnitSelected>(inside), Is.True);
                selection.Clear();
                Assert.That(selected.CalculateEntityCount(), Is.Zero);
            }
            finally
            {
                Object.DestroyImmediate(cameraObject);
                Object.DestroyImmediate(target);
            }
        }

        private static Entity CreateUnit(EntityManager manager, float3 position, ulong id)
        {
            Entity unit = manager.CreateEntity(typeof(UnitTag), typeof(UnitId), typeof(UnitSelected),
                typeof(LocalToWorld), typeof(WorldRenderBounds));
            manager.SetComponentEnabled<UnitSelected>(unit, false);
            manager.SetComponentData(unit, new UnitId { Value = id });
            manager.SetComponentData(unit, new LocalToWorld { Value = float4x4.Translate(position) });
            manager.SetComponentData(unit, new WorldRenderBounds
            {
                Value = new AABB { Center = position, Extents = new float3(0.5f) }
            });
            return unit;
        }
    }
}
