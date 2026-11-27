using System;
using System.Diagnostics;
using NUnit.Framework;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Transforms;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsOverlapResolutionTests
    {
        private const float Frame = 1f / 60f;
        private const float Diameter = 0.7f;

        [TestCase(0.4f)]
        [TestCase(0f)]
        public void OverlapPairSeparatesSymmetricallyWithinTheSpeedLimit(float spacing)
        {
            using var simulation = new Simulation();
            Entity first = simulation.Create(float2.zero);
            Entity second = simulation.Create(new float2(spacing, 0f));
            float2 center = (simulation.Position(first) + simulation.Position(second)) * 0.5f;
            float previous = spacing;
            for (int i = 0; i < 24; i++)
            {
                float2 before = simulation.Position(first);
                simulation.Step();
                float2 after = simulation.Position(first);
                Assert.That(math.all(math.isfinite(after)), Is.True);
                Assert.That(math.distance(before, after), Is.LessThanOrEqualTo(0.050001f));
                float distance = math.distance(after, simulation.Position(second));
                Assert.That(distance + 0.000001f, Is.GreaterThanOrEqualTo(previous));
                previous = distance;
            }
            Assert.That(previous, Is.GreaterThan(0.699f));
            Assert.That(math.distance(center, (simulation.Position(first) + simulation.Position(second)) * 0.5f),
                Is.LessThan(0.000001f));
        }

        [Test]
        public void MobileUnitAloneMovesAwayFromFixedAndMultipleContacts()
        {
            using var simulation = new Simulation();
            Entity mover = simulation.Create(float2.zero);
            Entity left = simulation.Create(new float2(-0.3f, -0.3f), movable: false);
            Entity right = simulation.Create(new float2(0.3f, -0.3f), movable: false);
            Entity zeroSpeed = simulation.Create(new float2(0.3f, -0.3f), speed: 0f);
            float2 initialTarget = simulation.Target(mover);
            for (int i = 0; i < 24; i++)
                simulation.Step();
            Assert.That(simulation.Position(left), Is.EqualTo(new float2(-0.3f, -0.3f)));
            Assert.That(simulation.Position(right), Is.EqualTo(new float2(0.3f, -0.3f)));
            Assert.That(simulation.Position(zeroSpeed), Is.EqualTo(new float2(0.3f, -0.3f)));
            Assert.That(simulation.Position(mover).y, Is.GreaterThan(0.3f));
            Assert.That(simulation.Target(mover), Is.EqualTo(initialTarget));
        }

        [Test]
        public void EqualSimulatedTimeConvergesAtComparableRates()
        {
            float at30 = DepthAfter(30, 0.2f);
            float at60 = DepthAfter(60, 0.2f);
            float at120 = DepthAfter(120, 0.2f);
            Assert.That(at60, Is.LessThan(0.05f));
            Assert.That(at30, Is.EqualTo(at60).Within(0.015f));
            Assert.That(at120, Is.EqualTo(at60).Within(0.015f));
            UnityEngine.Debug.Log($"Overlap timestep comparison at 0.20 s: 30 Hz {at30:F6}, 60 Hz {at60:F6}, 120 Hz {at120:F6} remaining depth.");
        }

        [Test]
        public void PostMovementSnapshotDetectsNewOverlapAndToggleRestoresOriginalPath()
        {
            using var simulation = new Simulation(withMovement: true);
            Entity mover = simulation.Create(new float2(-0.1f, 0f), target: new float2(4f, 0f));
            Entity obstacle = simulation.Create(new float2(0.7f, 0f), movable: false);
            simulation.Step(0.05f);
            Assert.That(simulation.Position(mover).x, Is.EqualTo(0.0125f).Within(0.00001f));
            Assert.That(simulation.Position(obstacle), Is.EqualTo(new float2(0.7f, 0f)));
            simulation.Configure(false);
            simulation.Step(0.05f);
            Assert.That(simulation.Position(mover).x, Is.EqualTo(0.2125f).Within(0.00001f));
        }

        [Test]
        public void UnitThatArrivesThisStepIsFixedInTheCorrectionSnapshot()
        {
            using var simulation = new Simulation(withMovement: true);
            Entity arriving = simulation.Create(float2.zero, target: new float2(0.04f, 0f));
            Entity passing = simulation.Create(new float2(-0.4f, 0f), target: new float2(-4f, 0f));
            simulation.Step();
            Assert.That(simulation.Moving(arriving), Is.False);
            Assert.That(simulation.Position(arriving), Is.EqualTo(new float2(0.04f, 0f)));
            Assert.That(simulation.Position(passing).x, Is.EqualTo(-0.4f - 4f * Frame - 0.05f).Within(0.00001f));
        }

        [Test]
        public void ArrivedAndHoldUnitsKeepTheirPoseAndCanAcceptNewCommands()
        {
            using var simulation = new Simulation(withMovement: true, avoidance: true);
            Entity arrived = simulation.Create(new float2(-2f, -0.2f), target: float2.zero);
            simulation.Create(new float2(0.72f, 0f), movable: false);
            Entity held = simulation.Create(new float2(4f, 0f), target: new float2(4.05f, 0f));
            simulation.Create(new float2(4.65f, 0f), movable: false);
            for (int i = 0; i < 180 && simulation.Moving(arrived); i++)
                simulation.Step();
            Assert.That(simulation.Moving(arrived), Is.False);
            Assert.That(simulation.Moving(held), Is.True);
            Assert.That(simulation.Plan(held), Is.EqualTo(AvoidanceMove.Hold));
            var arrivedPose = simulation.Pose(arrived);
            var heldPose = simulation.Pose(held);
            Assert.That(arrivedPose.Position.xz, Is.EqualTo(float2.zero));
            Assert.That(heldPose.Position.x, Is.EqualTo(4f));
            for (int i = 0; i < 60; i++)
            {
                simulation.Step();
                Assert.That(simulation.Pose(arrived).Position, Is.EqualTo(arrivedPose.Position));
                Assert.That(simulation.Pose(arrived).Rotation.value, Is.EqualTo(arrivedPose.Rotation.value));
                Assert.That(simulation.Pose(held).Position, Is.EqualTo(heldPose.Position));
                Assert.That(simulation.Pose(held).Rotation.value, Is.EqualTo(heldPose.Rotation.value));
            }
            simulation.IssueMove(arrived, new float2(-2f, 0f));
            simulation.Step();
            Assert.That(simulation.Moving(arrived), Is.True);
            Assert.That(simulation.Position(arrived).x, Is.LessThan(arrivedPose.Position.x));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NonOverlappingFormationRetainsItsMovement(bool enable)
        {
            using var simulation = new Simulation(withMovement: true);
            simulation.Configure(enable);
            Entity first = simulation.Create(float2.zero, target: new float2(2f, 0f));
            Entity second = simulation.Create(new float2(0f, 0.9f), target: new float2(2f, 0.9f));
            for (int step = 1; step <= 36; step++)
            {
                simulation.Step();
                float x = math.min(step * Frame * 4f, 2f);
                Assert.That(math.distance(simulation.Position(first), new float2(x, 0f)), Is.LessThan(0.00001f));
                Assert.That(math.distance(simulation.Position(second), new float2(x, 0.9f)), Is.LessThan(0.00001f));
            }
        }

        [Test]
        public void OpposingMovementAndCorrectionDoNotOscillateAtEquilibrium()
        {
            using var simulation = new Simulation(withMovement: true);
            Entity mover = simulation.Create(float2.zero, target: new float2(2f, 0f), speed: 1.5f);
            Entity anchor = simulation.Create(new float2(0.675f, 0f), movable: false);
            for (int i = 0; i < 12; i++)
                simulation.Step();
            var settled = simulation.Pose(mover);
            Assert.That(math.abs(settled.Position.x), Is.LessThan(0.000001f));
            for (int i = 0; i < 60; i++)
            {
                simulation.Step();
                Assert.That(math.distance(simulation.Pose(mover).Position, settled.Position), Is.LessThan(0.000001f));
                Assert.That(math.abs(math.dot(simulation.Pose(mover).Rotation.value, settled.Rotation.value)), Is.GreaterThan(0.999999f));
                Assert.That(simulation.Position(anchor), Is.EqualTo(new float2(0.675f, 0f)));
            }
            Assert.That(simulation.Moving(mover), Is.True);
        }

        [Test]
        public void DenseCoincidentClusterStaysFiniteAndDisplacementsStayBounded()
        {
            using var simulation = new Simulation();
            var units = new Entity[128];
            for (int i = 0; i < units.Length; i++)
                units[i] = simulation.Create(float2.zero);
            for (int step = 0; step < 12; step++)
            {
                var before = Array.ConvertAll(units, simulation.Position);
                simulation.Step();
                for (int i = 0; i < units.Length; i++)
                {
                    float2 after = simulation.Position(units[i]);
                    Assert.That(math.all(math.isfinite(after)), Is.True);
                    Assert.That(math.distance(before[i], after), Is.LessThanOrEqualTo(0.050001f));
                }
            }
        }

        [TestCase(1000)]
        [TestCase(10000)]
        public void CompareMixedClustersWithResolutionEnabledAndDisabled(int count)
        {
            using (var warmup = new Simulation())
            {
                warmup.Create(float2.zero);
                warmup.Create(float2.zero);
                warmup.Step();
            }
            Measure(count, false);
            Measure(count, true);
        }

        private static void Measure(int count, bool enabled)
        {
            const int unitsPerCluster = 10;
            const int steps = 24;
            const float depthThreshold = 0.0001f;
            using var simulation = new Simulation();
            simulation.Configure(enabled);
            using var build = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "RTS.Overlap.Build");
            using var calculate = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "RTS.Overlap.Calculate");
            using var apply = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "RTS.Overlap.Apply");
            var positions = new[]
            {
                new float2(0f, 0f), new float2(0.4f, 0f),
                new float2(0f, 3f), new float2(0.4f, 3f),
                new float2(0f, 6f), new float2(0.4f, 6f),
                new float2(0f, 9f), new float2(0.28f, 9f),
                new float2(0f, 9.28f), new float2(0.28f, 9.28f)
            };
            var mobile = new[] { true, true, true, false, false, false, true, true, true, true };
            var units = new Entity[count];
            var sample = new float2[unitsPerCluster];
            for (int i = 0; i < count; i++)
            {
                int cluster = i / unitsPerCluster;
                int slot = i % unitsPerCluster;
                var origin = new float2((cluster % 32) * 16f, (cluster / 32) * 16f);
                units[i] = simulation.Create(origin + positions[slot], mobile[slot]);
            }

            long first = 0, steady = 0, diagnostics = 0;
            long firstBuild = 0, firstCalculate = 0, firstApply = 0;
            int firstConverged = -1;
            PairStats[] initial = null;
            PairStats[] final = null;
            for (int step = 0; step <= steps; step++)
            {
                if (step > 0)
                {
                    long start = Stopwatch.GetTimestamp();
                    simulation.Step();
                    long elapsed = Stopwatch.GetTimestamp() - start;
                    if (step == 1)
                    {
                        first = elapsed;
                        firstBuild = build.CurrentValue;
                        firstCalculate = calculate.CurrentValue;
                        firstApply = apply.CurrentValue;
                    }
                    else
                    {
                        steady += elapsed;
                    }
                }

                long diagnosticStart = Stopwatch.GetTimestamp();
                // Cluster spacing exceeds the interaction radius even at maximum correction speed for this run.
                var pairs = new PairStats[3];
                for (int cluster = 0; cluster < units.Length; cluster += unitsPerCluster)
                {
                    for (int i = 0; i < unitsPerCluster; i++)
                        sample[i] = simulation.Position(units[cluster + i]);
                    for (int i = 0; i < unitsPerCluster; i++)
                    for (int j = i + 1; j < unitsPerCluster; j++)
                    {
                        float depth = Diameter - math.distance(sample[i], sample[j]);
                        if (depth > depthThreshold)
                            pairs[(mobile[i] ? 0 : 1) + (mobile[j] ? 0 : 1)].Add(depth);
                    }
                }
                diagnostics += Stopwatch.GetTimestamp() - diagnosticStart;
                initial ??= pairs;
                final = pairs;
                if (firstConverged < 0 && math.max(pairs[0].MaxDepth, pairs[1].MaxDepth) < 0.01f)
                    firstConverged = step;
            }

            double ms = 1000.0 / Stopwatch.Frequency;
            int steadySteps = steps - 1;
            UnityEngine.Debug.Log($"Overlap {count} enabled={enabled}: first {first * ms:F3} ms, steady {steady * ms / steadySteps:F3} ms/step, " +
                $"job marker ms/step build={(build.CurrentValue - firstBuild) / (steadySteps * 1e6):F3} " +
                $"calculate={(calculate.CurrentValue - firstCalculate) / (steadySteps * 1e6):F3} " +
                $"apply={(apply.CurrentValue - firstApply) / (steadySteps * 1e6):F3}, diagnostics {diagnostics * ms:F3} ms total; " +
                $"first step below 0.01={firstConverged}; mobile/mobile {initial[0]} -> {final[0]}; " +
                $"mobile/fixed {initial[1]} -> {final[1]}; fixed/fixed {initial[2]} -> {final[2]}.");
            Assert.That(final[2].Count, Is.EqualTo(initial[2].Count));
            Assert.That(final[2].MaxDepth, Is.EqualTo(initial[2].MaxDepth));
            if (enabled)
            {
                Assert.That(firstConverged, Is.InRange(1, steps));
                Assert.That(final[0].Count + final[1].Count, Is.LessThan(initial[0].Count + initial[1].Count));
            }
            else
            {
                Assert.That(firstConverged, Is.EqualTo(-1));
                Assert.That(build.CurrentValue + calculate.CurrentValue + apply.CurrentValue, Is.Zero);
                Assert.That(final[0].Count + final[1].Count, Is.EqualTo(initial[0].Count + initial[1].Count));
            }
        }

        private static float DepthAfter(int hertz, float seconds)
        {
            using var simulation = new Simulation();
            Entity unit = simulation.Create(float2.zero);
            Entity anchor = simulation.Create(new float2(0.2f, 0f), movable: false);
            for (int i = 0; i < math.round(hertz * seconds); i++)
                simulation.Step(1f / hertz);
            return Diameter - math.distance(simulation.Position(unit), simulation.Position(anchor));
        }

        private struct PairStats
        {
            internal int Count;
            internal float MaxDepth;
            private double _sum;

            internal void Add(float depth)
            {
                Count++;
                MaxDepth = math.max(MaxDepth, depth);
                _sum += depth;
            }

            public override string ToString() => $"{Count} pairs, mean {(Count == 0 ? 0.0 : _sum / Count):F6}, max {MaxDepth:F6}";
        }

        internal sealed class Simulation : IDisposable
        {
            private readonly World _world = new World("Overlap resolution test");
            private readonly UnitOverlapResolutionSystem _correction;
            private readonly SimulationSystemGroup _group;
            private readonly UnitMoveCommandSystem _commands;
            private EntityManager Manager => _world.EntityManager;
            private ulong _unitId;
            private double _time;

            internal Simulation(bool withMovement = false, bool avoidance = false)
            {
                _correction = _world.GetOrCreateSystemManaged<UnitOverlapResolutionSystem>();
                if (!withMovement)
                    return;
                var grid = _world.GetOrCreateSystemManaged<UnitSpatialGridSystem>();
                var movement = _world.GetOrCreateSystemManaged<UnitMovementSystem>();
                _commands = _world.GetOrCreateSystemManaged<UnitMoveCommandSystem>();
                var settings = LocalAvoidanceSettings.Default;
                settings.EnableLocalAvoidance = avoidance;
                movement.ConfigureAvoidance(settings);
                _group = _world.GetOrCreateSystemManaged<SimulationSystemGroup>();
                _group.AddSystemToUpdateList(_correction);
                _group.AddSystemToUpdateList(_world.GetOrCreateSystemManaged<TransformSystemGroup>());
                _group.AddSystemToUpdateList(movement);
                _group.AddSystemToUpdateList(_commands);
                _group.AddSystemToUpdateList(grid);
                _group.AddSystemToUpdateList(_world.GetOrCreateSystemManaged<UnitSpawnSystem>());
                _group.SortSystems();
            }

            internal Entity Create(float2 position, bool movable = true, float2? target = null, float speed = 4f)
            {
                Entity unit = Manager.CreateEntity(typeof(UnitTag), typeof(UnitId), typeof(UnitSelected),
                    typeof(UnitStats), typeof(UnitMoveTarget), typeof(UnitAvoidanceVelocity),
                    typeof(UnitOverlapCorrection), typeof(LocalTransform));
                Manager.SetComponentData(unit, new UnitId { Value = ++_unitId });
                Manager.SetComponentEnabled<UnitSelected>(unit, false);
                Manager.SetComponentData(unit, new UnitStats { MovementSpeed = speed });
                Manager.SetComponentData(unit, new UnitMoveTarget { DestinationXZ = target ?? position + new float2(8f, 0f) });
                Manager.SetComponentEnabled<UnitMoveTarget>(unit, movable);
                Manager.SetComponentData(unit, LocalTransform.FromPosition(new float3(position.x, 0f, position.y)));
                return unit;
            }

            internal void Configure(bool enabled)
            {
                var settings = OverlapResolutionSettings.Default;
                settings.EnableOverlapResolution = enabled;
                _correction.Configure(settings);
            }

            internal LocalTransform Pose(Entity entity) => Manager.GetComponentData<LocalTransform>(entity);
            internal float2 Position(Entity entity) => Pose(entity).Position.xz;
            internal float2 Target(Entity entity) => Manager.GetComponentData<UnitMoveTarget>(entity).DestinationXZ;
            internal bool Moving(Entity entity) => Manager.IsComponentEnabled<UnitMoveTarget>(entity);
            internal AvoidanceMove Plan(Entity entity) => Manager.GetComponentData<UnitAvoidanceVelocity>(entity).Mode;

            internal void IssueMove(Entity unit, float2 target)
            {
                Manager.SetComponentEnabled<UnitSelected>(unit, true);
                _commands.IssueMove(target);
            }

            internal void Step(float deltaTime = Frame)
            {
                _world.SetTime(new TimeData(_time += deltaTime, deltaTime));
                if (_group != null)
                    _group.Update();
                else
                    _correction.Update();
                Manager.CompleteDependencyBeforeRO<LocalTransform>();
            }

            public void Dispose() => _world.Dispose();
        }
    }
}
