using System;
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
    [UpdateAfter(typeof(UnitSpawnSystem))]
    [UpdateBefore(typeof(UnitMovementSystem))]
    internal partial class UnitSpatialGridSystem : SystemBase
    {
        // Match the current 0.9 formation spacing. The unit hull spans 0.44 by 0.54 on XZ.
        internal const float DefaultCellSize = 0.9f;
        private const int InitialCapacity = 1024;

        private EntityQuery _units;
        private NativeParallelMultiHashMap<int2, SpatialGridEntry> _entries;
        private JobHandle _build;
        private JobHandle _readers;
        private int _capacity = InitialCapacity;
        private float _requestedCellSize = DefaultCellSize;
        private float _snapshotCellSize = DefaultCellSize;
        private ulong _snapshotVersion;

        // Configuration is main-thread only. The current snapshot retains its original cell size.
        internal void SetCellSize(float cellSize)
        {
            if (!(cellSize > 0f) || !math.isfinite(cellSize))
                throw new ArgumentOutOfRangeException(nameof(cellSize));

            _requestedCellSize = cellSize;
        }

        internal readonly struct Snapshot
        {
            internal readonly SpatialGrid Grid;
            internal readonly JobHandle Build;
            private readonly UnitSpatialGridSystem _owner;
            private readonly ulong _version;

            internal Snapshot(UnitSpatialGridSystem owner)
            {
                Grid = new SpatialGrid(owner._entries.AsReadOnly(), owner._snapshotCellSize);
                Build = owner._build;
                _owner = owner;
                _version = owner._snapshotVersion;
            }

            // On the main thread, include Build in the reader's input dependency and register its
            // final handle before returning from the consumer update. Never retain the view across
            // a grid update or transfer ownership of its container.
            internal void RegisterReader(JobHandle reader)
            {
                if (_owner == null || !_owner._entries.IsCreated || _owner._snapshotVersion != _version)
                    throw new InvalidOperationException("Register spatial grid readers on the current snapshot.");

                _owner._readers = JobHandle.CombineDependencies(_owner._readers, reader);
            }
        }

        internal Snapshot GetSnapshot() => new Snapshot(this);

        protected override void OnCreate()
        {
            _units = GetEntityQuery(ComponentType.ReadOnly<UnitTag>(), ComponentType.ReadOnly<LocalTransform>());
            _entries = new NativeParallelMultiHashMap<int2, SpatialGridEntry>(InitialCapacity, Allocator.Persistent);
        }

        protected override void OnUpdate()
        {
            JobHandle previousUse = JobHandle.CombineDependencies(_build, _readers);
            int count = _units.CalculateEntityCount();
            if (count > _capacity)
            {
                previousUse.Complete();
                int capacity = (int)Math.Max(count, Math.Min(int.MaxValue, 2L * _capacity));
                _entries.Capacity = capacity;
                _capacity = capacity;
            }

            JobHandle cleared = new ClearSpatialGridJob { Entries = _entries }.Schedule(previousUse);
            _snapshotCellSize = _requestedCellSize;
            _build = new BuildSpatialGridJob
            {
                Entries = _entries.AsParallelWriter(),
                Entities = GetEntityTypeHandle(),
                Transforms = GetComponentTypeHandle<LocalTransform>(true),
                CellSize = _snapshotCellSize
            }.ScheduleParallel(_units, JobHandle.CombineDependencies(cleared, Dependency));
            Dependency = _build;
            _readers = default;
            _snapshotVersion++;
        }

        protected override void OnDestroy()
        {
            JobHandle.CombineDependencies(_build, _readers).Complete();
            if (_entries.IsCreated)
                _entries.Dispose();
        }

        [BurstCompile]
        private struct ClearSpatialGridJob : IJob
        {
            internal NativeParallelMultiHashMap<int2, SpatialGridEntry> Entries;

            public void Execute() => Entries.Clear();
        }

        [BurstCompile]
        private struct BuildSpatialGridJob : IJobChunk
        {
            private static readonly ProfilerMarker Marker = new ProfilerMarker("RTS.SpatialGrid.Build");

            internal NativeParallelMultiHashMap<int2, SpatialGridEntry>.ParallelWriter Entries;
            [ReadOnly] internal EntityTypeHandle Entities;
            [ReadOnly] internal ComponentTypeHandle<LocalTransform> Transforms;
            internal float CellSize;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask,
                in v128 chunkEnabledMask)
            {
                using var scope = Marker.Auto();
                var entities = chunk.GetNativeArray(Entities);
                var transforms = chunk.GetNativeArray(ref Transforms);
                var units = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
                while (units.NextEntityIndex(out int index))
                {
                    float2 position = transforms[index].Position.xz;
                    if (SpatialGrid.TryGetCell(position, CellSize, out int2 cell))
                        Entries.Add(cell, new SpatialGridEntry { Entity = entities[index], PositionXZ = position });
                }
            }
        }
    }
}
