using System;
using NUnit.Framework;
using Unity.Burst;
using Unity.Entities;
using Unity.Mathematics;

namespace ProjectLegion.MovementBenchmark.Tests
{
    internal struct MovementTestEnabled : IComponentData, IEnableableComponent
    {
    }

    public class EcsMovementWorldTests
    {
        [Test]
        public void ChunkKernelSkipsDisabledRows()
        {
            using var world = new World("Movement mask test");
            EntityManager manager = world.EntityManager;
            EntityArchetype archetype = manager.CreateArchetype(typeof(MovementState), typeof(MovementTestEnabled));
            Entity enabled = manager.CreateEntity(archetype);
            Entity disabled = manager.CreateEntity(archetype);
            var state = new MovementState { Value = new UnitState { VelocityXZ = new float2(1f) } };
            manager.SetComponentData(enabled, state);
            manager.SetComponentData(disabled, state);
            manager.SetComponentEnabled<MovementTestEnabled>(disabled, false);
            using var query = manager.CreateEntityQuery(ComponentType.ReadWrite<MovementState>(),
                ComponentType.ReadOnly<MovementTestEnabled>());
            var job = new ManagedMovementChunkJob
            {
                Data = new MovementChunkData
                {
                    StateHandle = manager.GetComponentTypeHandle<MovementState>(isReadOnly: false),
                    ProbeHandle = manager.GetComponentTypeHandle<MovementExecutionProbe>(isReadOnly: false),
                    MinXZ = new float2(-2f),
                    MaxXZ = new float2(2f),
                    StepSeconds = 0.5f
                }
            };
            job.Run(query);

            Assert.That(manager.GetComponentData<MovementState>(enabled).Value.PositionXZ, Is.EqualTo(new float2(0.5f)));
            Assert.That(manager.GetComponentData<MovementState>(disabled).Value.PositionXZ, Is.EqualTo(float2.zero));
        }

        [TestCase(BenchmarkMode.EcsSequential)]
        [TestCase(BenchmarkMode.EcsSequentialBurst)]
        [TestCase(BenchmarkMode.EcsParallel)]
        [TestCase(BenchmarkMode.EcsParallelBurst)]
        public void EmptyQueryCanStep(BenchmarkMode mode)
        {
            using var world = new EcsMovementWorld(new BenchmarkConfig(mode), System.Array.Empty<UnitState>());
            Assert.That(world.ChunkCount, Is.Zero);
            world.StartStep();
            world.CompleteStep();
            world.CopyStates(System.Span<UnitState>.Empty);
        }

        [TestCase(BenchmarkMode.EcsSequential, 1, false)]
        [TestCase(BenchmarkMode.EcsSequentialBurst, 1, true)]
        [TestCase(BenchmarkMode.EcsParallel, 1, false)]
        [TestCase(BenchmarkMode.EcsParallelBurst, 1, true)]
        [TestCase(BenchmarkMode.EcsSequential, 10000, false)]
        [TestCase(BenchmarkMode.EcsSequentialBurst, 10000, true)]
        [TestCase(BenchmarkMode.EcsParallel, 10000, false)]
        [TestCase(BenchmarkMode.EcsParallelBurst, 10000, true)]
        public void VerifiesInitializationMovementAndExecutionBackend(BenchmarkMode mode, int count, bool burst)
        {
            if (burst)
                Assert.That(BurstCompiler.IsEnabled, Is.True, "Enable Burst Compilation in the Editor to verify Burst.");

            var config = new BenchmarkConfig(mode, unitCount: 10000);
            var initial = new UnitState[config.UnitCount];
            var repeat = new UnitState[config.UnitCount];
            UnitInitialization.Fill(config, initial);
            UnitInitialization.Fill(config, repeat);
            var actual = new UnitState[count];
            using var world = new EcsMovementWorld(config, initial.AsSpan(0, count), recordExecution: true);
            if (count > 1)
                Assert.That(world.ChunkCount, Is.GreaterThan(1));
            world.CopyStates(actual);

            for (int index = 0; index < count; index++)
            {
                Assert.That(math.asuint(actual[index].PositionXZ), Is.EqualTo(math.asuint(repeat[index].PositionXZ)));
                Assert.That(math.asuint(actual[index].VelocityXZ), Is.EqualTo(math.asuint(repeat[index].VelocityXZ)));
            }

            const int steps = 8;
            for (int step = 0; step < steps; step++)
            {
                world.StartStep();
                world.CompleteStep();
                world.VerifyExecution(burst ? MovementChunkData.BurstExecution : MovementChunkData.ManagedExecution);
            }
            world.CopyStates(actual);

            for (int index = 0; index < count; index++)
            {
                UnitState expected = initial[index];
                for (int step = 0; step < steps; step++)
                    UnitMovement.Advance(ref expected, config.MinXZ, config.MaxXZ, config.StepSeconds);

                // Eight steps at |position| <= 100. Allow one float ULP (~7.63e-6) per
                // step plus margin for the extra arithmetic at a reflection.
                Assert.That(math.cmax(math.abs(actual[index].PositionXZ - expected.PositionXZ)), Is.LessThan(0.00008f));
                Assert.That(actual[index].VelocityXZ, Is.EqualTo(expected.VelocityXZ));
            }
        }
    }
}
