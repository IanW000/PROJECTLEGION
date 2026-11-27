using System;
using System.Collections;
using System.Diagnostics;
using NUnit.Framework;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Scenes;
using Unity.Transforms;
#if UNITY_EDITOR
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
#endif

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsArrivalManagementTests
    {
        private static readonly float2 Start = new float2(-1.2f, 0f);
        private static readonly float2 FirstAlternative = new float2(0f, 0.9f);

        [TestCase(false)]
        [TestCase(true)]
        public void ExactArrivalWinsWhenTheOriginalSlotBecomesFree(bool initiallyOccupied)
        {
            using var sim = new Simulation();
            Entity mover = sim.Create(Start, float2.zero);
            if (initiallyOccupied)
            {
                Entity blocker = sim.Create(float2.zero, null);
                for (int step = 0; step < 10; step++)
                {
                    sim.Step();
                    Assert.That(sim.Active(mover), Is.True);
                    Assert.That(sim.Arrival(mover).AlternativeIndex, Is.Zero);
                }
                sim.Destroy(blocker);
            }
            sim.Until(() => !sim.Active(mover));
            Assert.That(sim.Arrival(mover).Status, Is.EqualTo(ArrivalStatus.OriginalCompleted));
            Assert.That(sim.Position(mover), Is.EqualTo(float2.zero));
            CheckSettled(sim, mover);
        }

        [Test]
        public void PersistentOccupancyReservesThenCompletesAndReleasesOriginalClaim()
        {
            using var sim = new Simulation();
            Entity mover = sim.Create(Start, float2.zero, aged: true);
            Entity blocker = sim.Create(float2.zero, null);
            sim.Step();
            Assert.That(sim.Arrival(mover).Status, Is.EqualTo(ArrivalStatus.Alternative));
            Assert.That(sim.Active(mover), Is.True);
            float2 alternative = sim.Target(mover);
            Assert.That(alternative, Is.EqualTo(FirstAlternative));
            sim.Until(() => !sim.Active(mover));
            Assert.That(sim.Position(mover), Is.EqualTo(alternative));
            Assert.That(sim.Arrival(mover).OriginalSlot, Is.EqualTo(float2.zero));
            Assert.That(sim.Arrival(mover).Status, Is.EqualTo(ArrivalStatus.AlternativeCompleted));
            Assert.That(sim.Arrival(mover).AlternativeIndex, Is.Zero);
            CheckSettled(sim, mover);
            sim.Configure(false);
            sim.Step();
            sim.Configure(true);
            sim.Step();
            Assert.That(sim.Arrival(mover).Status, Is.EqualTo(ArrivalStatus.AlternativeCompleted));
            Assert.That(sim.Position(mover), Is.EqualTo(alternative));

            sim.Destroy(blocker);
            // The new command's first fallback is the previously completed unit's original slot.
            Entity claimant = sim.Create(new float2(-1.2f, -0.9f), new float2(0f, -0.9f), aged: true);
            sim.Create(new float2(0f, -0.9f), null);
            sim.Step();
            Assert.That(sim.Target(claimant), Is.EqualTo(float2.zero));
            Assert.That(sim.Arrival(mover).OriginalSlot, Is.EqualTo(float2.zero));
        }

        [Test]
        public void PendingOriginalSlotsAndCompetingReservationsProtectTheirSpacing()
        {
            using var sim = new Simulation(move: false);
            var originalOwner = sim.Create(new float2(5f, 8f), FirstAlternative);
            var first = sim.Create(Start, float2.zero, aged: true);
            var second = sim.Create(Start, new float2(0f, 0.02f), aged: true);
            sim.Create(float2.zero, null);
            sim.Step();
            Assert.That(sim.Target(originalOwner), Is.EqualTo(FirstAlternative));
            Assert.That(sim.Arrival(first).Status, Is.EqualTo(ArrivalStatus.Alternative));
            Assert.That(sim.Arrival(second).Status, Is.EqualTo(ArrivalStatus.Alternative));
            Assert.That(math.distance(sim.Target(first), sim.Target(second)), Is.GreaterThanOrEqualTo(0.7f));
            Assert.That(math.distance(sim.Target(first), FirstAlternative), Is.GreaterThanOrEqualTo(0.7f));
            Assert.That(math.distance(sim.Target(second), FirstAlternative), Is.GreaterThanOrEqualTo(0.7f));
            Assert.That(sim.Metrics().Conflicts, Is.GreaterThan(0));
        }

        [Test]
        public void DestructionAndCommandReplacementFreeReservationsInTheNextBatch()
        {
            using var sim = new Simulation(move: false, candidateLimit: 1);
            sim.Create(float2.zero, null);
            var first = sim.Create(Start, float2.zero, aged: true);
            sim.Step();
            Assert.That(sim.Target(first), Is.EqualTo(FirstAlternative));
            sim.Destroy(first);
            var replacement = sim.Create(Start, float2.zero, aged: true);
            sim.Step();
            Assert.That(sim.Target(replacement), Is.EqualTo(FirstAlternative));

            uint originalVersion = sim.Arrival(replacement).CommandVersion;
            sim.Command(replacement, new float2(5f, 0f));
            var claimant = sim.Create(Start, float2.zero, aged: true);
            sim.Step();
            Assert.That(sim.Target(claimant), Is.EqualTo(FirstAlternative));
            Assert.That(sim.Arrival(replacement).CommandVersion, Is.EqualTo(originalVersion + 1));
            Assert.That(sim.Target(replacement), Is.EqualTo(new float2(5f, 0f)));
            Assert.That(sim.Arrival(replacement).FailedCandidates, Is.Zero);
            Assert.That(sim.Arrival(replacement).AlternativeIndex, Is.Zero);
        }

        [Test]
        public void FailedAlternativesAreExcludedUntilANewCommandAndDisableRestoresOriginal()
        {
            using var sim = new Simulation(move: false);
            sim.Create(float2.zero, null);
            var mover = sim.Create(Start, float2.zero, aged: true);
            sim.Step();
            float2 first = sim.Target(mover);
            sim.Until(() => sim.Arrival(mover).FailedAttempts == 1 && sim.Arrival(mover).AlternativeIndex > 0);
            Assert.That(sim.Arrival(mover).FailedCandidates, Is.Not.Zero);
            Assert.That(sim.Target(mover), Is.Not.EqualTo(first));
            sim.Configure(false);
            sim.Step();
            Assert.That(sim.Target(mover), Is.EqualTo(float2.zero));
            Assert.That(sim.Active(mover), Is.True);
            Assert.That(sim.Arrival(mover).AlternativeIndex, Is.Zero);
            Assert.That(sim.Metrics().Queued, Is.Zero);
            sim.Configure(true);
            sim.Step();
            Assert.That(sim.Arrival(mover).Status, Is.EqualTo(ArrivalStatus.TemporarilyOccupied));
            sim.Command(mover, float2.zero);
            sim.Step();
            Assert.That(sim.Arrival(mover).FailedAttempts, Is.Zero);
            Assert.That(sim.Arrival(mover).FailedCandidates, Is.Zero);
        }

        [Test]
        public void SlowProgressKeepsAnAlternativeReserved()
        {
            using var sim = new Simulation();
            sim.Create(float2.zero, null);
            var mover = sim.Create(Start, float2.zero, aged: true, speed: 0.08f);
            sim.Step();
            var alternative = sim.Target(mover);
            for (int i = 0; i < 56; i++)
                sim.Step();
            Assert.That(sim.Active(mover), Is.True);
            Assert.That(sim.Arrival(mover).Status, Is.EqualTo(ArrivalStatus.Alternative));
            Assert.That(sim.Arrival(mover).FailedAttempts, Is.Zero);
            Assert.That(sim.Target(mover), Is.EqualTo(alternative));
        }

        [Test]
        public void AnOlderPendingRequestBeatsNewWorkWithALowerUnitId()
        {
            using var sim = new Simulation(move: false, budget: 1);
            Entity Request(float2 slot, ulong id)
            {
                sim.Create(slot, null);
                var unit = sim.Create(slot + Start, slot, aged: true);
                sim.SetId(unit, id);
                return unit;
            }
            var first = Request(float2.zero, 10);
            var older = Request(new float2(0f, 8f), 20);
            sim.Step();
            Assert.That(sim.Arrival(first).Status, Is.EqualTo(ArrivalStatus.Alternative));
            Assert.That(sim.Arrival(older).Status, Is.EqualTo(ArrivalStatus.PersistentlyOccupied));
            var newcomer = Request(new float2(0f, 16f), 1);
            sim.Step();
            Assert.That(sim.Arrival(older).Status, Is.EqualTo(ArrivalStatus.Alternative));
            Assert.That(sim.Arrival(newcomer).Status, Is.EqualTo(ArrivalStatus.PersistentlyOccupied));
            sim.Step();
            Assert.That(sim.Arrival(newcomer).Status, Is.EqualTo(ArrivalStatus.Alternative));
        }

        [Test]
        public void ManagedArrivalStillChecksTheLastStepWithSteeringDisabled()
        {
            using var sim = new Simulation(avoidance: false);
            var goal = new float2(0.04f, 0f);
            var mover = sim.Create(float2.zero, goal);
            var blocker = sim.Create(goal, null);
            sim.Step();
            Assert.That(sim.Active(mover), Is.True);
            Assert.That(sim.Position(mover), Is.EqualTo(float2.zero));
            sim.Destroy(blocker);
            sim.Step();
            Assert.That(sim.Active(mover), Is.False);
            Assert.That(sim.Arrival(mover).Status, Is.EqualTo(ArrivalStatus.OriginalCompleted));
        }

        [Test]
        public void AThousandFixedUnitsOverTheSlotCannotForceAnUnsafeAlternative()
        {
            using var sim = new Simulation(move: false);
            for (int i = 0; i < 1024; i++)
                sim.Create(new float2((i % 32 - 16) * 0.2f, (i / 32 - 16) * 0.2f), null);
            var mover = sim.Create(Start, float2.zero, aged: true);
            for (int i = 0; i < 12; i++)
                sim.Step();
            Assert.That(sim.Active(mover), Is.True);
            Assert.That(sim.Target(mover), Is.EqualTo(float2.zero));
            Assert.That(sim.Arrival(mover).Status, Is.EqualTo(ArrivalStatus.NoAlternative));
        }

        [Test]
        public void ABlockedRouteWithAnOpenDestinationDoesNotChangeTheCommand()
        {
            using var sim = new Simulation();
            var mover = sim.Create(Start, new float2(3f, 0f));
            for (int i = 0; i < 6; i++)
                sim.Create(new float2((i / 2) * 0.65f, (i % 2 == 0 ? -1 : 1) * 0.4f), null);
            sim.Until(() => sim.Arrival(mover).Status == ArrivalStatus.RouteBlocked);
            Assert.That(sim.Active(mover), Is.True);
            Assert.That(sim.Target(mover), Is.EqualTo(new float2(3f, 0f)));
        }

        [Test]
        public void SuccessfulNormalFormationPreservesEveryAssignedSlot()
        {
            using var sim = new Simulation();
            var units = new Entity[9];
            for (int i = 0; i < units.Length; i++)
            {
                units[i] = sim.Create(new float2(-5f + i / 3 * 0.9f, i % 3 * 0.9f), null, speed: 4f);
                sim.Select(units[i]);
            }
            sim.Issue(new float2(3f, 0f));
            sim.Step();
            var assigned = Array.ConvertAll(units, unit => sim.Arrival(unit).OriginalSlot);
            sim.Until(() => Array.TrueForAll(units, unit => !sim.Active(unit)));
            for (int i = 0; i < units.Length; i++)
            {
                Assert.That(sim.Position(units[i]), Is.EqualTo(assigned[i]));
                Assert.That(sim.Arrival(units[i]).Status, Is.EqualTo(ArrivalStatus.OriginalCompleted));
            }
        }

#if UNITY_EDITOR
        [UnityTest]
        public IEnumerator SubSceneUnloadReleasesAnActiveReservation()
        {
            var previous = SceneManager.GetActiveScene();
            var scene = EditorSceneManager.LoadSceneInPlayMode("Assets/Gameplay/Scenes/RtsPrototype.unity",
                new LoadSceneParameters(LoadSceneMode.Additive));
            World world = null;
            Entity blocker = Entity.Null;
            Entity claimant = Entity.Null;
            try
            {
                yield return null;
                world = World.DefaultGameObjectInjectionWorld;
                var manager = world.EntityManager;
                using var owned = manager.CreateEntityQuery(typeof(UnitTag), typeof(UnitArrivalState), typeof(SceneTag));
                for (int frame = 0; frame < 300 && owned.IsEmptyIgnoreFilter; frame++)
                    yield return null;
                Assert.That(owned.IsEmptyIgnoreFilter, Is.False);
                Entity owner;
                using (var units = owned.ToEntityArray(Allocator.Temp))
                    owner = units[0];
                var slot = new float2(100f, 100f);
                manager.SetComponentData(owner, LocalTransform.FromPosition(new float3(slot.x - 1.2f, 0f, slot.y)));
                manager.SetComponentData(owner, new UnitStats { MovementSpeed = 0.01f });
                SetCommand(manager, owner, slot, true);
                blocker = Create(manager, slot, null);
                for (int frame = 0; frame < 300 && manager.GetComponentData<UnitArrivalState>(owner).AlternativeIndex == 0; frame++)
                    yield return null;
                Assert.That(manager.GetComponentData<UnitArrivalState>(owner).AlternativeIndex, Is.GreaterThan(0));
                var reserved = manager.GetComponentData<UnitMoveTarget>(owner).DestinationXZ;
                Assert.That(manager.IsComponentEnabled<UnitMoveTarget>(owner), Is.True);
                UnityEngine.GameObject.Find("RTS Units").GetComponent<SubScene>().enabled = false;
                for (int frame = 0; frame < 300 && manager.Exists(owner); frame++)
                    yield return null;
                Assert.That(manager.Exists(owner), Is.False);

                claimant = Create(manager, slot + Start, slot, true, 0.01f);
                for (int frame = 0; frame < 300 && manager.GetComponentData<UnitArrivalState>(claimant).AlternativeIndex == 0; frame++)
                    yield return null;
                Assert.That(manager.GetComponentData<UnitArrivalState>(claimant).AlternativeIndex, Is.GreaterThan(0));
                Assert.That(manager.GetComponentData<UnitMoveTarget>(claimant).DestinationXZ, Is.EqualTo(reserved));
            }
            finally
            {
                if (world is { IsCreated: true })
                {
                    if (world.EntityManager.Exists(blocker))
                        world.EntityManager.DestroyEntity(blocker);
                    if (world.EntityManager.Exists(claimant))
                        world.EntityManager.DestroyEntity(claimant);
                }
                if (previous.IsValid() && previous.isLoaded)
                    SceneManager.SetActiveScene(previous);
                SceneManager.UnloadSceneAsync(scene);
            }
        }
#endif

        [TestCase(1000)]
        [TestCase(10000)]
        public void BudgetServesEverySimultaneousRequestWithoutStarvation(int count)
        {
            Measure(count, false);
            Measure(count, true);
        }

        private static void Measure(int count, bool occupied)
        {
            const int budget = 128;
            using var sim = new Simulation(move: false, budget: budget);
            using var observe = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "RTS.Arrival.Observe");
            using var search = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "RTS.Arrival.Search");
            using var arbitrate = ProfilerRecorder.StartNew(ProfilerCategory.Scripts, "RTS.Arrival.Arbitrate");
            for (int i = 0; i < count / 2; i++)
            {
                var slot = new float2((i % 100) * 6f, (i / 100) * 6f);
                sim.Create(slot + Start, slot, aged: occupied);
                sim.Create(slot + (occupied ? float2.zero : new float2(0f, 2.5f)), null);
            }
            int batches = (count / 2 + budget - 1) / budget;
            var times = new double[batches];
            var observation = new double[batches];
            var searches = new double[batches];
            var arbitration = new double[batches];
            long prevObserve = 0, prevSearch = 0, prevArbitrate = 0;
            int served = 0, reserved = 0, conflicts = 0, peakQueued = 0;
            double maxQueue = 0;
            long diagnostics = 0;
            for (int batch = 0; batch < batches; batch++)
            {
                long start = Stopwatch.GetTimestamp();
                sim.Step();
                times[batch] = (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
                start = Stopwatch.GetTimestamp();
                var metrics = sim.Metrics();
                ObserveMarker(observe, ref prevObserve, observation, batch);
                ObserveMarker(search, ref prevSearch, searches, batch);
                ObserveMarker(arbitrate, ref prevArbitrate, arbitration, batch);
                served += metrics.Served;
                reserved += metrics.Reserved;
                conflicts += metrics.Conflicts;
                peakQueued = math.max(peakQueued, metrics.Queued);
                maxQueue = math.max(maxQueue, metrics.MaxQueueDelay);
                diagnostics += Stopwatch.GetTimestamp() - start;
                Assert.That(metrics.Served, Is.LessThanOrEqualTo(budget));
            }
            Assert.That(served, Is.EqualTo(occupied ? count / 2 : 0));
            Assert.That(reserved, Is.EqualTo(served));
            Assert.That(maxQueue, Is.LessThanOrEqualTo((batches - 1) * 0.05 + 0.0001));
            UnityEngine.Debug.Log($"Arrival scale {count} occupied={occupied}: pipeline={Summary(times)} " +
                $"observe={Summary(observation)} search={Summary(searches)} arbitrate={Summary(arbitration)} " +
                $"batches={batches} served={served} reserved={reserved} conflicts={conflicts} peakQueued={peakQueued} " +
                $"maxQueue={maxQueue:F3}s nativeBuffers={sim.BufferBytes} bytes diagnostics=" +
                $"{diagnostics * 1000d / Stopwatch.Frequency:F3} ms");
        }

        private static void ObserveMarker(ProfilerRecorder recorder, ref long previous, double[] values, int index)
        {
            long current = recorder.CurrentValue;
            values[index] = (current - previous) / 1e6;
            previous = current;
        }

        private static string Summary(double[] values)
        {
            double total = 0;
            foreach (double value in values)
                total += value;
            Array.Sort(values);
            return $"{total / values.Length:F3}/{values[(int)math.ceil(values.Length * 0.95f) - 1]:F3}/{values[values.Length - 1]:F3}ms";
        }

        private static void CheckSettled(Simulation sim, Entity unit)
        {
            var pose = sim.Pose(unit);
            for (int i = 0; i < 60; i++)
            {
                sim.Step();
                Assert.That(sim.Pose(unit).Position, Is.EqualTo(pose.Position));
                Assert.That(sim.Pose(unit).Rotation.value, Is.EqualTo(pose.Rotation.value));
            }
        }

        private static Entity Create(EntityManager manager, float2 position, float2? slot, bool aged = false, float speed = 1f)
        {
            Entity unit = manager.CreateEntity(typeof(UnitTag), typeof(UnitSelected), typeof(UnitId),
                typeof(UnitStats), typeof(UnitMoveTarget), typeof(UnitAvoidanceVelocity), typeof(UnitMovementProgress),
                typeof(UnitArrivalState), typeof(UnitOverlapCorrection), typeof(LocalTransform));
            manager.SetComponentData(unit, new UnitId { Value = (ulong)unit.Index });
            manager.SetComponentData(unit, new UnitStats { MovementSpeed = speed });
            manager.SetComponentData(unit, LocalTransform.FromPosition(new float3(position.x, 0f, position.y)));
            manager.SetComponentEnabled<UnitSelected>(unit, false);
            if (slot.HasValue)
                SetCommand(manager, unit, slot.Value, aged);
            else
                manager.SetComponentEnabled<UnitMoveTarget>(unit, false);
            return unit;
        }

        private static void SetCommand(EntityManager manager, Entity entity, float2 slot, bool aged)
        {
            manager.SetComponentData(entity, new UnitMoveTarget { DestinationXZ = slot });
            manager.SetComponentEnabled<UnitMoveTarget>(entity, true);
            manager.SetComponentData(entity, new UnitArrivalState
            {
                OriginalSlot = slot, CommandVersion = 1, FormationForward = new float2(1f, 0f), Spacing = 0.9f,
                OccupiedSince = aged ? -10 : 0, LastProgressTime = aged ? -10 : 0, BestDistance = aged ? 0 : float.MaxValue
            });
        }

        private sealed class Simulation : IDisposable
        {
            private readonly World _world = new World("Arrival management test");
            private readonly SimulationSystemGroup _group;
            private readonly UnitArrivalSystem _arrival;
            private readonly UnitMoveCommandSystem _commands;
            private ArrivalManagementSettings _settings;
            private double _time;
            private EntityManager Manager => _world.EntityManager;

            internal Simulation(bool move = true, int budget = 128, int candidateLimit = 16, bool avoidance = true)
            {
                _group = _world.GetOrCreateSystemManaged<SimulationSystemGroup>();
                _arrival = Add<UnitArrivalSystem>();
                _commands = Add<UnitMoveCommandSystem>();
                Add<UnitSpatialGridSystem>();
                Add<UnitWaitingProbeSystem>();
                var movement = Add<UnitMovementSystem>();
                var avoidanceSettings = LocalAvoidanceSettings.Default;
                avoidanceSettings.EnableLocalAvoidance = avoidance;
                movement.ConfigureAvoidance(avoidanceSettings);
                movement.Enabled = move;
                Add<UnitOverlapResolutionSystem>().Enabled = move;
                Add<UnitStuckDetectionSystem>().Enabled = move;
                Add<UnitSpawnSystem>();
                Add<TransformSystemGroup>();
                _group.SortSystems();
                _settings = ArrivalManagementSettings.Default;
                _settings.BatchInterval = 0.05f;
                _settings.OccupancySeconds = 0.65f;
                _settings.RequestBudget = budget;
                _settings.CandidateLimit = candidateLimit;
                Configure(true);
            }

            private T Add<T>() where T : ComponentSystemBase
            {
                var system = _world.GetOrCreateSystemManaged<T>();
                _group.AddSystemToUpdateList(system);
                return system;
            }

            internal void Configure(bool enabled)
            {
                _settings.EnableArrivalManagement = enabled;
                _arrival.Configure(_settings);
            }
            internal Entity Create(float2 position, float2? slot, bool aged = false, float speed = 1f)
                => RtsArrivalManagementTests.Create(Manager, position, slot, aged, speed);
            internal void Select(Entity entity) => Manager.SetComponentEnabled<UnitSelected>(entity, true);
            internal void SetId(Entity entity, ulong id) => Manager.SetComponentData(entity, new UnitId { Value = id });
            internal void Issue(float2 target) => _commands.IssueMove(target);
            internal void Command(Entity entity, float2 target)
            {
                Select(entity);
                Issue(target);
            }
            internal void Destroy(Entity entity) => Manager.DestroyEntity(entity);
            internal float2 Target(Entity entity) => Manager.GetComponentData<UnitMoveTarget>(entity).DestinationXZ;
            internal bool Active(Entity entity) => Manager.IsComponentEnabled<UnitMoveTarget>(entity);
            internal UnitArrivalState Arrival(Entity entity) => Manager.GetComponentData<UnitArrivalState>(entity);
            internal LocalTransform Pose(Entity entity) => Manager.GetComponentData<LocalTransform>(entity);
            internal float2 Position(Entity entity) => Pose(entity).Position.xz;
            internal UnitArrivalSystem.BatchMetrics Metrics() => _arrival.ReadMetrics();
            internal long BufferBytes => _arrival.PersistentBufferBytes;

            internal void Step()
            {
                const float dt = 0.05f;
                _world.SetTime(new TimeData(_time += dt, dt));
                _group.Update();
                Manager.CompleteDependencyBeforeRO<UnitArrivalState>();
                Manager.CompleteDependencyBeforeRO<LocalTransform>();
            }

            internal void Until(Func<bool> reached)
            {
                for (int i = 0; i < 180 && !reached(); i++)
                    Step();
                Assert.That(reached(), Is.True);
            }
            public void Dispose() => _world.Dispose();
        }
    }
}
