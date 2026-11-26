using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Entities;
using Unity.Mathematics;

namespace ProjectLegion.Gameplay
{
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]
    [UpdateBefore(typeof(UnitMovementSystem))]
    internal partial class UnitMoveCommandSystem : SystemBase
    {
        private EntityQuery _recipients;
        private float2 _destination;
        private bool _hasCommand;

        protected override void OnCreate()
        {
            _recipients = new EntityQueryBuilder(Unity.Collections.Allocator.Temp)
                .WithAll<UnitTag, UnitSelected>().WithPresentRW<UnitMoveTarget>().Build(this);
        }

        internal void IssueMove(float2 destination)
        {
            _destination = destination;
            _hasCommand = true;
        }

        protected override void OnUpdate()
        {
            if (!_hasCommand)
                return;

            _hasCommand = false;
            Dependency = new AssignDestinationJob
            {
                Targets = GetComponentTypeHandle<UnitMoveTarget>(),
                Destination = _destination
            }.ScheduleParallel(_recipients, Dependency);
        }

        [BurstCompile]
        private struct AssignDestinationJob : IJobChunk
        {
            public ComponentTypeHandle<UnitMoveTarget> Targets;
            public float2 Destination;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                var targets = chunk.GetNativeArray(ref Targets);
                var recipients = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (recipients.NextEntityIndex(out int index))
                {
                    targets[index] = new UnitMoveTarget { DestinationXZ = Destination };
                    chunk.SetComponentEnabled(ref Targets, index, true);
                }
            }
        }
    }
}
