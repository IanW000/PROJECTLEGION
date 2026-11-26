using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Entities;
using Unity.Mathematics;

namespace ProjectLegion.MovementBenchmark
{
    internal struct MovementChunkData
    {
        internal const int ManagedExecution = 1;
        internal const int BurstExecution = 2;

        public ComponentTypeHandle<MovementState> StateHandle;
        public ComponentTypeHandle<MovementExecutionProbe> ProbeHandle;
        public bool RecordExecution;
        public float2 MinXZ;
        public float2 MaxXZ;
        public float StepSeconds;

        public void Advance(in ArchetypeChunk chunk, bool useEnabledMask, in v128 enabledMask)
        {
            var states = chunk.GetNativeArray(ref StateHandle);
            var entities = new ChunkEntityEnumerator(useEnabledMask, enabledMask, chunk.Count);
            while (entities.NextEntityIndex(out int index))
            {
                MovementState state = states[index];
                UnitMovement.Advance(ref state.Value, MinXZ, MaxXZ, StepSeconds);
                states[index] = state;
            }

            if (RecordExecution)
            {
                int backend = BurstExecution;
                MarkManagedExecution(ref backend);
                var probes = chunk.GetNativeArray(ref ProbeHandle);
                entities = new ChunkEntityEnumerator(useEnabledMask, enabledMask, chunk.Count);
                while (entities.NextEntityIndex(out int index))
                    probes[index] = new MovementExecutionProbe { Backend = probes[index].Backend | backend };
            }
        }

        [BurstDiscard]
        private static void MarkManagedExecution(ref int backend)
        {
            backend = ManagedExecution;
        }
    }

    internal struct ManagedMovementChunkJob : IJobChunk
    {
        public MovementChunkData Data;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask,
            in v128 chunkEnabledMask)
        {
            Data.Advance(chunk, useEnabledMask, chunkEnabledMask);
        }
    }

    [BurstCompile(CompileSynchronously = true, FloatMode = FloatMode.Strict)]
    internal struct BurstMovementChunkJob : IJobChunk
    {
        public MovementChunkData Data;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask,
            in v128 chunkEnabledMask)
        {
            Data.Advance(chunk, useEnabledMask, chunkEnabledMask);
        }
    }
}
