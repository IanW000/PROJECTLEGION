using System;
using NUnit.Framework;
using Unity.Mathematics;

namespace ProjectLegion.MovementBenchmark.Tests
{
    public class UnitInitializationTests
    {
        [TestCase(100)]
        [TestCase(500)]
        [TestCase(1000)]
        [TestCase(2500)]
        [TestCase(5000)]
        [TestCase(10000)]
        public void FillsEveryUnitWithinConfiguredRanges(int count)
        {
            var config = new BenchmarkConfig(unitCount: count, minXZ: new float2(-40f, 10f),
                maxXZ: new float2(75f, 30f), minSpeed: 3f, maxSpeed: 6f);
            var units = new UnitState[count];
            UnitInitialization.Fill(config, units);

            foreach (UnitState unit in units)
            {
                Assert.That(math.all(unit.PositionXZ >= config.MinXZ & unit.PositionXZ <= config.MaxXZ), Is.True);
                float speed = math.length(unit.VelocityXZ);
                Assert.That(speed, Is.InRange(3f - 0.00001f, 6f + 0.00001f));
            }
        }

        [Test]
        public void SameSeedRetainsExactOrderAcrossRunsAndCounts()
        {
            var first = new UnitState[100];
            var second = new UnitState[500];
            UnitInitialization.Fill(new BenchmarkConfig(unitCount: 100), first);
            UnitInitialization.Fill(new BenchmarkConfig(mode: BenchmarkMode.EcsParallelBurst, unitCount: 500), second);

            for (int i = 0; i < first.Length; i++)
            {
                Assert.That(second[i].PositionXZ, Is.EqualTo(first[i].PositionXZ));
                Assert.That(second[i].VelocityXZ, Is.EqualTo(first[i].VelocityXZ));
            }

            // These reference values lock down the seed and draw order without calling the generator.
            AssertState(first[0], 55.387735f, -20.965481f, -2.640682f, -3.926383f);
            AssertState(first[1], -66.526318f, 52.905488f, 7.141515f, -0.096974f);
        }

        [Test]
        public void DifferentSeedChangesSequence()
        {
            var config = new BenchmarkConfig(unitCount: 100);
            var first = new UnitState[100];
            var second = new UnitState[100];
            UnitInitialization.Fill(config, first);
            UnitInitialization.Fill(new BenchmarkConfig(unitCount: 100, seed: 67890u), second);

            Assert.That(second[0].PositionXZ, Is.Not.EqualTo(first[0].PositionXZ));
        }

        [Test]
        public void RejectsNullConfigAndMismatchedDestination()
        {
            Assert.Throws<ArgumentNullException>(() => UnitInitialization.Fill(null, new UnitState[100]));
            Assert.Throws<ArgumentException>(() => UnitInitialization.Fill(
                new BenchmarkConfig(unitCount: 100), new UnitState[99]));
        }

        private static void AssertState(UnitState actual, float x, float z, float vx, float vz)
        {
            const float tolerance = 0.00002f;
            Assert.That(actual.PositionXZ.x, Is.EqualTo(x).Within(tolerance));
            Assert.That(actual.PositionXZ.y, Is.EqualTo(z).Within(tolerance));
            Assert.That(actual.VelocityXZ.x, Is.EqualTo(vx).Within(tolerance));
            Assert.That(actual.VelocityXZ.y, Is.EqualTo(vz).Within(tolerance));
        }
    }
}
