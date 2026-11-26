using Unity.Mathematics;

namespace ProjectLegion.MovementBenchmark
{
    public struct UnitState
    {
        public float2 PositionXZ;
        public float2 VelocityXZ;
    }

    public static class UnitMovement
    {
        // Pass validated bounds and step values, an in-bounds state, and velocity within the configured speed limit.
        public static void Advance(ref UnitState state, float2 minXZ, float2 maxXZ, float stepSeconds)
        {
            float2 next = state.PositionXZ + state.VelocityXZ * stepSeconds;
            ReflectAxis(ref next.x, ref state.VelocityXZ.x, minXZ.x, maxXZ.x);
            ReflectAxis(ref next.y, ref state.VelocityXZ.y, minXZ.y, maxXZ.y);
            state.PositionXZ = next;
        }

        private static void ReflectAxis(ref float next, ref float velocity, float min, float max)
        {
            // Mirror only the overshoot. At exact contact, turn an outward velocity inward.
            if (velocity > 0f && next >= max)
            {
                next = math.max(min, max - (next - max));
                velocity = -velocity;
            }
            else if (velocity < 0f && next <= min)
            {
                next = math.min(max, min + (min - next));
                velocity = -velocity;
            }
        }
    }
}
