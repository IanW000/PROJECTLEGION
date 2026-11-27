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
    [UpdateBefore(typeof(TransformSystemGroup))]
    internal partial class UnitMovementSystem : SystemBase
    {
        private const float TurnSpeedRadians = 4f * math.PI;
        private EntityQuery _movingUnits;
        private EntityQuery _avoidingUnits;
        private LocalAvoidanceSettings _avoidance = LocalAvoidanceSettings.Default;
        internal float CollisionRadius => _avoidance.CollisionRadius;
        internal bool CalculatedAvoidance { get; private set; }

        protected override void OnStopRunning() => CalculatedAvoidance = false;

        internal void ConfigureAvoidance(LocalAvoidanceSettings settings) => _avoidance = settings.Validated();

        protected override void OnCreate()
        {
            _movingUnits = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<UnitTag, UnitStats>().WithAllRW<LocalTransform, UnitMoveTarget>().Build(this);
            _avoidingUnits = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<UnitTag, UnitStats, LocalTransform, UnitMoveTarget>().WithAllRW<UnitAvoidanceVelocity>().Build(this);
            RequireForUpdate(_movingUnits);
        }

        protected override void OnUpdate()
        {
            CalculatedAvoidance = false;
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (!(deltaTime > 0f) || !math.isfinite(deltaTime))
                return;

            var grid = _avoidance.EnableLocalAvoidance ? World.GetExistingSystemManaged<UnitSpatialGridSystem>() : null;
            var detector = World.GetExistingSystemManaged<UnitStuckDetectionSystem>();
            bool respectWaiting = detector is { Monitoring: true };
            uint epoch = respectWaiting ? detector.Epoch : 0;
            var progress = GetComponentLookup<UnitMovementProgress>(true);
            bool calculateAvoidance = grid is { Enabled: true } && !_avoidingUnits.IsEmptyIgnoreFilter;
            CalculatedAvoidance = calculateAvoidance;
            if (calculateAvoidance)
            {
                var snapshot = grid.GetSnapshot();
                var calculate = new CalculateAvoidanceJob
                {
                    Grid = snapshot.Grid,
                    Entities = GetEntityTypeHandle(),
                    Transforms = GetComponentTypeHandle<LocalTransform>(true),
                    Targets = GetComponentTypeHandle<UnitMoveTarget>(true),
                    Stats = GetComponentTypeHandle<UnitStats>(true),
                    TargetLookup = GetComponentLookup<UnitMoveTarget>(true),
                    StatsLookup = GetComponentLookup<UnitStats>(true),
                    Progress = progress,
                    RespectWaiting = respectWaiting,
                    Epoch = epoch,
                    Results = GetComponentTypeHandle<UnitAvoidanceVelocity>(),
                    Settings = _avoidance,
                    DeltaTime = deltaTime
                };
                Dependency = calculate.ScheduleParallel(_avoidingUnits, JobHandle.CombineDependencies(Dependency, snapshot.Build));
                snapshot.RegisterReader(Dependency);
            }

            Dependency = new MoveUnitsJob
            {
                Targets = GetComponentTypeHandle<UnitMoveTarget>(),
                Transforms = GetComponentTypeHandle<LocalTransform>(),
                Stats = GetComponentTypeHandle<UnitStats>(true),
                Avoidance = GetComponentTypeHandle<UnitAvoidanceVelocity>(),
                Progress = progress,
                Entities = GetEntityTypeHandle(),
                RespectWaiting = respectWaiting,
                Epoch = epoch,
                UseAvoidance = calculateAvoidance,
                DeltaTime = deltaTime
            }.ScheduleParallel(_movingUnits, Dependency);
        }

        [BurstCompile]
        private struct CalculateAvoidanceJob : IJobChunk
        {
            private static readonly ProfilerMarker Marker = new ProfilerMarker("RTS.Avoidance.Calculate");

            public SpatialGrid Grid;
            [ReadOnly] public EntityTypeHandle Entities;
            [ReadOnly] public ComponentTypeHandle<LocalTransform> Transforms;
            [ReadOnly] public ComponentTypeHandle<UnitMoveTarget> Targets;
            [ReadOnly] public ComponentTypeHandle<UnitStats> Stats;
            [ReadOnly] public ComponentLookup<UnitMoveTarget> TargetLookup;
            [ReadOnly] public ComponentLookup<UnitStats> StatsLookup;
            [ReadOnly] public ComponentLookup<UnitMovementProgress> Progress;
            public bool RespectWaiting;
            public uint Epoch;
            public ComponentTypeHandle<UnitAvoidanceVelocity> Results;
            public LocalAvoidanceSettings Settings;
            public float DeltaTime;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                using var scope = Marker.Auto();
                var entities = chunk.GetNativeArray(Entities);
                var transforms = chunk.GetNativeArray(ref Transforms);
                var targets = chunk.GetNativeArray(ref Targets);
                var stats = chunk.GetNativeArray(ref Stats);
                var results = chunk.GetNativeArray(ref Results);
                var units = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (units.NextEntityIndex(out int index))
                {
                    Entity entity = entities[index];
                    results[index] = IsWaiting(entity) ? default : Calculate(entity, transforms[index].Position.xz,
                        targets[index].DestinationXZ, stats[index].MovementSpeed);
                }
            }

            private UnitAvoidanceVelocity Calculate(Entity entity, float2 position, float2 destination, float speed)
            {
                float2 offset = destination - position;
                float distance = math.length(offset);
                if (!math.isfinite(distance) || !math.isfinite(speed) || speed < 0f)
                    return new UnitAvoidanceVelocity { Mode = AvoidanceMove.Hold };
                if (distance == 0f || speed == 0f)
                    return default;

                float2 desired = LocalAvoidance.DesiredVelocity(offset, speed, DeltaTime);
                bool canReach = distance <= speed * DeltaTime;
                if (canReach)
                {
                    var arrival = CheckArrival(entity, position, destination, distance);
                    if (arrival != AvoidanceMove.Steer)
                        return new UnitAvoidanceVelocity { Mode = arrival };
                }

                float2 steering = float2.zero;
                var nearest = LocalAvoidance.FindNearest(Grid, entity, position, Settings, out _);
                // Pairwise yielding can stack into persistent braking in crowds.
                bool allowCrossingYield = nearest.Length <= LocalAvoidance.SparseCrossingNeighborLimit;
                for (int i = 0; i < nearest.Length; i++)
                {
                    var other = nearest[i].Entry;
                    steering += LocalAvoidance.Steering(entity, position, desired, other.Entity,
                        other.PositionXZ, GetDesiredVelocity(other), Settings, allowCrossingYield);
                }

                if (math.all(steering == float2.zero))
                    return new UnitAvoidanceVelocity { Mode = canReach ? AvoidanceMove.Hold : AvoidanceMove.Direct };

                float2 velocity = LocalAvoidance.ClampLength(desired + steering * speed, math.length(desired));
                return new UnitAvoidanceVelocity { Velocity = velocity, Mode = AvoidanceMove.Steer };
            }

            private AvoidanceMove CheckArrival(Entity entity, float2 position, float2 destination, float distance)
            {
                float diameter = 2f * Settings.CollisionRadius;
                float diameterSquared = diameter * diameter;
                // Cover the whole static sweep even if its end lies outside the steering radius.
                // Arrival safety scans all candidates in this query. Top-K is only for steering.
                float searchRadius = math.max(Settings.NeighborRadius, distance + diameter);
                if (!Grid.TryQuery(position, searchRadius, entity, out var candidates))
                    return AvoidanceMove.Hold;

                float2 step = destination - position;
                bool pathConflict = false;
                while (candidates.MoveNext(out var other))
                {
                    if (math.distancesq(destination, other.PositionXZ) < diameterSquared)
                        return AvoidanceMove.Hold;

                    if (pathConflict)
                        continue;

                    // Calculate runs before anybody applies movement. Approximate an active neighbor's
                    // path using its target-limited desired step; a stationary neighbor has zero step.
                    float2 relativeStart = position - other.PositionXZ;
                    float2 relativeStep = step - GetDesiredVelocity(other) * DeltaTime;
                    float lengthSquared = math.lengthsq(relativeStep);
                    float time = lengthSquared > 0f
                        ? math.saturate(-math.dot(relativeStart, relativeStep) / lengthSquared)
                        : 0f;
                    pathConflict = math.lengthsq(relativeStart + relativeStep * time) < diameterSquared;
                }

                return pathConflict ? AvoidanceMove.Steer : AvoidanceMove.Direct;
            }

            private float2 GetDesiredVelocity(SpatialGridEntry other)
            {
                if (!IsWaiting(other.Entity) && TargetLookup.TryGetComponent(other.Entity, out var target) &&
                    TargetLookup.IsComponentEnabled(other.Entity) &&
                    StatsLookup.TryGetComponent(other.Entity, out var stats))
                {
                    return LocalAvoidance.DesiredVelocity(target.DestinationXZ - other.PositionXZ,
                        stats.MovementSpeed, DeltaTime);
                }
                return float2.zero;
            }

            private bool IsWaiting(Entity entity) => RespectWaiting &&
                Progress.TryGetComponent(entity, out var progress) && StuckProbe.IsWaiting(progress, Epoch);
        }

        [BurstCompile]
        private struct MoveUnitsJob : IJobChunk
        {
            private static readonly ProfilerMarker Marker = new ProfilerMarker("RTS.Movement.Apply");

            public ComponentTypeHandle<UnitMoveTarget> Targets;
            public ComponentTypeHandle<LocalTransform> Transforms;
            [ReadOnly] public ComponentTypeHandle<UnitStats> Stats;
            public ComponentTypeHandle<UnitAvoidanceVelocity> Avoidance;
            [ReadOnly] public ComponentLookup<UnitMovementProgress> Progress;
            [ReadOnly] public EntityTypeHandle Entities;
            public bool RespectWaiting;
            public uint Epoch;
            public bool UseAvoidance;
            public float DeltaTime;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                using var scope = Marker.Auto();
                var targets = chunk.GetNativeArray(ref Targets);
                var transforms = chunk.GetNativeArray(ref Transforms);
                var stats = chunk.GetNativeArray(ref Stats);
                var entities = chunk.GetNativeArray(Entities);
                bool hasAvoidance = chunk.Has(ref Avoidance);
                bool useAvoidance = UseAvoidance && hasAvoidance;
                var avoidance = hasAvoidance ? chunk.GetNativeArray(ref Avoidance) : default;
                var units = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (units.NextEntityIndex(out int index))
                {
                    if (RespectWaiting && Progress.TryGetComponent(entities[index], out var progress) &&
                        StuckProbe.IsWaiting(progress, Epoch))
                        continue;
                    LocalTransform transform = transforms[index];
                    var planned = useAvoidance ? avoidance[index] : default;
                    if (planned.Mode == AvoidanceMove.Hold)
                        continue;
                    if (planned.Mode == AvoidanceMove.Steer)
                    {
                        float2 displacement = planned.Velocity * DeltaTime;
                        if (math.any(displacement != float2.zero))
                        {
                            Turn(ref transform, displacement);
                            transform.Position.xz += displacement;
                            transforms[index] = transform;
                        }
                        continue;
                    }

                    float2 destination = targets[index].DestinationXZ;
                    float2 offset = destination - transform.Position.xz;
                    float distance = math.length(offset);
                    float step = math.max(0f, stats[index].MovementSpeed) * DeltaTime;
                    if (distance > 0f && step > 0f)
                        Turn(ref transform, offset);

                    if (distance <= step)
                    {
                        transform.Position.xz = destination;
                        if (hasAvoidance)
                            avoidance[index] = default;
                        chunk.SetComponentEnabled(ref Targets, index, false);
                    }
                    else if (step > 0f)
                    {
                        transform.Position.xz += offset * (step / distance);
                    }
                    else
                    {
                        continue;
                    }

                    transforms[index] = transform;
                }
            }

            private void Turn(ref LocalTransform transform, float2 direction)
            {
                float3 forward = math.forward(transform.Rotation);
                float currentYaw = math.atan2(forward.x, forward.z);
                float desiredYaw = math.atan2(direction.x, direction.y);
                float difference = desiredYaw - currentYaw;
                float shortestTurn = math.atan2(math.sin(difference), math.cos(difference));
                float turnLimit = TurnSpeedRadians * DeltaTime;
                transform.Rotation = quaternion.RotateY(currentYaw + math.clamp(shortestTurn, -turnLimit, turnLimit));
            }
        }
    }
}
