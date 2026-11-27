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
    internal static class SpatialGridBuild
    {
        internal const int InitialCapacity = 1024;

        internal static JobHandle Schedule(EntityQuery units, EntityTypeHandle entities,
            ComponentTypeHandle<LocalTransform> transforms, float cellSize, ProfilerMarker marker,
            ref NativeParallelMultiHashMap<int2, SpatialGridEntry> entries, ref int capacity,
            JobHandle previousUse, JobHandle input)
        {
            int count = units.CalculateEntityCount();
            if (count > capacity)
            {
                previousUse.Complete();
                int newCapacity = (int)Math.Max(count, Math.Min(int.MaxValue, 2L * capacity));
                entries.Capacity = newCapacity;
                capacity = newCapacity;
            }

            JobHandle cleared = new ClearSpatialGridJob { Entries = entries }.Schedule(previousUse);
            return new BuildSpatialGridJob
            {
                Marker = marker,
                Entries = entries.AsParallelWriter(),
                Entities = entities,
                Transforms = transforms,
                CellSize = cellSize
            }.ScheduleParallel(units, JobHandle.CombineDependencies(cleared, input));
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
            internal ProfilerMarker Marker;

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
