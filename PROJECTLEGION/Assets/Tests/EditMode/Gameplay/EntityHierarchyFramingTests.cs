using System.Reflection;
using NUnit.Framework;
using ProjectLegion.Gameplay.Editor;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.UIElements;

namespace ProjectLegion.Gameplay.Tests
{
    public class EntityHierarchyFramingTests
    {
        [Test]
        public void AdapterBindsInstalledEntitiesEditorAndRejectsUnrelatedElements()
        {
            var assembly = Assembly.Load("Unity.Entities.Editor");
            Assert.That(EntityHierarchyRowAdapter.IsHierarchyWindow(
                assembly.GetType("Unity.Entities.Editor.HierarchyWindow", true)), Is.True);
            var adapter = EntityHierarchyRowAdapter.Bind(assembly);
            Assert.That(adapter, Is.Not.Null);
            Assert.That(adapter.TryGetEntity(new Label("Entity"), out _, out _), Is.False);
            Assert.That(EntityHierarchyRowAdapter.Bind(typeof(EntityHierarchyFramingTests).Assembly), Is.Null);
        }

        [Test]
        public void UsesOnlyTheRequestedEntityWorldRenderBounds()
        {
            using var world = new World("Framing bounds test");
            var manager = world.EntityManager;
            var first = manager.CreateEntity(typeof(WorldRenderBounds));
            var second = manager.CreateEntity(typeof(WorldRenderBounds));
            manager.SetComponentData(first, new WorldRenderBounds
            {
                Value = new AABB { Center = new float3(1, 2, 3), Extents = new float3(2) }
            });
            manager.SetComponentData(second, new WorldRenderBounds
            {
                Value = new AABB { Center = new float3(10, 20, 30), Extents = new float3(4) }
            });

            Assert.That(EntityHierarchyFraming.TryGetBounds(world, second, out var bounds), Is.True);
            Assert.That(bounds.center, Is.EqualTo(new Vector3(10, 20, 30)));
            Assert.That(bounds.size, Is.EqualTo(Vector3.one * 8));
        }

        [Test]
        public void FallsBackToWorldPositionWhenRenderBoundsAreDegenerate()
        {
            using var world = new World("Framing fallback test");
            var manager = world.EntityManager;
            var entity = manager.CreateEntity(typeof(WorldRenderBounds), typeof(LocalToWorld));
            manager.SetComponentData(entity, new LocalToWorld
            {
                Value = float4x4.Translate(new float3(5, 6, 7))
            });

            Assert.That(EntityHierarchyFraming.TryGetBounds(world, entity, out var bounds), Is.True);
            Assert.That(bounds.center, Is.EqualTo(new Vector3(5, 6, 7)));
            Assert.That(bounds.size, Is.EqualTo(Vector3.one));
        }

        [Test]
        public void RejectsNonSpatialPrefabDestroyedAndDisposedTargets()
        {
            var world = new World("Framing invalid target test");
            Entity destroyed;
            using (world)
            {
                var manager = world.EntityManager;
                var nonSpatial = manager.CreateEntity();
                var prefab = manager.CreateEntity(typeof(Prefab), typeof(LocalToWorld));
                destroyed = manager.CreateEntity(typeof(LocalToWorld));
                manager.DestroyEntity(destroyed);
                Assert.That(EntityHierarchyFraming.TryGetBounds(world, nonSpatial, out _), Is.False);
                Assert.That(EntityHierarchyFraming.TryGetBounds(world, prefab, out _), Is.False);
                Assert.That(EntityHierarchyFraming.TryGetBounds(world, destroyed, out _), Is.False);
            }

            Assert.That(EntityHierarchyFraming.TryGetBounds(world, destroyed, out _), Is.False);
        }
    }
}
