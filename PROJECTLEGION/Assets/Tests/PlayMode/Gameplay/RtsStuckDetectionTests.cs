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
    public sealed class RtsStuckDetectionTests
    {
        private const float Frame = 1f / 60f;

        [TestCase(4f)]
        [TestCase(0.08f)]
        public void UnobstructedMovementDoesNotEnterWaiting(float speed)
        {
            using var sim = new Simulation();
            Entity unit = sim.Create(float2.zero, new float2(speed, 0f), speed);
            for (int i = 0; i < 90; i++)
            {
                sim.Step();
                Assert.That(sim.State(unit).State, Is.EqualTo(UnitMovementState.Moving));
            }
            Assert.That(sim.IsMoving(unit), Is.False);
            Assert.That(sim.Position(unit), Is.EqualTo(new float2(speed, 0f)));
        }

        [Test]
        public void TemporaryBlockAndPassableDetourBothReachTheTarget()
        {
            using var sim = new Simulation();
            Entity mover = sim.Create(new float2(-2f, 0f), new float2(2f, 0f), 4f);
            sim.Create(float2.zero, null);
            float lateral = 0f;
            for (int i = 0; i < 120; i++)
            {
                sim.Step();
                lateral = math.max(lateral, math.abs(sim.Position(mover).y));
                Assert.That(sim.State(mover).State, Is.EqualTo(UnitMovementState.Moving));
            }
            Assert.That(lateral, Is.GreaterThan(0.15f));
            Assert.That(sim.IsMoving(mover), Is.False);
        }

        [Test]
        public void ShortBlockedArrivalClearsBeforeTheSecondObservationWindow()
        {
            using var sim = new Simulation();
            var destination = new float2(0.04f, 0f);
            var mover = sim.Create(float2.zero, destination, 4f);
            var blocker = sim.Create(destination, null);
            for (int i = 0; i < 30; i++)
                sim.Step();
            Assert.That(sim.IsMoving(mover), Is.True);
            Assert.That(sim.State(mover).State, Is.EqualTo(UnitMovementState.Moving));
            sim.Destroy(blocker);
            sim.Step();
            Assert.That(sim.IsMoving(mover), Is.False);
            Assert.That(sim.Position(mover), Is.EqualTo(destination));
        }

        [Test]
        public void SustainedLateralAndAwayProgressCanCompleteADetour()
        {
            using var sim = new Simulation(simulateMotion: false);
            var mover = sim.Create(new float2(-0.75f, 0f), new float2(2f, 0f));
            sim.Create(float2.zero, null);
            for (int i = 0; i < 216; i++)
            {
                float step = i / 60f;
                float2 position = step <= 1.2f
                    ? new float2(-0.75f - step * 0.16f, step * 0.8f)
                    : math.lerp(new float2(-0.942f, 0.96f), new float2(2f, 0f), (step - 1.2f) / 2.4f);
                sim.SetPosition(mover, position);
                sim.Step();
                Assert.That(sim.State(mover).State, Is.EqualTo(UnitMovementState.Moving));
            }
            sim.SetPosition(mover, new float2(1.99f, 0f));
            sim.EnableMotion(true);
            sim.Step();
            Assert.That(sim.IsMoving(mover), Is.False);
            Assert.That(sim.Position(mover), Is.EqualTo(new float2(2f, 0f)));
        }

        [Test]
        public void OscillationCountsAsBlockedAndAnOpenSideCanFailAndBackOff()
        {
            using var sim = new Simulation(simulateMotion: false);
            var mover = sim.Create(float2.zero, new float2(2f, 0f));
            sim.Create(new float2(0.55f, 0.47f), null);
            for (int i = 0; i < 120 && sim.State(mover).State != UnitMovementState.Waiting; i++)
            {
                sim.SetPosition(mover, new float2(0f, (i & 1) == 0 ? 0.015f : -0.015f),
                    quaternion.RotateY((i & 1) == 0 ? 0.1f : -0.1f));
                sim.Step();
            }
            Assert.That(sim.State(mover).State, Is.EqualTo(UnitMovementState.Waiting));

            float previousDelay = sim.State(mover).RetryDelay;
            for (int trial = 0; trial < 2; trial++)
            {
                WaitFor(sim, mover, UnitMovementState.Retrying, 100);
                // Zero net progress represents the actual mover failing to follow the clear side probe.
                WaitFor(sim, mover, UnitMovementState.Waiting, 100);
                var state = sim.State(mover);
                Assert.That(state.RetryDelay, Is.GreaterThan(previousDelay));
                Assert.That(state.NextProbe - sim.Time, Is.GreaterThanOrEqualTo(state.RetryDelay - 0.000001));
                var pose = sim.Pose(mover);
                for (int i = 0; i < 10; i++)
                {
                    sim.Step();
                    Assert.That(sim.State(mover).State, Is.EqualTo(UnitMovementState.Waiting));
                    Assert.That(sim.Pose(mover).Rotation.value, Is.EqualTo(pose.Rotation.value));
                }
                previousDelay = state.RetryDelay;
            }
        }

        [Test]
        public void WaitingProxyHasZeroVelocityEvenWithAnEnabledTargetAndStaleSteering()
        {
            float2 WithObstacle(bool waiting)
            {
                using var sim = new Simulation();
                var mover = sim.Create(new float2(-0.9f, 0f), new float2(3f, 0f), 4f);
                var blocker = sim.Create(float2.zero, waiting ? new float2(0f, 4f) : (float2?)null, 4f);
                if (waiting)
                    sim.SeedWaiting(blocker, 20);
                sim.SetPlan(blocker, new UnitAvoidanceVelocity { Mode = AvoidanceMove.Steer, Velocity = new float2(0f, 4f) });
                sim.Step();
                Assert.That(sim.Position(blocker), Is.EqualTo(float2.zero));
                if (waiting)
                    Assert.That(sim.Plan(blocker).Velocity, Is.EqualTo(float2.zero));
                return sim.Position(mover);
            }
            Assert.That(WithObstacle(true), Is.EqualTo(WithObstacle(false)));
        }

        [Test]
        public void WaitingFixedPairPreservesAnExistingOverlap()
        {
            using var sim = new Simulation();
            var first = sim.Create(float2.zero, new float2(3f, 0f));
            var second = sim.Create(new float2(0.4f, 0f), new float2(3f, 1f));
            sim.SeedWaiting(first, 20);
            sim.SeedWaiting(second, 20);
            for (int i = 0; i < 60; i++)
                sim.Step();
            Assert.That(sim.Position(first), Is.EqualTo(float2.zero));
            Assert.That(sim.Position(second), Is.EqualTo(new float2(0.4f, 0f)));
            Assert.That(sim.State(first).State, Is.EqualTo(UnitMovementState.Waiting));
            Assert.That(sim.State(second).State, Is.EqualTo(UnitMovementState.Waiting));
        }

        [Test]
        public void RealOverlapCorrectionCanCancelMovementUntilDetectionStopsTheAttempts()
        {
            using var sim = new Simulation(avoidance: false);
            var mover = sim.Create(float2.zero, new float2(2f, 0f), 1.5f);
            sim.Create(new float2(0.675f, 0f), null);
            int canceled = 0;
            for (int i = 0; i < 120 && sim.State(mover).State != UnitMovementState.Waiting; i++)
            {
                sim.Step();
                if (sim.Correction(mover).x < -0.02f && math.abs(sim.Position(mover).x) < 0.000001f)
                    canceled++;
            }
            Assert.That(canceled, Is.GreaterThan(20));
            Assert.That(sim.State(mover).State, Is.EqualTo(UnitMovementState.Waiting));
            sim.Step();
            Assert.That(sim.Correction(mover), Is.EqualTo(float2.zero));
            Assert.That(sim.IsMoving(mover), Is.True);
        }

        [Test]
        public void BlockedColumnsWaitThenRecoverInMoreThanOneStep()
        {
            using var sim = new Simulation();
            var leaders = new Entity[3];
            var followers = new Entity[3];
            var walls = new Entity[18];
            for (int group = 0; group < 3; group++)
            {
                float y = group * 8f;
                for (int i = 0; i < 6; i++)
                    walls[group * 6 + i] = sim.Create(new float2((i / 2) * 0.65f, y + ((i & 1) == 0 ? -0.4f : 0.4f)), null);
                for (int i = 0; i < 7; i++)
                {
                    float x = -4.55f + i * 0.65f;
                    sim.Create(new float2(x, y - 1.10f), null);
                    sim.Create(new float2(x, y + 1.10f), null);
                }
                leaders[group] = sim.Create(new float2(-2f, y + 0.11f), new float2(3f, y));
                followers[group] = sim.Create(new float2(-2.8f, y - 0.11f), new float2(3.9f, y));
            }
            var leaderWaited = new bool[3];
            var followerWaited = new bool[3];
            for (int i = 0; i < 420; i++)
            {
                sim.Step();
                for (int group = 0; group < 3; group++)
                {
                    leaderWaited[group] |= sim.State(leaders[group]).State == UnitMovementState.Waiting;
                    followerWaited[group] |= sim.State(followers[group]).State == UnitMovementState.Waiting;
                }
            }
            for (int group = 0; group < 3; group++)
            {
                Assert.That(leaderWaited[group], Is.True, $"Leader {group}, position {sim.Position(leaders[group])}");
                Assert.That(followerWaited[group], Is.True, $"Follower {group}, position {sim.Position(followers[group])}");
            }

            var cached = sim.Pose(leaders[0]);
            for (int i = 0; i < 60; i++)
            {
                sim.Step();
                Assert.That(sim.Pose(leaders[0]).Position, Is.EqualTo(cached.Position));
                Assert.That(sim.Pose(leaders[0]).Rotation.value, Is.EqualTo(cached.Rotation.value));
            }
            foreach (Entity wall in walls)
                sim.Destroy(wall);

            int recoverySteps = 0;
            int previousWaiting = sim.CountStates().Waiting;
            Assert.That(previousWaiting, Is.GreaterThanOrEqualTo(4));
            for (int i = 0; i < 480; i++)
            {
                sim.Step();
                int waiting = 0;
                foreach (Entity unit in leaders)
                    waiting += sim.State(unit).State == UnitMovementState.Waiting ? 1 : 0;
                foreach (Entity unit in followers)
                    waiting += sim.State(unit).State == UnitMovementState.Waiting ? 1 : 0;
                if (waiting < previousWaiting)
                    recoverySteps++;
                previousWaiting = waiting;
            }
            Assert.That(recoverySteps, Is.GreaterThan(1));
            foreach (Entity unit in leaders)
                Assert.That(sim.IsMoving(unit), Is.False);
            foreach (Entity unit in followers)
                Assert.That(sim.IsMoving(unit), Is.False);
        }

        [Test]
        public void SameCommandAndRuntimeToggleDiscardWaitingAndOldPlan()
        {
            using var sim = new Simulation(overlap: false);
            var target = new float2(3f, 0f);
            var mover = sim.Create(float2.zero, target, 4f);
            sim.SeedWaiting(mover, 20);
            uint version = sim.State(mover).CommandVersion;
            sim.Command(mover, target);
            sim.Step();
            Assert.That(sim.State(mover).CommandVersion, Is.EqualTo(version + 1));
            Assert.That(sim.State(mover).State, Is.EqualTo(UnitMovementState.Moving));
            Assert.That(sim.Position(mover).x, Is.GreaterThan(0f));

            sim.SeedWaiting(mover, 20);
            sim.SetPlan(mover, new UnitAvoidanceVelocity { Mode = AvoidanceMove.Steer, Velocity = new float2(0f, 4f) });
            var before = sim.Position(mover);
            sim.Configure(false);
            sim.Step();
            Assert.That(sim.Position(mover).x, Is.GreaterThan(before.x));
            Assert.That(sim.Position(mover).y, Is.EqualTo(before.y));
            Assert.That(sim.State(mover).NextProbe, Is.Zero);
            Assert.That(sim.Plan(mover).Velocity, Is.EqualTo(float2.zero));
            sim.Configure(true);
            sim.Step();
            var reset = sim.State(mover);
            Assert.That(reset.State, Is.EqualTo(UnitMovementState.Moving));
            Assert.That(reset.FailedWindows, Is.Zero);
            Assert.That(reset.WindowSeconds, Is.Zero);
            Assert.That(reset.RetryDelay, Is.Zero);
        }

        [Test]
        public void DetectionAndRecoveryUseSimulationTime()
        {
            foreach (int hz in new[] { 30, 60, 120 })
            {
                using var sim = new Simulation();
                float dt = 1f / hz;
                var target = new float2(0.01f, 0f);
                var mover = sim.Create(float2.zero, target, 4f);
                var blocker = sim.Create(target, null);
                WaitFor(sim, mover, UnitMovementState.Waiting, hz * 2, dt);
                double detected = sim.Time;
                double probeDue = sim.State(mover).NextProbe;
                sim.SetPosition(blocker, new float2(0.01f, 3f));
                while (sim.IsMoving(mover) && sim.Time - detected < 1)
                    sim.Step(dt);
                double recovered = sim.Time - detected;
                Assert.That(sim.IsMoving(mover), Is.False);
                Assert.That(detected, Is.InRange(0.79, 0.92));
                Assert.That(recovered, Is.InRange(0.24, 0.49));
                Assert.That(sim.Time - probeDue, Is.InRange(0, dt + 0.00001));
                UnityEngine.Debug.Log($"Stuck timestep {hz} Hz: detected={detected:F4}s recovered={recovered:F4}s");
            }
        }

        [TestCase(1000)]
        [TestCase(10000)]
        public void ReportMassWakeUpCost(int count)
        {
            Measure(count, false);
            Measure(count, true);
        }

        private static void Measure(int count, bool monitoring)
        {
            const int steps = 80;
            using var sim = new Simulation();
            sim.Configure(monitoring);
            using var probe = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "RTS.Stuck.Probe");
            using var observe = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "RTS.Stuck.Observe");
            using var obstacles = new NativeArray<Entity>(count / 2, Allocator.Temp);
            var obstacleStorage = obstacles;
            for (int i = 0; i < obstacles.Length; i++)
            {
                var origin = new float2((i % 100) * 5f, (i / 100) * 5f);
                var mover = sim.Create(origin, origin + new float2(1.5f, 0f), 4f);
                obstacleStorage[i] = sim.Create(origin + new float2(0.4f, 0f), null);
                sim.SeedWaiting(mover, 0.25);
            }
            sim.Step();
            long initialProbe = probe.CurrentValue;
            long initialObserve = observe.CurrentValue;

            var milliseconds = new double[steps];
            uint lastProbeTotal = 0;
            uint peakProbes = 0;
            uint lastCandidateTotal = 0, peakCandidates = 0;
            double busiestProbeStep = 0;
            int peakWaiting = 0, peakRetrying = 0;
            long diagnosticTicks = 0;
            for (int i = 0; i < steps; i++)
            {
                if (i == 12)
                    sim.Destroy(obstacles);
                long start = Stopwatch.GetTimestamp();
                sim.Step();
                milliseconds[i] = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                start = Stopwatch.GetTimestamp();
                var states = sim.CountStates();
                uint probesThisStep = states.Probes - lastProbeTotal;
                if (probesThisStep > peakProbes)
                {
                    peakProbes = probesThisStep;
                    busiestProbeStep = milliseconds[i];
                }
                else if (probesThisStep == peakProbes && probesThisStep > 0)
                {
                    busiestProbeStep = math.max(busiestProbeStep, milliseconds[i]);
                }
                lastProbeTotal = states.Probes;
                peakCandidates = math.max(peakCandidates, states.Candidates - lastCandidateTotal);
                lastCandidateTotal = states.Candidates;
                peakWaiting = math.max(peakWaiting, states.Waiting);
                peakRetrying = math.max(peakRetrying, states.Retrying);
                diagnosticTicks += Stopwatch.GetTimestamp() - start;
            }
            double total = 0;
            foreach (double ms in milliseconds)
                total += ms;
            Array.Sort(milliseconds);
            var final = sim.CountStates();
            UnityEngine.Debug.Log($"Stuck scale {count} monitor={monitoring}: mean={total / steps:F3} ms " +
                $"p95={milliseconds[(int)math.ceil(steps * 0.95f) - 1]:F3} max={milliseconds[steps - 1]:F3} " +
                $"probeMarker={(probe.CurrentValue - initialProbe) / (steps * 1e6):F3} ms/step " +
                $"observeMarker={(observe.CurrentValue - initialObserve) / (steps * 1e6):F3} ms/step " +
                $"peakProbes={peakProbes} busiestProbeStep={busiestProbeStep:F3} ms peakCandidates={peakCandidates} " +
                $"peakWaiting={peakWaiting} peakRetrying={peakRetrying} " +
                $"finalMoving={final.Active} finalWaiting={final.Waiting} finalRetrying={final.Retrying} " +
                $"diagnostics={diagnosticTicks * 1000.0 / Stopwatch.Frequency:F3} ms total");
            Assert.That(peakProbes, Is.EqualTo(monitoring ? count / 2 : 0));
            Assert.That(final.Active, Is.Zero);
        }

        private static void WaitFor(Simulation sim, Entity unit, UnitMovementState state, int frames, float dt = Frame)
        {
            for (int i = 0; i < frames && sim.State(unit).State != state; i++)
                sim.Step(dt);
            Assert.That(sim.State(unit).State, Is.EqualTo(state));
        }

        private sealed class Simulation : IDisposable
        {
            private readonly World _world = new World("Stuck test");
            private readonly UnitStuckDetectionSystem _detector;
            private readonly UnitMoveCommandSystem _commands;
            private readonly UnitMovementSystem _movement;
            private readonly UnitOverlapResolutionSystem _overlap;
            private readonly SimulationSystemGroup _group;
            private readonly EntityQuery _tracked;
            private readonly EntityQuery _active;
            private ulong _unitId;
            internal double Time { get; private set; }
            private EntityManager Manager => _world.EntityManager;

            internal Simulation(bool simulateMotion = true, bool overlap = true, bool avoidance = true)
            {
                _group = _world.GetOrCreateSystemManaged<SimulationSystemGroup>();
                _detector = _world.GetOrCreateSystemManaged<UnitStuckDetectionSystem>();
                _commands = _world.GetOrCreateSystemManaged<UnitMoveCommandSystem>();
                _movement = _world.GetOrCreateSystemManaged<UnitMovementSystem>();
                _overlap = _world.GetOrCreateSystemManaged<UnitOverlapResolutionSystem>();
                _group.AddSystemToUpdateList(_detector);
                _group.AddSystemToUpdateList(_movement);
                _group.AddSystemToUpdateList(_overlap);
                _group.AddSystemToUpdateList(_commands);
                _group.AddSystemToUpdateList(_world.GetOrCreateSystemManaged<UnitSpatialGridSystem>());
                _group.AddSystemToUpdateList(_world.GetOrCreateSystemManaged<UnitWaitingProbeSystem>());
                _group.AddSystemToUpdateList(_world.GetOrCreateSystemManaged<UnitSpawnSystem>());
                _group.AddSystemToUpdateList(_world.GetOrCreateSystemManaged<TransformSystemGroup>());
                _group.SortSystems();
                var avoidanceSettings = LocalAvoidanceSettings.Default;
                avoidanceSettings.EnableLocalAvoidance = avoidance;
                _movement.ConfigureAvoidance(avoidanceSettings);
                _movement.Enabled = simulateMotion;
                _overlap.Enabled = overlap && simulateMotion;
                _tracked = Manager.CreateEntityQuery(ComponentType.ReadOnly<UnitMovementProgress>());
                _active = Manager.CreateEntityQuery(ComponentType.ReadOnly<UnitMovementProgress>(), ComponentType.ReadOnly<UnitMoveTarget>());
            }

            internal Entity Create(float2 position, float2? target, float speed = 1f)
            {
                Entity unit = Manager.CreateEntity(typeof(UnitTag), typeof(UnitId), typeof(UnitSelected),
                    typeof(UnitStats), typeof(UnitMoveTarget), typeof(UnitAvoidanceVelocity),
                    typeof(UnitOverlapCorrection), typeof(UnitMovementProgress), typeof(LocalTransform));
                Manager.SetComponentData(unit, new UnitId { Value = ++_unitId });
                Manager.SetComponentEnabled<UnitSelected>(unit, false);
                Manager.SetComponentData(unit, new UnitStats { MovementSpeed = speed });
                Manager.SetComponentData(unit, new UnitMoveTarget { DestinationXZ = target.GetValueOrDefault() });
                Manager.SetComponentEnabled<UnitMoveTarget>(unit, target.HasValue);
                SetPosition(unit, position);
                return unit;
            }

            internal void SetPosition(Entity unit, float2 position, quaternion? rotation = null)
            {
                var pose = LocalTransform.FromPosition(new float3(position.x, 0f, position.y));
                pose.Rotation = rotation ?? quaternion.identity;
                Manager.SetComponentData(unit, pose);
            }

            internal void Configure(bool monitoring)
            {
                var settings = StuckDetectionSettings.Default;
                settings.EnableStuckDetection = monitoring;
                _detector.Configure(settings);
            }

            internal void EnableMotion(bool enabled) => _movement.Enabled = enabled;
            internal void SeedWaiting(Entity unit, double due)
            {
                var state = State(unit);
                state.State = UnitMovementState.Waiting;
                state.Epoch = _detector.Epoch;
                state.Initialized = true;
                state.NextProbe = due;
                state.RetryDelay = 0.2f;
                state.BestDistance = math.distance(Position(unit), Manager.GetComponentData<UnitMoveTarget>(unit).DestinationXZ);
                Manager.SetComponentData(unit, state);
            }

            internal void Command(Entity unit, float2 target)
            {
                Manager.SetComponentEnabled<UnitSelected>(unit, true);
                _commands.IssueMove(target);
            }
            internal (uint Probes, uint Candidates, int Waiting, int Retrying, int Active) CountStates()
            {
                using var states = _tracked.ToComponentDataArray<UnitMovementProgress>(Allocator.Temp);
                uint probes = 0;
                uint candidates = 0;
                int waiting = 0, retrying = 0;
                foreach (var state in states)
                {
                    probes += state.ProbeCount;
                    candidates += state.ProbeCandidates;
                    waiting += state.State == UnitMovementState.Waiting ? 1 : 0;
                    retrying += state.State == UnitMovementState.Retrying ? 1 : 0;
                }
                return (probes, candidates, waiting, retrying, _active.CalculateEntityCount());
            }
            internal void Destroy(Entity unit) => Manager.DestroyEntity(unit);
            internal void Destroy(NativeArray<Entity> units) => Manager.DestroyEntity(units);
            internal LocalTransform Pose(Entity unit) => Manager.GetComponentData<LocalTransform>(unit);
            internal float2 Position(Entity unit) => Pose(unit).Position.xz;
            internal UnitMovementProgress State(Entity unit) => Manager.GetComponentData<UnitMovementProgress>(unit);
            internal bool IsMoving(Entity unit) => Manager.IsComponentEnabled<UnitMoveTarget>(unit);
            internal UnitAvoidanceVelocity Plan(Entity unit) => Manager.GetComponentData<UnitAvoidanceVelocity>(unit);
            internal float2 Correction(Entity unit) => Manager.GetComponentData<UnitOverlapCorrection>(unit).Displacement;
            internal void SetPlan(Entity unit, UnitAvoidanceVelocity plan) => Manager.SetComponentData(unit, plan);
            internal void Step(float dt = Frame)
            {
                Time += dt;
                _world.SetTime(new TimeData(Time, dt));
                _group.Update();
                Manager.CompleteDependencyBeforeRO<UnitMovementProgress>();
                Manager.CompleteDependencyBeforeRO<LocalTransform>();
            }

            public void Dispose() => _world.Dispose();
        }
    }
}
