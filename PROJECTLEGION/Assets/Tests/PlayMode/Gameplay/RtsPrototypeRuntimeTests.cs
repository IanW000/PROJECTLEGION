using System.Collections;
using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
#endif

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsPrototypeRuntimeTests
    {
        [Test]
        public void ResetClearsClaimsCommandsAndOldStatisticsAcrossMultipleGenerations()
        {
            using var world = new World("Prototype reset");
            var manager = world.EntityManager;
            var prototype = manager.CreateEntity(typeof(Prefab), typeof(UnitTag), typeof(UnitSelected),
                typeof(UnitId), typeof(UnitStats), typeof(LocalTransform), typeof(UnitMoveTarget),
                typeof(UnitMovementProgress), typeof(UnitArrivalState), typeof(UnitAvoidanceVelocity), typeof(UnitOverlapCorrection));
            manager.SetComponentEnabled<UnitSelected>(prototype, false);
            manager.SetComponentEnabled<UnitMoveTarget>(prototype, false);
            manager.SetComponentData(prototype, LocalTransform.Identity);
            manager.SetComponentData(prototype, new UnitStats { MovementSpeed = 4f });
            var source = manager.CreateEntity(typeof(PrototypeSpawnSource), typeof(UnitSpawnRequest));
            var request = new UnitSpawnRequest { Prefab = prototype, Count = 100, Spacing = 0.7f };
            manager.SetComponentData(source, request);
            manager.SetComponentData(source, new PrototypeSpawnSource { Request = request });
            var spawn = world.GetOrCreateSystemManaged<UnitSpawnSystem>();
            var grid = world.GetOrCreateSystemManaged<UnitSpatialGridSystem>();
            var arrival = world.GetOrCreateSystemManaged<UnitArrivalSystem>();
            var commands = world.GetOrCreateSystemManaged<UnitMoveCommandSystem>();
            var stats = world.GetOrCreateSystemManaged<PrototypeStatisticsSystem>();
            var detector = world.GetOrCreateSystemManaged<UnitStuckDetectionSystem>();
            stats.Collect = true;
            using var owned = manager.CreateEntityQuery(typeof(PrototypeUnit));
            world.SetTime(new TimeData(1, 0.05f));
            spawn.Update();
            Entity old;
            using (var units = owned.ToEntityArray(Allocator.Temp))
            {
                old = units[0];
                for (int i = 0; i < 4; i++)
                {
                    manager.SetComponentEnabled<UnitSelected>(units[i], true);
                    manager.SetComponentEnabled<UnitMoveTarget>(units[i], i < 3);
                    if (i == 1 || i == 2)
                        manager.SetComponentData(units[i], new UnitMovementProgress
                        {
                            Epoch = detector.Epoch,
                            State = i == 1 ? UnitMovementState.Waiting : UnitMovementState.Retrying
                        });
                }
            }
            manager.SetComponentData(old, new UnitArrivalState
            {
                OriginalSlot = new float2(0f, 6f), CommandVersion = 7,
                AlternativeIndex = 1, Status = ArrivalStatus.Alternative, LastProgressTime = 1
            });
            manager.SetComponentData(old, new UnitMoveTarget { DestinationXZ = new float2(1f, 6f) });
            grid.Update();
            var previousSnapshot = grid.GetSnapshot();
            arrival.Update();
            Assert.That(arrival.ActiveClaimCount, Is.GreaterThanOrEqualTo(2));
            stats.Update();
            manager.CompleteAllTrackedJobs();
            stats.Update();
            Assert.That(stats.TryGet(spawn.Generation, out var previous), Is.True);
            Assert.That(previous.Total, Is.EqualTo(100));
            Assert.That(previous.Selected, Is.EqualTo(4));
            Assert.That(previous.Moving, Is.EqualTo(1));
            Assert.That(previous.Waiting, Is.EqualTo(1));
            Assert.That(previous.Retrying, Is.EqualTo(1));
            Assert.That(previous.Idle, Is.EqualTo(97));

            world.SetTime(new TimeData(2, 0.05f));
            stats.Update();
            commands.IssueMove(new float2(8f, 8f));
            int[] presets = { 10000, 100, 5000, 1000, 100 };
            for (int i = 0; i < presets.Length; i++)
            {
                world.SetTime(new TimeData(3 + i, 0.05f));
                Assert.That(spawn.RequestReset(presets[i]), Is.True);
                Assert.That(spawn.RequestReset(100), Is.False);
                spawn.Update();
                commands.Update();
                Assert.That(manager.Exists(old), Is.False);
                Assert.That(arrival.ActiveClaimCount, Is.Zero);
                Assert.Throws<System.InvalidOperationException>(() => previousSnapshot.RegisterReader(default));
                Assert.That(owned.CalculateEntityCount(), Is.EqualTo(presets[i]));
                Assert.That(stats.TryGet(spawn.Generation, out _), Is.False);
                stats.Update();
                manager.CompleteAllTrackedJobs();
                stats.Update();
                Assert.That(stats.TryGet(spawn.Generation, out var counts), Is.True);
                Assert.That(counts.Total, Is.EqualTo(presets[i]));
                Assert.That(counts.Idle, Is.EqualTo(counts.Total));
                Assert.That(counts.Selected + counts.Waiting + counts.Retrying + counts.Moving, Is.Zero);
                using var current = owned.ToEntityArray(Allocator.Temp);
                old = current[0];
                Assert.That(manager.GetComponentData<UnitArrivalState>(old).CommandVersion, Is.Zero);
                Assert.That(manager.GetComponentData<UnitMovementProgress>(old).State, Is.EqualTo(UnitMovementState.Moving));
            }
        }

#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator PrototypePanelRaycastsAndItsControlsDriveTheLiveSimulation()
        {
            var previous = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.LoadSceneInPlayMode("Assets/Gameplay/Scenes/RtsPrototype.unity",
                new LoadSceneParameters(LoadSceneMode.Additive));
            try
            {
                yield return null;
                var world = World.DefaultGameObjectInjectionWorld;
                var spawn = world.GetExistingSystemManaged<UnitSpawnSystem>();
                var hud = Object.FindFirstObjectByType<PrototypeHud>();
                Assert.That(hud, Is.Not.Null);
                var buttons = hud.GetComponentsInChildren<Button>();
                var reset = System.Array.Find(buttons, button => button.name == "Reset simulation");
                for (int i = 0; i < 300 && !reset.interactable; i++)
                    yield return null;
                Assert.That(reset.interactable, Is.True);
                Canvas.ForceUpdateCanvases();
                var preset = System.Array.Find(buttons, button => button.name == "100");
                var rect = (RectTransform)preset.transform;
                Vector2 point = RectTransformUtility.WorldToScreenPoint(null, rect.TransformPoint(rect.rect.center));
                Assert.That(PrototypeInputGate.PointerBlocked(point), Is.True);
                var hits = new List<RaycastResult>();
                var evt = new PointerEventData(EventSystem.current) { position = point, button = PointerEventData.InputButton.Left };
                EventSystem.current.RaycastAll(evt, hits);
                Assert.That(hits.Exists(hit => hit.gameObject == preset.gameObject), Is.True);
                ExecuteEvents.Execute(preset.gameObject, evt, ExecuteEvents.pointerClickHandler);
                uint generation = spawn.Generation;
                reset.onClick.Invoke();
                Assert.That(PrototypeInputGate.ResetPending, Is.True);
                for (int i = 0; i < 120 && spawn.Generation == generation; i++)
                    yield return null;
                Assert.That(spawn.Generation, Is.Not.EqualTo(generation));
                using var units = world.EntityManager.CreateEntityQuery(typeof(PrototypeUnit));
                Assert.That(units.CalculateEntityCount(), Is.EqualTo(100));
                GameObject.Find("Arrival management").GetComponent<Toggle>().isOn = false;
                yield return null;
                yield return null;
                Assert.That(world.GetExistingSystemManaged<UnitArrivalSystem>().Managing, Is.False);
                hud.SetShown(false);
                Assert.That(PrototypeInputGate.PointerBlocked(point), Is.False);
                Assert.That(world.GetExistingSystemManaged<PrototypeStatisticsSystem>().Collect, Is.False);
                hud.SetShown(true);
                Assert.That(world.GetExistingSystemManaged<PrototypeStatisticsSystem>().Collect, Is.True);
            }
            finally
            {
                if (previous.IsValid() && previous.isLoaded)
                    SceneManager.SetActiveScene(previous);
                SceneManager.UnloadSceneAsync(scene);
            }
        }
#endif
    }
}
