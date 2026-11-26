#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Scenes;
using Unity.Transforms;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsUnitTests
    {
        private const int WaitFrameLimit = 300;

        [TestCase(100)]
        [TestCase(5000)]
        [TestCase(10000)]
        public void BatchProducesUniqueIdsAndConsumesTheRequest(int count)
        {
            using var world = new World("Unit spawn test");
            EntityManager manager = world.EntityManager;
            Entity prefab = manager.CreateEntity(typeof(Prefab), typeof(UnitTag), typeof(UnitId),
                typeof(UnitStats), typeof(LocalTransform));
            manager.SetComponentData(prefab, LocalTransform.Identity);
            manager.SetComponentData(prefab, new UnitStats { MovementSpeed = 4f });
            Entity request = manager.CreateEntity(typeof(UnitSpawnRequest));
            manager.SetComponentData(request, new UnitSpawnRequest
            {
                Prefab = prefab,
                Count = count,
                Spacing = 0.7f,
                Origin = new float3(1, 0, 19)
            });

            var system = world.GetOrCreateSystemManaged<UnitSpawnSystem>();
            using var query = manager.CreateEntityQuery(ComponentType.ReadOnly<UnitTag>(), ComponentType.ReadOnly<UnitId>());
            system.Update();
            system.Update();
            Assert.That(manager.HasComponent<UnitSpawnRequest>(request), Is.False);
            Assert.That(query.CalculateEntityCount(), Is.EqualTo(count));
            using var ids = query.ToComponentDataArray<UnitId>(Allocator.Temp);
            var seen = new HashSet<ulong>();
            foreach (UnitId id in ids)
                if (id.Value < 1 || id.Value > (ulong)count || !seen.Add(id.Value))
                    Assert.Fail($"Duplicate or invalid unit ID {id.Value}.");
        }

        [UnityTest]
        public IEnumerator PrototypeSubSceneSpawnsRendersUnloadsAndReloadsWithoutDuplicates()
        {
            Scene previous = SceneManager.GetActiveScene();
            Scene prototype = EditorSceneManager.LoadSceneInPlayMode(
                "Assets/Gameplay/Scenes/RtsPrototype.unity", new LoadSceneParameters(LoadSceneMode.Additive));
            RenderTexture target = null;
            try
            {
                yield return null;
                World world = World.DefaultGameObjectInjectionWorld;
                Assert.That(world, Is.Not.Null);
                var manager = world.EntityManager;
                using var units = manager.CreateEntityQuery(ComponentType.ReadOnly<UnitTag>(), ComponentType.ReadOnly<UnitId>());
                using var renderable = manager.CreateEntityQuery(ComponentType.ReadOnly<UnitTag>(),
                    ComponentType.ReadOnly<MaterialMeshInfo>());
                using var selected = manager.CreateEntityQuery(ComponentType.ReadOnly<UnitTag>(),
                    ComponentType.ReadOnly<UnitSelected>());
                using var selectable = new EntityQueryBuilder(Allocator.Temp).WithAll<UnitTag, UnitSelected>()
                    .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState).Build(manager);
                yield return WaitForCount(units, 1000);
                Assert.That(renderable.CalculateEntityCount(), Is.EqualTo(1000));
                Assert.That(selectable.CalculateEntityCount(), Is.EqualTo(1000));
                Assert.That(selected.CalculateEntityCount(), Is.Zero);
                Assert.That(Object.FindObjectsByType<UnitAuthoring>(FindObjectsInactive.Include, FindObjectsSortMode.None), Is.Empty);
                var subScene = GameObject.Find("RTS Units").GetComponent<SubScene>();
                Camera camera = GameObject.Find("RTS Camera Rig").GetComponentInChildren<Camera>();

                ulong firstId = ulong.MaxValue;
                using (var range = units.ToEntityArray(Allocator.Temp))
                {
                    foreach (Entity entity in range)
                        firstId = System.Math.Min(firstId, manager.GetComponentData<UnitId>(entity).Value);

                    Assert.That(firstId, Is.Positive);
                    var seen = new HashSet<ulong>();
                    foreach (Entity entity in range)
                    {
                        ulong id = manager.GetComponentData<UnitId>(entity).Value;
                        ulong gridIndex = id - firstId;
                        Assert.That(gridIndex, Is.LessThan(1000ul));
                        Assert.That(seen.Add(id), Is.True, $"Duplicate ID {id}");
                        Assert.That(manager.GetComponentData<UnitStats>(entity).MovementSpeed, Is.EqualTo(4f));
                        if (gridIndex == 0 || gridIndex == 31 || gridIndex == 999)
                        {
                            float3 actual = manager.GetComponentData<LocalTransform>(entity).Position;
                            float x = gridIndex == 0 ? -10.85f : gridIndex == 31 ? 10.85f : 2.45f;
                            float z = gridIndex == 999 ? 29.85f : 8.15f;
                            Assert.That(actual.x, Is.EqualTo(x).Within(0.001f));
                            Assert.That(actual.z, Is.EqualTo(z).Within(0.001f));
                        }
                    }
                }

                // Allow presentation systems to register the new entities before requesting a render.
                yield return null;
                yield return null;
                Assert.That(units.CalculateEntityCount(), Is.EqualTo(1000));
                target = new RenderTexture(640, 360, 24, RenderTextureFormat.ARGB32);
                Assert.That(target.Create(), Is.True);
                Color32[] populated = Capture(camera, target);

                subScene.enabled = false;
                yield return WaitForCount(units, 0);
                yield return null;
                yield return null;
                Color32[] empty = Capture(camera, target);
                int visibleUnitPixels = CountMintPixels(populated);
                int backgroundPixels = CountMintPixels(empty);
                Debug.Log($"RTS render verification: {visibleUnitPixels} mint pixels with units, {backgroundPixels} without.");
                Assert.That(visibleUnitPixels - backgroundPixels, Is.GreaterThan(100));

                subScene.enabled = true;
                yield return WaitForCount(units, 1000);
                yield return null;
                Assert.That(units.CalculateEntityCount(), Is.EqualTo(1000));
                using var reloadedIds = units.ToComponentDataArray<UnitId>(Allocator.Temp);
                var reloaded = new HashSet<ulong>();
                foreach (UnitId id in reloadedIds)
                {
                    Assert.That(id.Value, Is.GreaterThan(firstId + 999ul));
                    Assert.That(reloaded.Add(id.Value), Is.True, $"Duplicate reloaded ID {id.Value}");
                }
            }
            finally
            {
                if (target != null)
                    Object.Destroy(target);
                if (previous.IsValid() && previous.isLoaded)
                    SceneManager.SetActiveScene(previous);
                SceneManager.UnloadSceneAsync(prototype);
            }
        }

        private static IEnumerator WaitForCount(EntityQuery query, int expected)
        {
            for (int i = 0; i < WaitFrameLimit && query.CalculateEntityCount() != expected; i++)
                yield return null;

            Assert.That(query.CalculateEntityCount(), Is.EqualTo(expected));
        }

        private static Color32[] Capture(Camera camera, RenderTexture target)
        {
            RenderPipeline.SubmitRenderRequest(camera, new UniversalRenderPipeline.SingleCameraRequest { destination = target });
            RenderTexture previous = RenderTexture.active;
            var image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
            try
            {
                RenderTexture.active = target;
                image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
                return image.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                Object.Destroy(image);
            }
        }

        private static int CountMintPixels(Color32[] pixels)
        {
            int count = 0;
            foreach (Color32 pixel in pixels)
                if (pixel.g - pixel.r > 30 && pixel.b - pixel.r > 12 && pixel.g - pixel.b > 10)
                    count++;
            return count;
        }
    }
}
#endif
