using NUnit.Framework;
using Unity.Mathematics;

namespace ProjectLegion.MovementBenchmark.Tests
{
    public class UnitMovementTests
    {
        private static readonly float2 MinXZ = new float2(-10f, -20f);
        private static readonly float2 MaxXZ = new float2(10f, 20f);
        private const float TestStepSeconds = 0.25f;

        [TestCase(1f, 2f, 4f, -2f, 2f, 1.5f, 4f, -2f, TestName = "FreeMovement")]
        [TestCase(9.5f, 0f, 4f, 2f, 9.5f, 0.5f, -4f, 2f, TestName = "ReflectsAtMaxXWithOvershoot")]
        [TestCase(-9.75f, 0f, -4f, 0f, -9.25f, 0f, 4f, 0f, TestName = "ReflectsAtMinXWithOvershoot")]
        [TestCase(0f, 19.75f, 0f, 2f, 0f, 19.75f, 0f, -2f, TestName = "ReflectsAtMaxZWithOvershoot")]
        [TestCase(0f, -19.5f, 0f, -4f, 0f, -19.5f, 0f, 4f, TestName = "ReflectsAtMinZWithOvershoot")]
        [TestCase(9f, 0f, 4f, 0f, 10f, 0f, -4f, 0f, TestName = "ReflectsAtExactMaxXContact")]
        [TestCase(0f, -19f, 0f, -4f, 0f, -20f, 0f, 4f, TestName = "ReflectsAtExactMinZContact")]
        [TestCase(10f, 0f, 4f, 0f, 9f, 0f, -4f, 0f, TestName = "ReflectsOutwardVelocityAtWall")]
        [TestCase(10f, 0f, -4f, 0f, 9f, 0f, -4f, 0f, TestName = "KeepsInwardVelocityAtWall")]
        [TestCase(9.5f, -19.75f, 4f, -4f, 9.5f, -19.25f, -4f, 4f, TestName = "ReflectsBothAxesAtCorner")]
        [TestCase(10f, -20f, 0f, 0f, 10f, -20f, 0f, 0f, TestName = "ZeroVelocityStaysAtCorner")]
        public void AdvancesOneStep(float x, float z, float vx, float vz,
            float expectedX, float expectedZ, float expectedVx, float expectedVz)
        {
            var state = new UnitState { PositionXZ = new float2(x, z), VelocityXZ = new float2(vx, vz) };
            float speedSquared = math.lengthsq(state.VelocityXZ);

            UnitMovement.Advance(ref state, MinXZ, MaxXZ, TestStepSeconds);

            Assert.That(state.PositionXZ, Is.EqualTo(new float2(expectedX, expectedZ)));
            Assert.That(state.VelocityXZ, Is.EqualTo(new float2(expectedVx, expectedVz)));
            Assert.That(math.lengthsq(state.VelocityXZ), Is.EqualTo(speedSquared));
        }

        [Test]
        public void RemainsInsideAreaAndPreservesSpeedOverManySteps()
        {
            const int stepCount = 12000;
            var config = new BenchmarkConfig(unitCount: 100);
            var units = new UnitState[config.UnitCount];
            UnitInitialization.Fill(config, units);

            for (int i = 0; i < units.Length; i++)
            {
                UnitState state = units[i];
                float speedSquared = math.lengthsq(state.VelocityXZ);
                bool stayedInBounds = true;
                for (int step = 0; step < stepCount; step++)
                {
                    UnitMovement.Advance(ref state, config.MinXZ, config.MaxXZ, config.StepSeconds);
                    stayedInBounds &= math.all(state.PositionXZ >= config.MinXZ & state.PositionXZ <= config.MaxXZ);
                }

                Assert.That(stayedInBounds, Is.True, "Unit " + i);
                Assert.That(math.lengthsq(state.VelocityXZ), Is.EqualTo(speedSquared), "Unit " + i);
            }
        }
    }
}
