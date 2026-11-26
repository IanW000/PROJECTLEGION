using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;

namespace ProjectLegion.EcsProofOfConcept
{
    public class ChunkSimulationTests
    {
        private const int MultiChunkEntityCount = 4096;
        private const int ExcludedValue = -42;

        [TestCase(1, false, false, TestName = "B_Run_Managed_SingleEntity")]
        [TestCase(MultiChunkEntityCount, false, false, TestName = "B_Run_Managed_MultipleChunks")]
        [TestCase(1, false, true, TestName = "C_Run_Burst_SingleEntity")]
        [TestCase(MultiChunkEntityCount, false, true, TestName = "C_Run_Burst_MultipleChunks")]
        [TestCase(1, true, false, TestName = "D_ScheduleParallel_Managed_SingleEntity")]
        [TestCase(MultiChunkEntityCount, true, false, TestName = "D_ScheduleParallel_Managed_MultipleChunks")]
        [TestCase(1, true, true, TestName = "E_ScheduleParallel_Burst_SingleEntity")]
        [TestCase(MultiChunkEntityCount, true, true, TestName = "E_ScheduleParallel_Burst_MultipleChunks")]
        public void AdvancesMatchingEntities(int entityCount, bool runInParallel, bool enableBurst)
        {
            // EditMode runs these cases serially. Restore the Editor-wide option after all jobs finish.
            var options = BurstCompiler.Options;
            bool originalBurstSetting = options.EnableBurstCompilation;
            try
            {
                options.EnableBurstCompilation = enableBurst;
                Assert.That(BurstCompiler.IsEnabled, Is.EqualTo(enableBurst),
                    "The Editor must honor this test's Burst setting. Check for a forced command-line setting.");

                VerifySimulation(entityCount, runInParallel,
                    enableBurst ? AdvanceChunkJob.BurstExecution : AdvanceChunkJob.ManagedExecution);
            }
            finally
            {
                options.EnableBurstCompilation = originalBurstSetting;
            }
        }

        [TestCase(false, TestName = "EmptyQuery_Run")]
        [TestCase(true, TestName = "EmptyQuery_ScheduleParallel")]
        public void EmptyQueryLeavesExcludedEntityUnchanged(bool runInParallel)
        {
            VerifySimulation(0, runInParallel, expectedExecutionMode: 0);
        }

        private static void VerifySimulation(int entityCount, bool runInParallel, int expectedExecutionMode)
        {
            // The World owns the entities and system. This array only owns the temporary list of handles.
            using var world = new World("ECS Proof of Concept");
            using var entities = new NativeArray<Entity>(entityCount, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            EntityManager manager = world.EntityManager;
            EntityArchetype archetype = manager.CreateArchetype(typeof(SimulationState), typeof(SimulationStep));
            if (entityCount > 0)
                manager.CreateEntity(archetype, entities);

            for (int i = 0; i < entities.Length; i++)
            {
                manager.SetComponentData(entities[i], new SimulationState { Value = 3 * i });
                manager.SetComponentData(entities[i], new SimulationStep { Value = i % 7 + 1 });
            }

            Entity excluded = manager.CreateEntity(typeof(SimulationState));
            manager.SetComponentData(excluded, new SimulationState { Value = ExcludedValue });

            var system = world.GetOrCreateSystemManaged<ChunkSimulationSystem>();
            system.RunInParallel = runInParallel;
            Assert.That(system.MatchingEntityCount, Is.EqualTo(entityCount));
            if (entityCount == MultiChunkEntityCount)
                Assert.That(system.MatchingChunkCount, Is.GreaterThanOrEqualTo(2));

            try
            {
                system.Update();
            }
            finally
            {
                // Synchronize at the readback boundary, after both steps have been submitted.
                system.CompleteSimulation();
            }

            for (int i = 0; i < entities.Length; i++)
            {
                var state = manager.GetComponentData<SimulationState>(entities[i]);
                Assert.That(state.Value, Is.EqualTo(3 * i + 2 * (i % 7 + 1)), $"Entity {i} value");
                Assert.That(state.ExecutionMode, Is.EqualTo(expectedExecutionMode), $"Entity {i} execution mode");
            }

            var excludedState = manager.GetComponentData<SimulationState>(excluded);
            Assert.That(excludedState.Value, Is.EqualTo(ExcludedValue));
            Assert.That(excludedState.ExecutionMode, Is.Zero);
        }
    }
}
