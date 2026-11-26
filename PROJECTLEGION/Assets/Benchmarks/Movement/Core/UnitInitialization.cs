using System;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace ProjectLegion.MovementBenchmark
{
    public static class UnitInitialization
    {
        // The caller owns the buffer and may reuse or release it after the backend copies the state.
        public static void Fill(BenchmarkConfig config, Span<UnitState> units)
        {
            if (config == null)
                throw new ArgumentNullException(nameof(config));
            if (units.Length != config.UnitCount)
                throw new ArgumentException("The buffer length must match the configured unit count.", nameof(units));

            var random = new Random(config.Seed);
            for (int index = 0; index < units.Length; index++)
            {
                // Keep this draw order stable: X, Z, heading, speed.
                float2 position = random.NextFloat2(config.MinXZ, config.MaxXZ);
                float angle = random.NextFloat(2f * math.PI);
                float speed = random.NextFloat(config.MinSpeed, config.MaxSpeed);
                math.sincos(angle, out float sin, out float cos);
                units[index] = new UnitState { PositionXZ = position, VelocityXZ = new float2(cos, sin) * speed };
            }
        }
    }
}
