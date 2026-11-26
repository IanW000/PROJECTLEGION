using System;
using System.Collections.Generic;
using Unity.Mathematics;

namespace ProjectLegion.MovementBenchmark
{
    internal readonly struct BenchmarkWorkload
    {
        internal const int Warmup = 300;
        internal const int Samples = 1000;
        internal const int Repetitions = 3;
        internal const float StepSeconds = 1f / 60f;
        // One float ULP at |position| = 100. A changed reflection still fails the exact velocity check.
        internal const float PositionTolerance = 0.00000762939453125f;
        private static readonly int[] Counts = { 100, 500, 1000, 2500, 5000, 10000 };

        internal BenchmarkWorkload(BenchmarkMode mode, int units, int repetition)
        {
            Mode = mode;
            Units = units;
            Repetition = repetition;
        }

        internal BenchmarkMode Mode { get; }
        internal int Units { get; }
        internal int Repetition { get; }
        internal bool IsBaseline => Units == 0;

        internal BenchmarkConfig Config(bool smoke)
        {
            return new BenchmarkConfig(Mode, IsBaseline ? Counts[0] : Units, stepSeconds: StepSeconds,
                warmupSteps: smoke ? 2 : Warmup, sampleSteps: smoke ? 6 : Samples);
        }

        internal static List<BenchmarkWorkload> Plan(bool suite, BenchmarkMode mode, int units,
            int repetitions, int orderSeed)
        {
            if (repetitions < 1 || repetitions > 100)
                throw new ArgumentOutOfRangeException(nameof(repetitions));
            if (!suite)
                _ = new BenchmarkConfig(mode, units);

            var result = new List<BenchmarkWorkload>();
            if (suite)
            {
                for (int i = 0; i <= (int)BenchmarkMode.EcsParallelBurst; i++)
                    result.Add(new BenchmarkWorkload((BenchmarkMode)i, 0, 0));
            }
            else
            {
                result.Add(new BenchmarkWorkload(mode, 0, 0));
            }

            // Use a fixed PRNG implementation so ordering survives runtime upgrades.
            var order = new Unity.Mathematics.Random(orderSeed == 0 ? uint.MaxValue : unchecked((uint)orderSeed));
            for (int repetition = 1; repetition <= repetitions; repetition++)
            {
                int start = result.Count;
                if (suite)
                {
                    for (int i = 0; i <= (int)BenchmarkMode.EcsParallelBurst; i++)
                    foreach (int count in Counts)
                        result.Add(new BenchmarkWorkload((BenchmarkMode)i, count, repetition));
                }
                else
                {
                    result.Add(new BenchmarkWorkload(mode, units, repetition));
                }

                for (int index = result.Count - 1; index > start; index--)
                {
                    int swap = order.NextInt(start, index + 1);
                    BenchmarkWorkload previous = result[index];
                    result[index] = result[swap];
                    result[swap] = previous;
                }
            }
            return result;
        }

        internal static void Validate(BenchmarkConfig config, ReadOnlySpan<UnitState> actual)
        {
            if (actual.Length != config.UnitCount)
                throw new InvalidOperationException("The measured run has an unexpected final state count.");

            var expected = new UnitState[config.UnitCount];
            UnitInitialization.Fill(config, expected);
            for (int index = 0; index < expected.Length; index++)
            {
                UnitState state = expected[index];
                for (int step = 0; step < config.WarmupSteps + config.SampleSteps; step++)
                    UnitMovement.Advance(ref state, config.MinXZ, config.MaxXZ, config.StepSeconds);

                if (!math.all(math.abs(state.PositionXZ - actual[index].PositionXZ) <= PositionTolerance) ||
                    !math.all(state.VelocityXZ == actual[index].VelocityXZ))
                    throw new InvalidOperationException($"Final state differs from the managed reference at unit {index}.");
            }
        }
    }
}
