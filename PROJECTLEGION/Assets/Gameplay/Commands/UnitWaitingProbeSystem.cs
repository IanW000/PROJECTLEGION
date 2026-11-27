using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Transforms;

namespace ProjectLegion.Gameplay
{
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(UnitMoveCommandSystem))]
    [UpdateAfter(typeof(UnitSpatialGridSystem))]
    [UpdateBefore(typeof(UnitMovementSystem))]
    internal partial class UnitWaitingProbeSystem : SystemBase
    {
        private EntityQuery _units;

        protected override void OnCreate()
        {
            _units = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<UnitTag, UnitMoveTarget, UnitStats, LocalTransform>()
                .WithAllRW<UnitMovementProgress>().Build(this);
            RequireForUpdate(_units);
        }

        protected override void OnUpdate()
        {
            var detector = World.GetExistingSystemManaged<UnitStuckDetectionSystem>();
            var grid = World.GetExistingSystemManaged<UnitSpatialGridSystem>();
            float dt = SystemAPI.Time.DeltaTime;
            if (detector is not { Monitoring: true } || grid is not { Enabled: true } ||
                !(dt > 0f) || !math.isfinite(dt))
                return;

            var snapshot = grid.GetSnapshot();
            var job = new ProbeJob
            {
                Grid = snapshot.Grid,
                Entities = GetEntityTypeHandle(),
                Transforms = GetComponentTypeHandle<LocalTransform>(true),
                Targets = GetComponentTypeHandle<UnitMoveTarget>(true),
                Stats = GetComponentTypeHandle<UnitStats>(true),
                Progress = GetComponentTypeHandle<UnitMovementProgress>(),
                Epoch = detector.Epoch,
                Now = SystemAPI.Time.ElapsedTime,
                Settings = detector.Settings,
                Diameter = 2f * (World.GetExistingSystemManaged<UnitMovementSystem>()?.CollisionRadius ??
                    LocalAvoidanceSettings.Default.CollisionRadius)
            };
            Dependency = job.ScheduleParallel(_units, JobHandle.CombineDependencies(Dependency, snapshot.Build));
            snapshot.RegisterReader(Dependency);
        }

        [BurstCompile]
        private struct ProbeJob : IJobChunk
        {
            private static readonly ProfilerMarker Marker = new ProfilerMarker("RTS.Stuck.Probe");
            internal SpatialGrid Grid;
            [ReadOnly] internal EntityTypeHandle Entities;
            [ReadOnly] internal ComponentTypeHandle<LocalTransform> Transforms;
            [ReadOnly] internal ComponentTypeHandle<UnitMoveTarget> Targets;
            [ReadOnly] internal ComponentTypeHandle<UnitStats> Stats;
            internal ComponentTypeHandle<UnitMovementProgress> Progress;
            internal StuckDetectionSettings Settings;
            internal uint Epoch;
            internal float Diameter;
            internal double Now;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 mask)
            {
                using var scope = Marker.Auto();
                var entities = chunk.GetNativeArray(Entities);
                var transforms = chunk.GetNativeArray(ref Transforms);
                var targets = chunk.GetNativeArray(ref Targets);
                var stats = chunk.GetNativeArray(ref Stats);
                var progress = chunk.GetNativeArray(ref Progress);
                var units = new ChunkEntityEnumerator(useEnabledMask, mask, chunk.Count);
                while (units.NextEntityIndex(out int i))
                {
                    var state = progress[i];
                    if (!StuckProbe.IsWaiting(state, Epoch) || Now < state.NextProbe)
                        continue;

                    state.ProbeCount++;
                    state.NextProbe = Now + math.max(Settings.ProbeInterval, state.RetryDelay);
                    float2 position = transforms[i].Position.xz;
                    if (StuckProbe.HasExit(Grid, entities[i], position, targets[i].DestinationXZ,
                            stats[i].MovementSpeed, Diameter, Settings.ProbeInterval, out uint candidates))
                    {
                        state.State = UnitMovementState.Retrying;
                        // The first trial sample measures this step from the last waiting pose.
                        state.WindowStart = position;
                        state.PreviousPosition = position;
                        state.StartDistance = math.distance(position, targets[i].DestinationXZ);
                        state.WindowSeconds = 0f;
                        state.Travel = 0f;
                    }
                    state.ProbeCandidates += candidates;
                    progress[i] = state;
                }
            }
        }
    }

    internal static class StuckProbe
    {
        internal static bool IsWaiting(UnitMovementProgress progress, uint epoch)
            => progress.Epoch == epoch && progress.State == UnitMovementState.Waiting;

        internal static float Reach(float distance, float speed, float diameter, float interval)
            => math.min(distance, math.clamp(speed * interval, diameter * 0.5f, diameter));

        internal static bool HasExit(SpatialGrid grid, Entity entity, float2 position, float2 target,
            float speed, float diameter, float interval, out uint candidates)
        {
            candidates = 0;
            float distance = math.distance(position, target);
            if (!(distance > 0f) || !math.isfinite(distance) || !(speed > 0f) || !math.isfinite(speed))
                return false;
            float length = Reach(distance, speed, diameter, interval);
            float2 heading = (target - position) / distance;
            if (PathIsClear(grid, entity, position, heading * length, diameter, ref candidates))
                return true;

            // A lateral opening cannot unblock a target slot that is still occupied.
            if (distance <= length && !PathIsClear(grid, entity, target, float2.zero, diameter, ref candidates))
                return false;

            float2 side = new float2(heading.y, -heading.x) * 0.85f;
            return PathIsClear(grid, entity, position, math.normalize(heading + side) * length, diameter, ref candidates) ||
                PathIsClear(grid, entity, position, math.normalize(heading - side) * length, diameter, ref candidates);
        }

        internal static bool PathIsClear(SpatialGrid grid, Entity entity, float2 position, float2 step, float diameter)
        {
            uint candidates = 0;
            return PathIsClear(grid, entity, position, step, diameter, ref candidates);
        }

        private static bool PathIsClear(SpatialGrid grid, Entity entity, float2 position, float2 step, float diameter,
            ref uint scanned)
        {
            float stepSquared = math.lengthsq(step);
            if (!grid.TryQuery(position, math.sqrt(stepSquared) + diameter, entity, out var candidates))
                return false;

            float diameterSquared = diameter * diameter;
            while (candidates.MoveNext(out var candidate))
            {
                float2 offset = position - candidate.PositionXZ;
                float along = math.dot(offset, step);
                float closest = stepSquared > 0f ? math.saturate(-along / stepSquared) : 0f;
                if (math.lengthsq(offset + step * closest) >= diameterSquared)
                    continue;

                // Permit an exit that only reduces an existing penetration.
                if (stepSquared > 0f && along >= 0f && math.lengthsq(offset) < diameterSquared)
                    continue;
                scanned += (uint)candidates.CandidateCount;
                return false;
            }
            scanned += (uint)candidates.CandidateCount;
            return true;
        }
    }
}
