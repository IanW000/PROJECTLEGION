using NUnit.Framework;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsMovementTests
    {
        [Test]
        public void CommandsReplaceTargetsAndMovementSurvivesDeselection()
        {
            using var world = new World("RTS movement test");
            var manager = world.EntityManager;
            var commands = world.GetOrCreateSystemManaged<UnitMoveCommandSystem>();
            var movement = world.GetOrCreateSystemManaged<UnitMovementSystem>();
            Entity selected = CreateUnit(manager, true);
            Entity unselected = CreateUnit(manager, false);

            commands.IssueMove(new float2(2f, 0f));
            Step(world, commands, movement, 0.05f);
            LocalTransform first = manager.GetComponentData<LocalTransform>(selected);
            Assert.That(first.Position.x, Is.EqualTo(0.2f).Within(0.0001f));
            Assert.That(first.Position.y, Is.EqualTo(3f));
            Assert.That(math.forward(first.Rotation).x, Is.InRange(0.1f, 0.9f));
            Assert.That(manager.IsComponentEnabled<UnitMoveTarget>(unselected), Is.False);

            commands.IssueMove(new float2(0.2f, -1f));
            Step(world, commands, movement, 0.05f);
            Assert.That(manager.GetComponentData<UnitMoveTarget>(selected).DestinationXZ,
                Is.EqualTo(new float2(0.2f, -1f)));
            Assert.That(manager.GetComponentData<LocalTransform>(selected).Position.z, Is.EqualTo(-0.2f).Within(0.0001f));

            manager.SetComponentEnabled<UnitSelected>(selected, false);
            commands.IssueMove(new float2(100f, 100f));
            Step(world, commands, movement, 1f);
            Assert.That(manager.GetComponentData<LocalTransform>(selected).Position, Is.EqualTo(new float3(0.2f, 3f, -1f)));
            Assert.That(manager.IsComponentEnabled<UnitMoveTarget>(selected), Is.False);
            Assert.That(manager.GetComponentData<LocalTransform>(unselected).Position, Is.EqualTo(new float3(0f, 3f, 0f)));

            Step(world, commands, movement, 1f);
            Assert.That(manager.GetComponentData<LocalTransform>(selected).Position, Is.EqualTo(new float3(0.2f, 3f, -1f)));
        }

        [TestCase(1000)]
        [TestCase(10000)]
        public void BatchMovesMixedSelectionThenAllUnitsAcrossChunks(int count)
        {
            using var world = new World("RTS multi-chunk movement test");
            var manager = world.EntityManager;
            var commands = world.GetOrCreateSystemManaged<UnitMoveCommandSystem>();
            var movement = world.GetOrCreateSystemManaged<UnitMovementSystem>();
            Entity template = CreateUnit(manager, true);
            using var units = new NativeArray<Entity>(count, Allocator.Temp);
            manager.Instantiate(template, units);
            manager.DestroyEntity(template);
            for (int i = 0; i < units.Length; i++)
            {
                manager.SetComponentEnabled<UnitSelected>(units[i], i % 2 == 0);
                manager.SetComponentData(units[i], new UnitId { Value = (ulong)i + 1 });
            }

            commands.IssueMove(new float2(0f, 5f));
            Step(world, commands, movement, 0f);
            for (int i = 0; i < units.Length; i++)
                Assert.That(manager.IsComponentEnabled<UnitMoveTarget>(units[i]), Is.EqualTo(i % 2 == 0));

            Step(world, commands, movement, 0.25f);
            for (int i = 0; i < units.Length; i++)
            {
                float distance = math.length(manager.GetComponentData<LocalTransform>(units[i]).Position.xz);
                Assert.That(distance, Is.LessThanOrEqualTo(1.0001f));
                if (i % 2 != 0)
                    Assert.That(distance, Is.Zero);
            }

            Step(world, commands, movement, 30f);
            for (int i = 0; i < units.Length; i++)
            {
                Assert.That(manager.IsComponentEnabled<UnitMoveTarget>(units[i]), Is.False);
                manager.SetComponentEnabled<UnitSelected>(units[i], true);
            }

            var center = new float2(4f, 5f);
            commands.IssueMove(center, 1.5f);
            Step(world, commands, movement, 0f);
            var slots = new HashSet<float2>();
            double2 sum = default;
            foreach (Entity unit in units)
            {
                Assert.That(manager.IsComponentEnabled<UnitMoveTarget>(unit), Is.True);
                float2 slot = manager.GetComponentData<UnitMoveTarget>(unit).DestinationXZ;
                Assert.That(slots.Add(slot), Is.True);
                sum += (double2)slot;
            }
            Assert.That(math.distance((float2)(sum / count), center), Is.LessThan(0.0001f));

            Step(world, commands, movement, 60f);
            foreach (Entity unit in units)
            {
                Assert.That(manager.IsComponentEnabled<UnitMoveTarget>(unit), Is.False);
                Assert.That(manager.GetComponentData<LocalTransform>(unit).Position.xz,
                    Is.EqualTo(manager.GetComponentData<UnitMoveTarget>(unit).DestinationXZ));
            }
        }

        [Test]
        public void GroundTargetRequiresHitInsidePlaneAndCameraRange()
        {
            var ground = new GameObject("Ground targeting test");
            var mesh = new Mesh
            {
                bounds = new Bounds(Vector3.zero, new Vector3(4f, 0f, 8f))
            };
            try
            {
                var filter = ground.AddComponent<MeshFilter>();
                filter.sharedMesh = mesh;
                ground.transform.SetPositionAndRotation(new Vector3(5f, 2f, 8f), Quaternion.Euler(0f, 90f, 0f));
                ground.transform.localScale = new Vector3(2f, 1f, 1f);
                var ray = new Ray(new Vector3(7f, 12f, 9f), Vector3.down);
                Assert.That(RtsMoveCommandInput.TryGetGroundPoint(filter, ray, 20f, out var point), Is.True);
                Assert.That(point, Is.EqualTo(new Vector3(7f, 2f, 9f)));
                Assert.That(RtsMoveCommandInput.TryGetGroundPoint(filter, ray, 9f, out _), Is.False);
                var offPlane = new Ray(new Vector3(15f, 12f, 9f), Vector3.down);
                Assert.That(RtsMoveCommandInput.TryGetGroundPoint(filter, offPlane, 20f, out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(ground);
                Object.DestroyImmediate(mesh);
            }
        }

        private static Entity CreateUnit(EntityManager manager, bool selected)
        {
            Entity unit = manager.CreateEntity(typeof(UnitTag), typeof(UnitSelected), typeof(UnitId),
                typeof(UnitMoveTarget), typeof(UnitStats), typeof(LocalTransform));
            manager.SetComponentData(unit, new UnitStats { MovementSpeed = 4f });
            manager.SetComponentData(unit, LocalTransform.FromPosition(new float3(0f, 3f, 0f)));
            manager.SetComponentEnabled<UnitSelected>(unit, selected);
            manager.SetComponentEnabled<UnitMoveTarget>(unit, false);
            return unit;
        }

        private static void Step(World world, UnitMoveCommandSystem commands, UnitMovementSystem movement, float deltaTime)
        {
            world.SetTime(new TimeData(world.Time.ElapsedTime + deltaTime, deltaTime));
            commands.Update();
            movement.Update();
            world.EntityManager.CompleteAllTrackedJobs();
        }
    }
}
