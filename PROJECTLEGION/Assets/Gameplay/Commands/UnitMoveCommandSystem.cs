using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace ProjectLegion.Gameplay
{
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]
    [UpdateBefore(typeof(UnitMovementSystem))]
    internal partial class UnitMoveCommandSystem : SystemBase
    {
        private EntityQuery _recipients;
        private NativeList<FormationSlotAssignment.Unit> _units;
        private JobHandle _bufferDependency;
        private float2 _destination;
        private float _spacing;
        private bool _hasCommand;

        protected override void OnCreate()
        {
            _recipients = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<UnitTag, UnitSelected, UnitId, LocalTransform>()
                .WithPresentRW<UnitMoveTarget>().Build(this);
            _units = new NativeList<FormationSlotAssignment.Unit>(1024, Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            _bufferDependency.Complete();
            _units.Dispose();
        }

        internal void IssueMove(float2 destination, float spacing = RectangularFormation.DefaultSpacing)
        {
            _destination = destination;
            _spacing = spacing;
            _hasCommand = true;
        }

        internal void CancelPending() => _hasCommand = false;

        protected override void OnUpdate()
        {
            if (!_hasCommand)
                return;

            _hasCommand = false;
            var targets = GetComponentLookup<UnitMoveTarget>();
            var progress = GetComponentLookup<UnitMovementProgress>();
            var arrival = GetComponentLookup<UnitArrivalState>();
            var cleared = new ClearUnitsJob { Units = _units }
                .Schedule(JobHandle.CombineDependencies(Dependency, _bufferDependency));
            // Serial chunk gathering allows the native list to grow on demand without a main-thread count or sync.
            var gathered = new GatherSelectedUnitsJob
            {
                Units = _units,
                Entities = GetEntityTypeHandle(),
                Ids = GetComponentTypeHandle<UnitId>(true),
                Transforms = GetComponentTypeHandle<LocalTransform>(true)
            }.Schedule(_recipients, cleared);
            _bufferDependency = new AssignSlotsJob
            {
                Units = _units,
                Targets = targets,
                Progress = progress,
                Arrival = arrival,
                Destination = _destination,
                Spacing = _spacing
            }.Schedule(gathered);
            Dependency = _bufferDependency;
        }

        [BurstCompile]
        private struct ClearUnitsJob : IJob
        {
            public NativeList<FormationSlotAssignment.Unit> Units;

            public void Execute() => Units.Clear();
        }

        [BurstCompile]
        private struct GatherSelectedUnitsJob : IJobChunk
        {
            public NativeList<FormationSlotAssignment.Unit> Units;
            [ReadOnly] public EntityTypeHandle Entities;
            [ReadOnly] public ComponentTypeHandle<UnitId> Ids;
            [ReadOnly] public ComponentTypeHandle<LocalTransform> Transforms;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                var entities = chunk.GetNativeArray(Entities);
                var ids = chunk.GetNativeArray(ref Ids);
                var transforms = chunk.GetNativeArray(ref Transforms);
                var recipients = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (recipients.NextEntityIndex(out int index))
                {
                    Units.Add(new FormationSlotAssignment.Unit
                    {
                        Entity = entities[index],
                        Id = ids[index].Value,
                        Position = transforms[index].Position.xz
                    });
                }
            }
        }

        [BurstCompile]
        private struct AssignSlotsJob : IJob
        {
            public NativeList<FormationSlotAssignment.Unit> Units;
            public ComponentLookup<UnitMoveTarget> Targets;
            public ComponentLookup<UnitMovementProgress> Progress;
            public ComponentLookup<UnitArrivalState> Arrival;
            public float2 Destination;
            public float Spacing;

            public void Execute()
            {
                if (Units.Length == 0)
                    return;

                var units = Units.AsArray();
                var formation = FormationSlotAssignment.Sort(units, Destination, Spacing);
                for (int i = 0; i < units.Length; i++)
                {
                    Entity entity = units[i].Entity;
                    float2 slot = formation.GetSlot(i);
                    Targets[entity] = new UnitMoveTarget { DestinationXZ = slot };
                    Targets.SetComponentEnabled(entity, true);
                    if (Arrival.TryGetComponent(entity, out var arrival))
                    {
                        uint next = arrival.CommandVersion + 1;
                        Arrival[entity] = new UnitArrivalState
                        {
                            OriginalSlot = slot,
                            FormationForward = formation.Forward,
                            Spacing = math.isfinite(Spacing) ? math.max(Spacing, RectangularFormation.MinimumSpacing) :
                                RectangularFormation.DefaultSpacing,
                            CommandVersion = next == 0 ? 1 : next
                        };
                    }
                    if (Progress.TryGetComponent(entity, out var progress))
                    {
                        progress.CommandVersion++;
                        progress.Reset(progress.Epoch);
                        Progress[entity] = progress;
                    }
                }
            }
        }
    }
}
