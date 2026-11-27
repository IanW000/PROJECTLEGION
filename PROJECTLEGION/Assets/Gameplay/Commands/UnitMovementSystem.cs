using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace ProjectLegion.Gameplay
{
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]
    [UpdateBefore(typeof(TransformSystemGroup))]
    internal partial class UnitMovementSystem : SystemBase
    {
        private const float TurnSpeedRadians = 4f * math.PI;
        private EntityQuery _movingUnits;

        protected override void OnCreate()
        {
            _movingUnits = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<UnitTag, UnitStats>().WithAllRW<LocalTransform, UnitMoveTarget>().Build(this);
            RequireForUpdate(_movingUnits);
        }

        protected override void OnUpdate()
        {
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (deltaTime <= 0f)
                return;

            Dependency = new MoveUnitsJob
            {
                Targets = GetComponentTypeHandle<UnitMoveTarget>(),
                Transforms = GetComponentTypeHandle<LocalTransform>(),
                Stats = GetComponentTypeHandle<UnitStats>(true),
                DeltaTime = deltaTime
            }.ScheduleParallel(_movingUnits, Dependency);
        }

        [BurstCompile]
        private struct MoveUnitsJob : IJobChunk
        {
            public ComponentTypeHandle<UnitMoveTarget> Targets;
            public ComponentTypeHandle<LocalTransform> Transforms;
            [ReadOnly] public ComponentTypeHandle<UnitStats> Stats;
            public float DeltaTime;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                var targets = chunk.GetNativeArray(ref Targets);
                var transforms = chunk.GetNativeArray(ref Transforms);
                var stats = chunk.GetNativeArray(ref Stats);
                var units = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (units.NextEntityIndex(out int index))
                {
                    LocalTransform transform = transforms[index];
                    float2 destination = targets[index].DestinationXZ;
                    float2 offset = destination - transform.Position.xz;
                    float distance = math.length(offset);
                    float step = math.max(0f, stats[index].MovementSpeed) * DeltaTime;
                    if (distance > 0f && step > 0f)
                    {
                        float3 forward = math.forward(transform.Rotation);
                        float currentYaw = math.atan2(forward.x, forward.z);
                        float desiredYaw = math.atan2(offset.x, offset.y);
                        float difference = desiredYaw - currentYaw;
                        float shortestTurn = math.atan2(math.sin(difference), math.cos(difference));
                        float turnLimit = TurnSpeedRadians * DeltaTime;
                        transform.Rotation = quaternion.RotateY(currentYaw + math.clamp(shortestTurn, -turnLimit, turnLimit));
                    }

                    if (distance <= step)
                    {
                        transform.Position.xz = destination;
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
        }
    }
}
