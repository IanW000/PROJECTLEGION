using System;
using System.Diagnostics;
using NUnit.Framework;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Transforms;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsLocalAvoidanceTests
    {
        private const float StepSeconds = 0.05f;
        private const float Speed = 4f;

        [TestCase(false)]
        [TestCase(true)]
        public void NormalSpacingAndSingleUnitKeepTheirStraightPath(bool enable)
        {
            using var simulation = new Simulation(enable);
            Entity first = simulation.Create(new float2(0f, 0f), new float2(4f, 0f));
            Entity second = simulation.Create(new float2(0f, 0.9f), new float2(4f, 0.9f));
            Entity isolated = simulation.Create(new float2(0f, 10f), new float2(4f, 10f));
            for (int frame = 1; frame <= 24; frame++)
            {
                simulation.Step();
                float x = math.min(frame * StepSeconds * Speed, 4f);
                AssertPosition(simulation, first, new float2(x, 0f));
                AssertPosition(simulation, second, new float2(x, 0.9f));
                AssertPosition(simulation, isolated, new float2(x, 10f));
            }
            Assert.That(simulation.IsMoving(first) || simulation.IsMoving(second) || simulation.IsMoving(isolated),
                Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OpposingAndCrossingPathsReduceOverlap(bool crossing)
        {
            var baseline = RunEncounter(crossing, false);
            var avoidance = RunEncounter(crossing, true);
            UnityEngine.Debug.Log($"Avoidance {(crossing ? "crossing" : "head-on")}: overlap steps " +
                $"{baseline.OverlapSteps} disabled, {avoidance.OverlapSteps} enabled.");
            Assert.That(avoidance.OverlapSteps, Is.LessThan(baseline.OverlapSteps));
            Assert.That(avoidance.Arrived, Is.EqualTo(2));
            if (!crossing)
                Assert.That(avoidance.FirstDeviation * avoidance.SecondDeviation, Is.LessThan(0f));
        }

        [Test]
        public void StationaryUnitStaysPutAndMoverNavigatesPastIt()
        {
            using var simulation = new Simulation(true);
            Entity obstacle = simulation.Create(float2.zero, null);
            Entity mover = simulation.Create(new float2(-2f, 0f), new float2(2f, 0f));
            float lateral = 0f;
            for (int frame = 0; frame < 64; frame++)
            {
                simulation.Step();
                AssertPosition(simulation, obstacle, float2.zero);
                lateral = math.max(lateral, math.abs(simulation.Position(mover).y));
            }
            Assert.That(lateral, Is.GreaterThan(0.15f));
            AssertPosition(simulation, mover, new float2(2f, 0f));
            Assert.That(simulation.IsMoving(mover), Is.False);
        }

        [Test]
        public void CoincidentUnitsStayFiniteAndRespectSpeed()
        {
            using var simulation = new Simulation(true);
            Entity stationary = simulation.Create(float2.zero, null);
            Entity first = simulation.Create(float2.zero, new float2(4f, 0f));
            Entity second = simulation.Create(float2.zero, new float2(4f, 0.9f));
            float2 previousFirst = float2.zero;
            float2 previousSecond = float2.zero;
            for (int frame = 0; frame < 48; frame++)
            {
                simulation.Step();
                AssertStepSpeed(simulation, first, ref previousFirst);
                AssertStepSpeed(simulation, second, ref previousSecond);
                AssertPosition(simulation, stationary, float2.zero);
            }
            Assert.That(math.distance(previousFirst, previousSecond), Is.GreaterThan(0.05f));
            Assert.That(simulation.Target(first), Is.EqualTo(new float2(4f, 0f)));
        }

        [Test]
        public void InvalidInputHoldsAndReplacementFormationCommandUsesCurrentTargets()
        {
            using var simulation = new Simulation(true);
            Entity invalid = simulation.Create(new float2(30f, 30f), new float2(float.NaN, 30f));
            Entity first = simulation.Create(new float2(0f, 0f), null);
            Entity second = simulation.Create(new float2(0f, 0.9f), null);
            simulation.Select(first);
            simulation.Select(second);
            simulation.Commands.IssueMove(new float2(5f, 0f));
            for (int frame = 0; frame < 8; frame++)
                simulation.Step();
            simulation.Commands.IssueMove(new float2(0f, 4f));
            for (int frame = 0; frame < 90; frame++)
                simulation.Step();
            AssertPosition(simulation, invalid, new float2(30f, 30f));
            AssertPosition(simulation, first, simulation.Target(first));
            AssertPosition(simulation, second, simulation.Target(second));
            Assert.That(simulation.IsMoving(first) || simulation.IsMoving(second), Is.False);
            Assert.That((simulation.Target(first) + simulation.Target(second)) * 0.5f,
                Is.EqualTo(new float2(0f, 4f)));
        }

        [Test]
        public void NeighborLimitUsesDistanceThenEntityIdentity()
        {
            using var simulation = new Simulation(true);
            var center = new float2(2f);
            Entity self = simulation.Create(center, null);
            var positions = new[] { new float2(3f, 2f), new float2(2f, 3.5f), new float2(1f, 2f), new float2(2f, 2.5f) };
            var entities = new Entity[positions.Length];
            for (int i = 0; i < positions.Length; i++)
                entities[i] = simulation.Create(positions[i], null);

            using var map = new NativeParallelMultiHashMap<int2, SpatialGridEntry>(positions.Length, Allocator.Temp);
            var settings = LocalAvoidanceSettings.Default;
            settings.MaxNeighbors = 2;
            for (int pass = 0; pass < 2; pass++)
            {
                map.Clear();
                for (int i = 0; i < positions.Length; i++)
                {
                    int index = pass == 0 ? i : positions.Length - 1 - i;
                    map.Add(int2.zero, new SpatialGridEntry { Entity = entities[index], PositionXZ = positions[index] });
                }
                var grid = new SpatialGrid(map.AsReadOnly(), 4f);
                var candidates = LocalAvoidance.FindNearest(grid, self, center, settings, out long scanned);
                Assert.That(candidates.Length, Is.EqualTo(2));
                Assert.That(candidates[0].Entry.Entity, Is.EqualTo(entities[3]));
                Assert.That(candidates[1].Entry.Entity, Is.EqualTo(entities[0]));
                Assert.That(scanned, Is.EqualTo(positions.Length));
            }
        }

        [Test]
        public void OccupiedArrivalHoldsAndLiveDisableRestoresStraightMovement()
        {
            using var simulation = new Simulation(true);
            Entity obstacle = simulation.Create(new float2(0.15f, 0f), null);
            Entity mover = simulation.Create(float2.zero, new float2(0.15f, 0f));
            simulation.Step();
            AssertPosition(simulation, mover, float2.zero);
            Assert.That(simulation.IsMoving(mover), Is.True);

            simulation.Configure(false);
            simulation.Step();
            AssertPosition(simulation, mover, new float2(0.15f, 0f));
            AssertPosition(simulation, obstacle, new float2(0.15f, 0f));
            Assert.That(simulation.IsMoving(mover), Is.False);
        }

        [TestCase(0.72f, false)]
        [TestCase(0.69f, true)]
        [TestCase(2.5f, false)]
        public void LastStepArrivesOnlyWhenClearAndSettledUnitsStayStill(float obstacleX, bool occupied)
        {
            using var simulation = new Simulation(true, 1f / 60f);
            Entity mover = simulation.Create(new float2(-2f, -0.2f), float2.zero);
            simulation.Create(new float2(obstacleX, 0f), null);
            int steps = 0;
            while (steps++ < 180 && simulation.IsMoving(mover) && simulation.Plan(mover).Mode != AvoidanceMove.Hold)
                simulation.Step();

            Assert.That(simulation.IsMoving(mover), Is.EqualTo(occupied));
            Assert.That(simulation.Plan(mover).Mode, Is.EqualTo(occupied ? AvoidanceMove.Hold : AvoidanceMove.Direct));
            Assert.That(simulation.Plan(mover).Velocity, Is.EqualTo(float2.zero));
            if (!occupied)
                Assert.That(simulation.Position(mover), Is.EqualTo(float2.zero));

            var settled = simulation.Pose(mover);
            for (int i = 0; i < 60; i++)
            {
                simulation.Step();
                Assert.That(simulation.Pose(mover).Position, Is.EqualTo(settled.Position));
                Assert.That(simulation.Pose(mover).Rotation.value, Is.EqualTo(settled.Rotation.value));
            }

            if (!occupied)
            {
                var newTarget = new float2(-2f, 0f);
                simulation.Select(mover);
                simulation.Commands.IssueMove(newTarget);
                simulation.Step();
                Assert.That(simulation.IsMoving(mover), Is.True);
                Assert.That(simulation.Position(mover).x, Is.LessThan(settled.Position.x));
                for (int i = 0; i < 90 && simulation.IsMoving(mover); i++)
                    simulation.Step();
                Assert.That(simulation.IsMoving(mover), Is.False);
                Assert.That(simulation.Position(mover), Is.EqualTo(newTarget));
            }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void LastStepChecksStaticAndMovingConflictsEvenOutsideTopK(bool moving)
        {
            using var simulation = new Simulation(true, 0.5f, 1);
            float2 start = new float2(-1f, 0f);
            float2 target = new float2(1f, 0f);
            Entity mover = simulation.Create(start, target);
            simulation.Create(new float2(-1f, 0.9f), null);
            // Both blockers rank behind the distractor. Only the moving one's predicted step intersects.
            if (moving)
                simulation.Create(new float2(0f, -1f), new float2(0f, 1f));
            else
                simulation.Create(float2.zero, null);

            simulation.Step();
            Assert.That(simulation.IsMoving(mover), Is.True);
            Assert.That(simulation.Position(mover), Is.Not.EqualTo(target));
            Assert.That(simulation.Plan(mover).Mode, Is.EqualTo(AvoidanceMove.Hold));
        }

        [TestCase(1000)]
        [TestCase(10000)]
        public void CompareMultipleIndependentGroups(int count)
        {
            var baseline = MeasureGroups(count, false);
            var avoidance = MeasureGroups(count, true);
            Assert.That(avoidance.MinimumDistance, Is.GreaterThan(baseline.MinimumDistance));
            Assert.That(avoidance.Arrived, Is.GreaterThanOrEqualTo(count * 0.98f));
        }

        private static (float MinimumDistance, int Arrived) MeasureGroups(int count, bool enable)
        {
            const int frames = 96;
            const int warmup = 4;
            const int diagnosticStride = 4;
            using var simulation = new Simulation(enable);
            using var buildWorker = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "RTS.SpatialGrid.Build");
            using var calculate = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "RTS.Avoidance.Calculate");
            using var apply = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "RTS.Movement.Apply");
            var units = new Entity[count];
            var settings = LocalAvoidanceSettings.Default;
            for (int i = 0; i < count; i++)
            {
                int group = i / 100;
                int slot = i % 100;
                float side = slot < 50 ? -1f : 1f;
                int rank = (slot % 50) / 10;
                int lane = slot % 10;
                float2 origin = new float2((group % 10) * 24f, (group / 10) * 16f);
                float2 offset = new float2(side * (2.8f + rank * 0.9f), lane * 0.9f);
                units[i] = simulation.Create(origin + offset, origin + new float2(-offset.x, offset.y));
            }

            long buildTicks = 0;
            long movementTicks = 0;
            long diagnosticTicks = 0;
            long scanned = 0;
            long selected = 0;
            long maxScanned = 0;
            int probes = 0;
            int overlapObservations = 0;
            float minimumDistance = float.PositiveInfinity;
            long initialBuildWorker = 0;
            long initialCalculateWorker = 0;
            long initialApplyWorker = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                if (frame == warmup)
                {
                    initialBuildWorker = buildWorker.CurrentValue;
                    initialCalculateWorker = calculate.CurrentValue;
                    initialApplyWorker = apply.CurrentValue;
                }
                bool sampleStep = frame % diagnosticStride == 0;
                if (sampleStep)
                {
                    long sampleStart = Stopwatch.GetTimestamp();
                    simulation.CaptureSamplePositions();
                    diagnosticTicks += Stopwatch.GetTimestamp() - sampleStart;
                }
                simulation.Step(out long build, out long movement);
                if (frame >= warmup)
                {
                    buildTicks += build;
                    movementTicks += movement;
                }

                if (!sampleStep)
                    continue;
                long start = Stopwatch.GetTimestamp();
                SpatialGrid grid = simulation.Grid.GetSnapshot().Grid;
                // The grid describes the start of this step. Use a repeatable sample of entities
                // and their corresponding snapshot centers, independent of query processing order.
                var samplePositions = simulation.SampleBeforeMove;
                for (int sample = 0; sample < samplePositions.Length; sample++)
                {
                    Entity entity = units[sample * count / samplePositions.Length];
                    float2 center = samplePositions[sample];
                    var nearest = LocalAvoidance.FindNearest(grid, entity, center, settings, out long candidates);
                    scanned += candidates;
                    selected += nearest.Length;
                    maxScanned = Math.Max(maxScanned, candidates);
                    probes++;
                    if (nearest.Length == 0)
                        continue;
                    float distance = math.sqrt(nearest[0].DistanceSquared);
                    minimumDistance = math.min(minimumDistance, distance);
                    if (distance < settings.CollisionRadius * 2f)
                        overlapObservations++;
                }
                diagnosticTicks += Stopwatch.GetTimestamp() - start;
            }

            int arrived = 0;
            float remaining = 0f;
            foreach (Entity unit in units)
            {
                float2 position = simulation.Position(unit);
                Assert.That(math.all(math.isfinite(position)), Is.True);
                if (!simulation.IsMoving(unit))
                    arrived++;
                remaining += math.distance(position, simulation.Target(unit));
            }

            double millisecondsPerTick = 1000.0 / Stopwatch.Frequency;
            int measured = frames - warmup;
            UnityEngine.Debug.Log($"Avoidance {count} enabled={enable}: build {buildTicks * millisecondsPerTick / measured:F3} ms/step, " +
                $"movement {movementTicks * millisecondsPerTick / measured:F3} ms/step, " +
                $"job CPU ms/step build={(buildWorker.CurrentValue - initialBuildWorker) / (measured * 1e6):F3} " +
                $"calculate={(calculate.CurrentValue - initialCalculateWorker) / (measured * 1e6):F3} " +
                $"apply={(apply.CurrentValue - initialApplyWorker) / (measured * 1e6):F3}, " +
                $"diagnostics {diagnosticTicks * millisecondsPerTick:F2} ms total; " +
                $"candidates mean={scanned / (double)probes:F1} max={maxScanned}, selected mean={selected / (double)probes:F1}, " +
                $"overlap probes={overlapObservations}/{probes}, min center distance={minimumDistance:F3}, " +
                $"arrived={arrived}/{count}, mean remaining={remaining / count:F3}.");
            return (minimumDistance, arrived);
        }

        private static (int OverlapSteps, int Arrived, float FirstDeviation, float SecondDeviation)
            RunEncounter(bool crossing, bool enable)
        {
            using var simulation = new Simulation(enable);
            Entity first = simulation.Create(new float2(-2f, 0f), new float2(2f, 0f));
            Entity second = crossing
                ? simulation.Create(new float2(0f, -2f), new float2(0f, 2f))
                : simulation.Create(new float2(2f, 0f), new float2(-2f, 0f));
            int overlapSteps = 0;
            float firstDeviation = 0f;
            float secondDeviation = 0f;
            for (int frame = 0; frame < 64; frame++)
            {
                simulation.Step();
                float2 firstPosition = simulation.Position(first);
                float2 secondPosition = simulation.Position(second);
                if (math.distance(firstPosition, secondPosition) < 0.7f)
                    overlapSteps++;
                if (math.abs(firstPosition.y) > math.abs(firstDeviation))
                    firstDeviation = firstPosition.y;
                if (math.abs(secondPosition.y) > math.abs(secondDeviation))
                    secondDeviation = secondPosition.y;
            }
            int arrived = (simulation.IsMoving(first) ? 0 : 1) + (simulation.IsMoving(second) ? 0 : 1);
            return (overlapSteps, arrived, firstDeviation, secondDeviation);
        }

        private static void AssertPosition(Simulation simulation, Entity entity, float2 expected)
            => Assert.That(math.distance(simulation.Position(entity), expected), Is.LessThan(0.0001f));

        private static void AssertStepSpeed(Simulation simulation, Entity entity, ref float2 previous)
        {
            float2 position = simulation.Position(entity);
            Assert.That(math.all(math.isfinite(position)), Is.True);
            Assert.That(math.distance(previous, position), Is.LessThanOrEqualTo(Speed * StepSeconds + 0.0001f));
            previous = position;
        }

        private sealed class Simulation : IDisposable
        {
            private readonly World _world = new World("Local avoidance test");
            private readonly UnitMovementSystem _movement;
            private readonly float _stepSeconds;
            private readonly int _maxNeighbors;
            private readonly System.Collections.Generic.List<Entity> _units = new();
            private int _frame;

            internal UnitSpatialGridSystem Grid { get; }
            internal UnitMoveCommandSystem Commands { get; }
            internal float2[] SampleBeforeMove { get; } = new float2[64];
            private EntityManager Manager => _world.EntityManager;

            internal Simulation(bool enable, float stepSeconds = StepSeconds, int maxNeighbors = 8)
            {
                _stepSeconds = stepSeconds;
                _maxNeighbors = maxNeighbors;
                Grid = _world.GetOrCreateSystemManaged<UnitSpatialGridSystem>();
                Commands = _world.GetOrCreateSystemManaged<UnitMoveCommandSystem>();
                _movement = _world.GetOrCreateSystemManaged<UnitMovementSystem>();
                Configure(enable);
            }

            internal void Configure(bool enable)
            {
                var settings = LocalAvoidanceSettings.Default;
                settings.EnableLocalAvoidance = enable;
                settings.MaxNeighbors = _maxNeighbors;
                _movement.ConfigureAvoidance(settings);
            }

            internal Entity Create(float2 position, float2? target)
            {
                Entity entity = Manager.CreateEntity(typeof(UnitTag), typeof(UnitId), typeof(UnitSelected), typeof(UnitStats),
                    typeof(LocalTransform), typeof(UnitMoveTarget), typeof(UnitAvoidanceVelocity));
                Manager.SetComponentData(entity, new UnitId { Value = (ulong)_units.Count + 1 });
                Manager.SetComponentEnabled<UnitSelected>(entity, false);
                Manager.SetComponentData(entity, new UnitStats { MovementSpeed = Speed });
                Manager.SetComponentData(entity, LocalTransform.FromPosition(new float3(position.x, 0f, position.y)));
                Manager.SetComponentData(entity, new UnitMoveTarget { DestinationXZ = target.GetValueOrDefault() });
                Manager.SetComponentEnabled<UnitMoveTarget>(entity, target.HasValue);
                _units.Add(entity);
                return entity;
            }

            internal void Select(Entity entity) => Manager.SetComponentEnabled<UnitSelected>(entity, true);
            internal bool IsMoving(Entity entity) => Manager.IsComponentEnabled<UnitMoveTarget>(entity);
            internal LocalTransform Pose(Entity entity) => Manager.GetComponentData<LocalTransform>(entity);
            internal float2 Position(Entity entity) => Pose(entity).Position.xz;
            internal UnitAvoidanceVelocity Plan(Entity entity) => Manager.GetComponentData<UnitAvoidanceVelocity>(entity);
            internal float2 Target(Entity entity) => Manager.GetComponentData<UnitMoveTarget>(entity).DestinationXZ;
            internal void Step() => Step(out _, out _);

            internal void CaptureSamplePositions()
            {
                for (int i = 0; i < SampleBeforeMove.Length; i++)
                    SampleBeforeMove[i] = Position(_units[i * _units.Count / SampleBeforeMove.Length]);
            }

            internal void Step(out long build, out long movement)
            {
                _world.SetTime(new TimeData(++_frame * (double)_stepSeconds, _stepSeconds));
                Commands.Update();
                long start = Stopwatch.GetTimestamp();
                Grid.Update();
                Grid.GetSnapshot().Build.Complete();
                build = Stopwatch.GetTimestamp() - start;
                start = Stopwatch.GetTimestamp();
                _movement.Update();
                Manager.CompleteDependencyBeforeRO<LocalTransform>();
                movement = Stopwatch.GetTimestamp() - start;
            }

            public void Dispose() => _world.Dispose();
        }
    }
}
