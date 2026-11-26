using System;
using NUnit.Framework;
using Unity.Mathematics;

namespace ProjectLegion.MovementBenchmark.Tests
{
    public class BenchmarkConfigTests
    {
        [Test]
        public void DefaultsMatchBenchmarkSpecification()
        {
            var config = new BenchmarkConfig();

            Assert.That(config.Mode, Is.EqualTo(BenchmarkMode.MonoBehaviour));
            Assert.That(config.UnitCount, Is.EqualTo(1000));
            Assert.That(config.MinXZ, Is.EqualTo(new float2(-100f)));
            Assert.That(config.MaxXZ, Is.EqualTo(new float2(100f)));
            Assert.That(config.MinSpeed, Is.EqualTo(2f));
            Assert.That(config.MaxSpeed, Is.EqualTo(8f));
            Assert.That(config.Seed, Is.EqualTo(12345u));
            Assert.That(config.StepSeconds, Is.EqualTo(1f / 60f));
            Assert.That(config.WarmupSteps, Is.EqualTo(300));
            Assert.That(config.SampleSteps, Is.EqualTo(1000));
        }

        [TestCase(100)]
        [TestCase(500)]
        [TestCase(1000)]
        [TestCase(2500)]
        [TestCase(5000)]
        [TestCase(10000)]
        public void AcceptsSupportedUnitCounts(int count)
        {
            Assert.That(new BenchmarkConfig(unitCount: count).UnitCount, Is.EqualTo(count));
        }

        [TestCase(BenchmarkMode.MonoBehaviour)]
        [TestCase(BenchmarkMode.EcsSequential)]
        [TestCase(BenchmarkMode.EcsSequentialBurst)]
        [TestCase(BenchmarkMode.EcsParallel)]
        [TestCase(BenchmarkMode.EcsParallelBurst)]
        public void AcceptsSupportedModes(BenchmarkMode mode)
        {
            Assert.That(new BenchmarkConfig(mode: mode).Mode, Is.EqualTo(mode));
        }

        [Test]
        public void RejectsInvalidRunParameters()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(mode: (BenchmarkMode)(-1)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(mode: (BenchmarkMode)5));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(unitCount: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(unitCount: 101));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(seed: 0u));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(warmupSteps: -1));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(sampleSteps: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(sampleSteps: int.MaxValue));
        }

        [Test]
        public void RejectsInvalidBounds()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(minXZ: new float2(float.NaN, 0f)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(maxXZ: new float2(0f, float.PositiveInfinity)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(maxXZ: new float2(-101f, 100f)));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(maxXZ: new float2(100f, -100f)));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new BenchmarkConfig(minXZ: new float2(-float.MaxValue), maxXZ: new float2(float.MaxValue)));
        }

        [Test]
        public void RejectsInvalidSpeedsAndSteps()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(minSpeed: 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(minSpeed: float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(maxSpeed: 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(maxSpeed: float.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(stepSeconds: 0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(stepSeconds: -1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(stepSeconds: float.NaN));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(stepSeconds: float.PositiveInfinity));
        }

        [Test]
        public void RejectsStepsThatCanReachASecondWallOrOverflow()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new BenchmarkConfig(minXZ: new float2(0f), maxXZ: new float2(100f, 8f), stepSeconds: 1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(stepSeconds: 26f));
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(stepSeconds: float.MaxValue));
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                new BenchmarkConfig(minXZ: new float2(float.MaxValue * 0.5f), maxXZ: new float2(float.MaxValue),
                    maxSpeed: float.MaxValue * 0.25f, stepSeconds: 1f));
        }

        [TestCase(100000000f, -100f, 100000128f, 100f)]
        [TestCase(-100f, -100000128f, 100f, -100000000f)]
        public void RejectsBoundsThatCanSwallowAnEntireMovementStep(float minX, float minZ, float maxX, float maxZ)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(
                minXZ: new float2(minX, minZ), maxXZ: new float2(maxX, maxZ)));
        }

        [Test]
        public void RejectsBoundsThatCanRoundAnInteriorStepOntoAWall()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(
                minXZ: new float2(100000000f, -100f), maxXZ: new float2(100000128f, 100f),
                minSpeed: 5f, maxSpeed: 5f, stepSeconds: 1f));
        }

        [Test]
        public void AcceptsOffsetBoundsWithEnoughPrecisionForMovement()
        {
            var config = new BenchmarkConfig(minXZ: new float2(10000f, -100f), maxXZ: new float2(10200f, 100f));

            Assert.That(config.MinXZ.x, Is.EqualTo(10000f));
        }
    }
}
