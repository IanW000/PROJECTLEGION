using System;
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
        private static readonly ProfilerMarker BuildMarker = new ProfilerMarker("RTS.SpatialGrid.Build");

        private EntityQuery _units;
        private NativeParallelMultiHashMap<int2, SpatialGridEntry> _entries;
        private JobHandle _build;
        private JobHandle _readers;
        private int _capacity = SpatialGridBuild.InitialCapacity;
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
        internal float SnapshotCellSize => _snapshotCellSize;

        internal void Invalidate()
        {
            JobHandle.CombineDependencies(_build, _readers).Complete();
            _entries.Clear();
            _build = _readers = default;
            _snapshotVersion++;
        }

        protected override void OnCreate()
        {
            _units = GetEntityQuery(ComponentType.ReadOnly<UnitTag>(), ComponentType.ReadOnly<LocalTransform>());
            _entries = new NativeParallelMultiHashMap<int2, SpatialGridEntry>(SpatialGridBuild.InitialCapacity, Allocator.Persistent);
        }

        protected override void OnUpdate()
        {
            JobHandle previousUse = JobHandle.CombineDependencies(_build, _readers);
            _snapshotCellSize = _requestedCellSize;
            _build = SpatialGridBuild.Schedule(_units, GetEntityTypeHandle(), GetComponentTypeHandle<LocalTransform>(true),
                _snapshotCellSize, BuildMarker, ref _entries, ref _capacity, previousUse, Dependency);
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

    }
}
