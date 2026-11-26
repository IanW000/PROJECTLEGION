using System;
using Unity.Mathematics;

namespace ProjectLegion.MovementBenchmark
{
    public enum BenchmarkMode
    {
        MonoBehaviour,
        EcsSequential,
        EcsSequentialBurst,
        EcsParallel,
        EcsParallelBurst
    }

    public sealed class BenchmarkConfig
    {
        private const double DiagonalAxisFraction = 0.70710678;

        public BenchmarkMode Mode { get; }
        public int UnitCount { get; }
        public float2 MinXZ { get; }
        public float2 MaxXZ { get; }
        public float MinSpeed { get; }
        public float MaxSpeed { get; }
        public uint Seed { get; }
        public float StepSeconds { get; }
        public int WarmupSteps { get; }
        public int SampleSteps { get; }

        public BenchmarkConfig(
            BenchmarkMode mode = BenchmarkMode.MonoBehaviour,
            int unitCount = 1000,
            float2? minXZ = null,
            float2? maxXZ = null,
            float minSpeed = 2f,
            float maxSpeed = 8f,
            uint seed = 12345u,
            float stepSeconds = 1f / 60f,
            int warmupSteps = 300,
            int sampleSteps = 1000)
        {
            if ((uint)mode > (uint)BenchmarkMode.EcsParallelBurst)
                throw new ArgumentOutOfRangeException(nameof(mode));
            if (!IsSupportedUnitCount(unitCount))
                throw new ArgumentOutOfRangeException(nameof(unitCount));
            if (!math.isfinite(minSpeed) || minSpeed <= 0f)
                throw new ArgumentOutOfRangeException(nameof(minSpeed));
            if (!math.isfinite(maxSpeed) || maxSpeed < minSpeed)
                throw new ArgumentOutOfRangeException(nameof(maxSpeed));
            if (seed == 0u)
                throw new ArgumentOutOfRangeException(nameof(seed), "The Mathematics random generator requires a nonzero seed.");
            if (!math.isfinite(stepSeconds) || stepSeconds <= 0f)
                throw new ArgumentOutOfRangeException(nameof(stepSeconds));
            if (warmupSteps < 0)
                throw new ArgumentOutOfRangeException(nameof(warmupSteps));
            if (sampleSteps <= 0 || sampleSteps > int.MaxValue - warmupSteps)
                throw new ArgumentOutOfRangeException(nameof(sampleSteps));

            float2 min = minXZ ?? new float2(-100f);
            float2 max = maxXZ ?? new float2(100f);
            if (!math.all(math.isfinite(min)))
                throw new ArgumentOutOfRangeException(nameof(minXZ));
            float2 extent = max - min;
            if (!math.all(math.isfinite(max)) || !math.all(math.isfinite(extent)) || !math.all(extent > 0f))
                throw new ArgumentOutOfRangeException(nameof(maxXZ), "Both axes must have positive, finite extents.");

            float maxDisplacement = maxSpeed * stepSeconds;
            // Even an axis-aligned unit must travel less than the width of either axis in one step.
            if (maxDisplacement <= 0f || !math.all(extent > maxDisplacement) ||
                !math.all(math.isfinite(min - maxDisplacement)) || !math.all(math.isfinite(max + maxDisplacement)))
            {
                throw new ArgumentOutOfRangeException(nameof(stepSeconds),
                    "A step must have a positive finite displacement less than both extents, with finite endpoints.");
            }

            // At least one axis carries this much of a unit's displacement, even at the minimum speed.
            // Either axis can be the dominant one, so check both against their coarsest float spacing.
            double minDominantDisplacement = (double)minSpeed * stepSeconds * DiagonalAxisFraction;
            float2 largestMagnitude = math.max(math.abs(min), math.abs(max));
            if (minDominantDisplacement <= FloatSpacing(largestMagnitude.x) ||
                minDominantDisplacement <= FloatSpacing(largestMagnitude.y))
            {
                throw new ArgumentOutOfRangeException(nameof(stepSeconds),
                    "The minimum dominant-axis displacement must exceed float spacing at the arena bounds.");
            }

            Mode = mode;
            UnitCount = unitCount;
            MinXZ = min;
            MaxXZ = max;
            MinSpeed = minSpeed;
            MaxSpeed = maxSpeed;
            Seed = seed;
            StepSeconds = stepSeconds;
            WarmupSteps = warmupSteps;
            SampleSteps = sampleSteps;
        }

        public static bool IsSupportedUnitCount(int unitCount)
        {
            switch (unitCount)
            {
                case 100:
                case 500:
                case 1000:
                case 2500:
                case 5000:
                case 10000:
                    return true;
                default:
                    return false;
            }
        }

        private static float FloatSpacing(float magnitude)
        {
            // For a finite nonnegative float, incrementing its bits gives the next representable value.
            return math.asfloat(math.asuint(magnitude) + 1u) - magnitude;
        }
    }
}
