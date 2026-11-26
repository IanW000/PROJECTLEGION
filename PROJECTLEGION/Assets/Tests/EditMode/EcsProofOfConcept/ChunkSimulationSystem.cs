using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;

namespace ProjectLegion.EcsProofOfConcept
{
    internal struct SimulationState : IComponentData
    {
        public int Value;
        public int ExecutionMode;
    }

    internal struct SimulationStep : IComponentData
    {
        public int Value;
    }

    [BurstCompile(CompileSynchronously = true)]
    internal struct AdvanceChunkJob : IJobChunk
    {
        public const int ManagedExecution = 1;
        public const int BurstExecution = 2;

        public ComponentTypeHandle<SimulationState> StateHandle;
        [ReadOnly] public ComponentTypeHandle<SimulationStep> StepHandle;

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask,
            in v128 chunkEnabledMask)
        {
            int executionMode = BurstExecution;
            MarkManagedExecution(ref executionMode);

            // These arrays are views into ECS-owned chunk memory, valid for this Execute call.
            var states = chunk.GetNativeArray(ref StateHandle);
            var steps = chunk.GetNativeArray(ref StepHandle);
            var entities = new ChunkEntityEnumerator(useEnabledMask, chunkEnabledMask, chunk.Count);
            while (entities.NextEntityIndex(out int index))
            {
                // Parallel invocations own different chunks and write disjoint component rows.
                var state = states[index];
                state.Value += steps[index].Value;
                // Preserve both observations so a mix of managed and Burst execution fails the test.
                state.ExecutionMode |= executionMode;
                states[index] = state;
            }
        }

        // Burst removes this call. A managed fallback leaves a different value in the component.
        [BurstDiscard]
        private static void MarkManagedExecution(ref int executionMode)
        {
            executionMode = ManagedExecution;
        }
    }

    [DisableAutoCreation]
    internal partial class ChunkSimulationSystem : SystemBase
    {
        internal const int StepsPerUpdate = 2;

        private EntityQuery _query;
        private ComponentTypeHandle<SimulationState> _stateHandle;
        private ComponentTypeHandle<SimulationStep> _stepHandle;

        public bool RunInParallel { get; set; }
        public int MatchingEntityCount => _query.CalculateEntityCount();
        public int MatchingChunkCount => _query.CalculateChunkCount();

        protected override void OnCreate()
        {
            // The system owns this cached query and releases it during World disposal.
            _query = GetEntityQuery(ComponentType.ReadWrite<SimulationState>(),
                ComponentType.ReadOnly<SimulationStep>());
            _stateHandle = GetComponentTypeHandle<SimulationState>(isReadOnly: false);
            _stepHandle = GetComponentTypeHandle<SimulationStep>(isReadOnly: true);
        }

        protected override void OnUpdate()
        {
            // Refresh cached handles for the current system version before submitting any work.
            _stateHandle.Update(this);
            _stepHandle.Update(this);
            var job = new AdvanceChunkJob { StateHandle = _stateHandle, StepHandle = _stepHandle };

            if (!RunInParallel)
                CompleteDependency();

            // Keep both steps in one OnUpdate. SystemBase waits for its previous update's jobs
            // before entering a new OnUpdate, which would hide the dependency chain in this test.
            for (int step = 0; step < StepsPerUpdate; step++)
            {
                if (RunInParallel)
                    Dependency = job.ScheduleParallel(_query, Dependency);
                else
                    job.Run(_query);
            }
        }

        public void CompleteSimulation()
        {
            CompleteDependency();
        }
    }
}
