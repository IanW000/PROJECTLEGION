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
    internal struct OverlapResolutionSettings
    {
        [SerializeField] internal bool EnableOverlapResolution;
        [SerializeField, Range(0f, 1f)] internal float CorrectionStrength;
        [SerializeField, Min(0f)] internal float MaxCorrectionSpeed;

        internal static OverlapResolutionSettings Default => new OverlapResolutionSettings
        {
            EnableOverlapResolution = true,
            CorrectionStrength = 0.5f,
            MaxCorrectionSpeed = 3f
        };

        internal OverlapResolutionSettings Validated()
        {
            var settings = this;
            settings.CorrectionStrength = math.isfinite(CorrectionStrength)
                ? math.saturate(CorrectionStrength) : Default.CorrectionStrength;
            settings.MaxCorrectionSpeed = math.isfinite(MaxCorrectionSpeed)
                ? math.max(0f, MaxCorrectionSpeed) : Default.MaxCorrectionSpeed;
            return settings;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(UnitMovementSystem))]
    [UpdateBefore(typeof(TransformSystemGroup))]
    internal partial class UnitOverlapResolutionSystem : SystemBase
    {
        private const float ReferenceStepsPerSecond = 60f;
        private static readonly ProfilerMarker BuildMarker = new ProfilerMarker("RTS.Overlap.Build");
        private OverlapResolutionSettings _settings = OverlapResolutionSettings.Default;
        private EntityQuery _units;
        private EntityQuery _correctable;
        private EntityQueryMask _correctableMask;
        private NativeParallelMultiHashMap<int2, SpatialGridEntry> _entries;
        private int _capacity = SpatialGridBuild.InitialCapacity;
        private JobHandle _readers;

        internal void Configure(OverlapResolutionSettings settings) => _settings = settings.Validated();

        protected override void OnCreate()
        {
            _units = GetEntityQuery(ComponentType.ReadOnly<UnitTag>(), ComponentType.ReadOnly<LocalTransform>());
            _correctable = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<UnitTag, LocalTransform, UnitStats, UnitMoveTarget>()
                .WithAllRW<UnitOverlapCorrection>().Build(this);
            _correctableMask = _correctable.GetEntityQueryMask();
        }

        protected override void OnUpdate()
        {
            float deltaTime = SystemAPI.Time.DeltaTime;
            if (!_settings.EnableOverlapResolution || !math.isfinite(deltaTime) || deltaTime <= 0f ||
                _settings.CorrectionStrength <= 0f || _settings.MaxCorrectionSpeed <= 0f ||
                _correctable.IsEmptyIgnoreFilter)
                return;

            if (!_entries.IsCreated)
                _entries = new NativeParallelMultiHashMap<int2, SpatialGridEntry>(_capacity, Allocator.Persistent);

            var movement = World.GetExistingSystemManaged<UnitMovementSystem>();
            var preMovementGrid = World.GetExistingSystemManaged<UnitSpatialGridSystem>();
            float cellSize = preMovementGrid?.SnapshotCellSize ?? UnitSpatialGridSystem.DefaultCellSize;
            float radius = movement?.CollisionRadius ?? LocalAvoidanceSettings.Default.CollisionRadius;
            var entities = GetEntityTypeHandle();
            var transforms = GetComponentTypeHandle<LocalTransform>(true);
            var targets = GetComponentLookup<UnitMoveTarget>(true);
            var stats = GetComponentLookup<UnitStats>(true);
            var avoidance = GetComponentLookup<UnitAvoidanceVelocity>(true);
            var results = GetComponentTypeHandle<UnitOverlapCorrection>();
            var build = SpatialGridBuild.Schedule(_units, entities, transforms, cellSize,
                BuildMarker, ref _entries, ref _capacity, _readers, Dependency);

            // Strength is specified as the fraction removed in one 60 Hz step.
            float stepStrength = 1f - math.pow(1f - _settings.CorrectionStrength, deltaTime * ReferenceStepsPerSecond);
            _readers = new CalculateCorrectionJob
            {
                Grid = new SpatialGrid(_entries.AsReadOnly(), cellSize),
                Entities = entities,
                Transforms = transforms,
                Correctable = _correctableMask,
                Targets = targets,
                Stats = stats,
                Avoidance = avoidance,
                Results = results,
                HonorHold = movement is { Enabled: true, CalculatedAvoidance: true },
                Diameter = 2f * radius,
                Strength = stepStrength,
                MaxDistance = _settings.MaxCorrectionSpeed * deltaTime
            }.ScheduleParallel(_correctable, build);

            Dependency = new ApplyCorrectionJob
            {
                Transforms = GetComponentTypeHandle<LocalTransform>(),
                Corrections = GetComponentTypeHandle<UnitOverlapCorrection>(true)
            }.ScheduleParallel(_correctable, _readers);
        }

        protected override void OnDestroy()
        {
            _readers.Complete();
            if (_entries.IsCreated)
                _entries.Dispose();
        }

        [BurstCompile]
        private struct CalculateCorrectionJob : IJobChunk
        {
            private static readonly ProfilerMarker Marker = new ProfilerMarker("RTS.Overlap.Calculate");
            internal SpatialGrid Grid;
            [ReadOnly] internal EntityTypeHandle Entities;
            [ReadOnly] internal ComponentTypeHandle<LocalTransform> Transforms;
            [ReadOnly] internal ComponentLookup<UnitMoveTarget> Targets;
            [ReadOnly] internal ComponentLookup<UnitStats> Stats;
            [ReadOnly] internal ComponentLookup<UnitAvoidanceVelocity> Avoidance;
            internal EntityQueryMask Correctable;
            internal ComponentTypeHandle<UnitOverlapCorrection> Results;
            internal bool HonorHold;
            internal float Diameter;
            internal float Strength;
            internal float MaxDistance;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                using var scope = Marker.Auto();
                var entities = chunk.GetNativeArray(Entities);
                var transforms = chunk.GetNativeArray(ref Transforms);
                var corrections = chunk.GetNativeArray(ref Results);
                var units = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (units.NextEntityIndex(out int index))
                {
                    Entity entity = entities[index];
                    corrections[index] = new UnitOverlapCorrection
                    {
                        Displacement = CanMove(entity) ? Calculate(entity, transforms[index].Position.xz) : float2.zero
                    };
                }
            }

            private bool CanMove(Entity entity)
            {
                return Correctable.MatchesIgnoreFilter(entity) && Targets.IsComponentEnabled(entity) &&
                    Stats.TryGetComponent(entity, out var stats) &&
                    math.isfinite(stats.MovementSpeed) && stats.MovementSpeed > 0f &&
                    (!HonorHold || !Avoidance.TryGetComponent(entity, out var plan) || plan.Mode != AvoidanceMove.Hold);
            }

            private float2 Calculate(Entity entity, float2 position)
            {
                if (!Grid.TryQuery(position, Diameter, entity, out var neighbors))
                    return float2.zero;

                float2 sum = float2.zero;
                int contacts = 0;
                while (neighbors.MoveNext(out var neighbor))
                {
                    float2 offset = position - neighbor.PositionXZ;
                    float distance = math.length(offset);
                    float penetration = Diameter - distance;
                    if (penetration <= 0f)
                        continue;

                    float2 away = distance > 0f ? offset / distance : LocalAvoidance.CoincidentAxis(entity, neighbor.Entity);
                    sum += away * (penetration * (CanMove(neighbor.Entity) ? 0.5f : 1f));
                    contacts++;
                }

                return contacts > 0 ? LocalAvoidance.ClampLength(sum * (Strength / contacts), MaxDistance) : float2.zero;
            }
        }

        [BurstCompile]
        private struct ApplyCorrectionJob : IJobChunk
        {
            private static readonly ProfilerMarker Marker = new ProfilerMarker("RTS.Overlap.Apply");
            internal ComponentTypeHandle<LocalTransform> Transforms;
            [ReadOnly] internal ComponentTypeHandle<UnitOverlapCorrection> Corrections;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                using var scope = Marker.Auto();
                var transforms = chunk.GetNativeArray(ref Transforms);
                var corrections = chunk.GetNativeArray(ref Corrections);
                var units = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (units.NextEntityIndex(out int index))
                {
                    float2 offset = corrections[index].Displacement;
                    if (math.all(offset == float2.zero))
                        continue;
                    var transform = transforms[index];
                    transform.Position.xz += offset;
                    transforms[index] = transform;
                }
            }
        }
    }
}
