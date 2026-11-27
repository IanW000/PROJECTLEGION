using System;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Transforms;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [Serializable]
    internal struct StuckDetectionSettings
    {
        [SerializeField] internal bool EnableStuckDetection;
        [SerializeField, Min(0.05f)] internal float ObservationWindow;
        [SerializeField, Range(1, 8)] internal int FailedWindowLimit;
        [SerializeField, Range(0.01f, 1f)] internal float ProgressFraction;
        [SerializeField, Min(0f)] internal float MinimumWait;
        [SerializeField, Min(0.05f)] internal float ProbeInterval;
        [SerializeField, Min(0.05f)] internal float TrialWindow;
        [SerializeField, Min(0.05f)] internal float MaximumBackoff;

        internal static StuckDetectionSettings Default => new StuckDetectionSettings
        {
            EnableStuckDetection = true,
            ObservationWindow = 0.4f,
            FailedWindowLimit = 2,
            ProgressFraction = 0.1f,
            MinimumWait = 0.25f,
            ProbeInterval = 0.2f,
            TrialWindow = 0.4f,
            MaximumBackoff = 1f
        };

        internal StuckDetectionSettings Validated()
        {
            var result = this;
            result.ObservationWindow = Clamp(ObservationWindow, 0.4f, 0.05f, 10f);
            result.FailedWindowLimit = math.clamp(FailedWindowLimit, 1, 8);
            result.ProgressFraction = Clamp(ProgressFraction, 0.1f, 0.01f, 1f);
            result.MinimumWait = Clamp(MinimumWait, 0.25f, 0f, 10f);
            result.ProbeInterval = Clamp(ProbeInterval, 0.2f, 0.05f, 10f);
            result.TrialWindow = Clamp(TrialWindow, 0.4f, 0.05f, 10f);
            result.MaximumBackoff = Clamp(MaximumBackoff, 1f, result.ProbeInterval, 10f);
            return result;
        }

        private static float Clamp(float value, float fallback, float min, float max)
            => math.clamp(math.isfinite(value) ? value : fallback, min, max);
    }

    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(UnitMovementSystem))]
    [UpdateAfter(typeof(UnitOverlapResolutionSystem))]
    [UpdateBefore(typeof(TransformSystemGroup))]
    internal partial class UnitStuckDetectionSystem : SystemBase
    {
        private EntityQuery _units;
        private bool _resetPending;
        internal StuckDetectionSettings Settings { get; private set; } = StuckDetectionSettings.Default;
        internal uint Epoch { get; private set; } = 1;
        internal bool Monitoring => Enabled && Settings.EnableStuckDetection;

        internal void Configure(StuckDetectionSettings settings)
        {
            if (Settings.EnableStuckDetection != settings.EnableStuckDetection)
            {
                Epoch++;
                _resetPending = !settings.EnableStuckDetection;
            }
            Settings = settings.Validated();
        }

        protected override void OnCreate()
        {
            _units = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<UnitTag, UnitMoveTarget, UnitStats, LocalTransform>()
                .WithAllRW<UnitMovementProgress>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState).Build(this);
            RequireForUpdate(_units);
        }

        protected override void OnUpdate()
        {
            bool resetOnly = !Monitoring;
            float dt = SystemAPI.Time.DeltaTime;
            if (resetOnly ? !_resetPending : !(dt > 0f) || !math.isfinite(dt))
                return;

            var grid = World.GetExistingSystemManaged<UnitSpatialGridSystem>();
            if (grid == null)
                return;
            var snapshot = grid.GetSnapshot();
            var movement = World.GetExistingSystemManaged<UnitMovementSystem>();
            var job = new ObserveJob
            {
                Grid = snapshot.Grid,
                HasGrid = grid.Enabled,
                Entities = GetEntityTypeHandle(),
                Transforms = GetComponentTypeHandle<LocalTransform>(true),
                Targets = GetComponentTypeHandle<UnitMoveTarget>(true),
                Stats = GetComponentTypeHandle<UnitStats>(true),
                Progress = GetComponentTypeHandle<UnitMovementProgress>(),
                Avoidance = GetComponentTypeHandle<UnitAvoidanceVelocity>(),
                HonorHold = movement is { Enabled: true, CalculatedAvoidance: true },
                Diameter = 2f * (movement?.CollisionRadius ?? LocalAvoidanceSettings.Default.CollisionRadius),
                ResetOnly = resetOnly,
                Epoch = Epoch,
                DeltaTime = dt,
                Now = SystemAPI.Time.ElapsedTime,
                Settings = Settings
            };
            Dependency = job.ScheduleParallel(_units, JobHandle.CombineDependencies(Dependency, snapshot.Build));
            snapshot.RegisterReader(Dependency);
            _resetPending = false;
        }

        [BurstCompile]
        private struct ObserveJob : IJobChunk
        {
            private static readonly ProfilerMarker Marker = new ProfilerMarker("RTS.Stuck.Observe");
            internal SpatialGrid Grid;
            internal bool HasGrid;
            [ReadOnly] internal EntityTypeHandle Entities;
            [ReadOnly] internal ComponentTypeHandle<LocalTransform> Transforms;
            [ReadOnly] internal ComponentTypeHandle<UnitMoveTarget> Targets;
            [ReadOnly] internal ComponentTypeHandle<UnitStats> Stats;
            internal ComponentTypeHandle<UnitMovementProgress> Progress;
            internal ComponentTypeHandle<UnitAvoidanceVelocity> Avoidance;
            internal bool HonorHold;
            internal bool ResetOnly;
            internal float Diameter;
            internal uint Epoch;
            internal float DeltaTime;
            internal double Now;
            internal StuckDetectionSettings Settings;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 mask)
            {
                using var scope = Marker.Auto();
                var entities = chunk.GetNativeArray(Entities);
                var transforms = chunk.GetNativeArray(ref Transforms);
                var targets = chunk.GetNativeArray(ref Targets);
                var stats = chunk.GetNativeArray(ref Stats);
                var progress = chunk.GetNativeArray(ref Progress);
                bool hasAvoidance = chunk.Has(ref Avoidance);
                var avoidance = hasAvoidance ? chunk.GetNativeArray(ref Avoidance) : default;
                for (int i = 0; i < chunk.Count; i++)
                {
                    var state = progress[i];
                    bool previouslyWaiting = state.State == UnitMovementState.Waiting;
                    if (state.Epoch != Epoch)
                        state.Reset(Epoch);
                    if (ResetOnly || !chunk.IsComponentEnabled(ref Targets, i))
                    {
                        if (state.Initialized)
                            state.Reset(Epoch);
                    }
                    else
                    {
                        bool hold = HonorHold && hasAvoidance && avoidance[i].Mode == AvoidanceMove.Hold;
                        Observe(ref state, entities[i], transforms[i].Position.xz, targets[i].DestinationXZ,
                            stats[i].MovementSpeed, hold);
                    }
                    if (hasAvoidance && (state.State == UnitMovementState.Waiting || (ResetOnly && previouslyWaiting)))
                        avoidance[i] = default;
                    progress[i] = state;
                }
            }

            private void Observe(ref UnitMovementProgress state, Entity entity, float2 position, float2 target,
                float speed, bool hold)
            {
                float distance = math.distance(position, target);
                if (!math.isfinite(distance) || !math.isfinite(speed) || speed <= 0f)
                {
                    state.Reset(Epoch);
                    return;
                }

                if (!state.Initialized)
                {
                    state.Initialized = true;
                    state.BestDistance = distance;
                    BeginWindow(ref state, position, distance);
                    return;
                }

                if (state.State != UnitMovementState.Moving || state.FailedWindows > 0)
                    state.BlockedSeconds += DeltaTime;
                if (state.State == UnitMovementState.Waiting)
                    return;

                state.Travel += math.distance(position, state.PreviousPosition);
                state.PreviousPosition = position;
                state.WindowSeconds += DeltaTime;
                bool trial = state.State == UnitMovementState.Retrying;
                if (state.WindowSeconds < (trial ? Settings.TrialWindow : Settings.ObservationWindow))
                    return;

                float threshold = math.min(speed * state.WindowSeconds, state.StartDistance) * Settings.ProgressFraction;
                bool newBest = state.BestDistance - distance >= threshold;
                float net = math.distance(position, state.WindowStart);
                // Straight lateral detours get a bounded grace period. Repeated reversals have low
                // net displacement compared with their traveled distance.
                bool detour = net >= threshold && net >= state.Travel * 0.5f && state.DetourWindows < 6;
                bool progress = threshold > 0f && (newBest || detour);
                if (newBest)
                {
                    state.BestDistance = distance;
                    state.DetourWindows = 0;
                }
                else if (detour)
                    state.DetourWindows++;

                float length = StuckProbe.Reach(distance, speed, Diameter, Settings.ProbeInterval);
                bool blocked = !progress && (hold || (HasGrid && distance > 0f &&
                    !StuckProbe.PathIsClear(Grid, entity, position, (target - position) * (length / distance), Diameter)));
                if (!blocked)
                {
                    state.State = UnitMovementState.Moving;
                    state.Reason = UnitBlockReason.None;
                    state.FailedWindows = 0;
                    state.BlockedSeconds = 0f;
                    state.RetryDelay = 0f;
                    state.NextProbe = 0;
                }
                else
                {
                    state.Reason = hold ? UnitBlockReason.ArrivalHold : UnitBlockReason.Congestion;
                    state.BlockedSeconds = math.max(state.BlockedSeconds, state.WindowSeconds);
                    state.FailedWindows++;
                    if (trial || state.FailedWindows >= Settings.FailedWindowLimit)
                    {
                        state.State = UnitMovementState.Waiting;
                        state.RetryDelay = trial
                            ? math.min(Settings.MaximumBackoff, math.max(Settings.ProbeInterval, state.RetryDelay * 2f))
                            : Settings.ProbeInterval;
                        uint hash = math.hash(new int2(entity.Index, entity.Version));
                        float offset = (hash & 255u) * (1f / 255f) * Settings.ProbeInterval;
                        state.NextProbe = Now + math.max(Settings.MinimumWait, state.RetryDelay) + offset;
                    }
                }
                BeginWindow(ref state, position, distance);
            }

            private static void BeginWindow(ref UnitMovementProgress state, float2 position, float distance)
            {
                state.WindowStart = position;
                state.PreviousPosition = position;
                state.StartDistance = distance;
                state.WindowSeconds = 0f;
                state.Travel = 0f;
            }
        }
    }
}
