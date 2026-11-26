using System.Collections;
using NUnit.Framework;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.TestTools;

namespace ProjectLegion.MovementBenchmark.Tests
{
    public class MovementBenchmarkRuntimeTests
    {
        private const int TestUnitCount = 100;
        private const int SavedVSync = 1;
        private const int SavedTargetFrameRate = 72;
        private const int TimeoutFrames = 20;

        private GameObject _runnerObject;
        private GameObject _otherRunnerObject;
        private int _originalVSync;
        private int _originalTargetFrameRate;

        [SetUp]
        public void SetUp()
        {
            _originalVSync = QualitySettings.vSyncCount;
            _originalTargetFrameRate = Application.targetFrameRate;
            QualitySettings.vSyncCount = SavedVSync;
            Application.targetFrameRate = SavedTargetFrameRate;
        }

        [UnityTearDown]
        public IEnumerator TearDown()
        {
            if (_runnerObject != null)
                Object.Destroy(_runnerObject);
            if (_otherRunnerObject != null)
                Object.Destroy(_otherRunnerObject);

            yield return null;
            QualitySettings.vSyncCount = _originalVSync;
            Application.targetFrameRate = _originalTargetFrameRate;
        }

        [UnityTest]
        public IEnumerator InitializesThenAdvancesExactlyOncePerFrameAndCompletes()
        {
            var runner = CreateRunner(out _runnerObject);
            var config = new BenchmarkConfig(unitCount: TestUnitCount, warmupSteps: 1, sampleSteps: 2);
            var initial = new UnitState[TestUnitCount];
            UnitInitialization.Fill(config, initial);

            Assert.That(runner.TryStart(config), Is.True);
            Assert.That(runner.Lifecycle, Is.EqualTo(BenchmarkLifecycle.Initializing));
            Assert.That(runner.transform.childCount, Is.Zero);

            yield return WaitForState(runner, BenchmarkLifecycle.WarmingUp);
            Assert.That(runner.CompletedWarmupSteps, Is.Zero);
            Assert.That(runner.CompletedSampleSteps, Is.Zero);
            Assert.That(QualitySettings.vSyncCount, Is.Zero);
            Assert.That(Application.targetFrameRate, Is.EqualTo(-1));

            Transform root = runner.transform.GetChild(0);
            Assert.That(root.childCount, Is.EqualTo(TestUnitCount));
            var units = new MonoMovementUnit[TestUnitCount];
            for (int i = 0; i < units.Length; i++)
            {
                GameObject unitObject = root.GetChild(i).gameObject;
                units[i] = unitObject.GetComponent<MonoMovementUnit>();
                // Transform and the movement component are the only components on a unit.
                Assert.That(unitObject.GetComponents<Component>().Length, Is.EqualTo(2));
                Assert.That(units[i].State.PositionXZ, Is.EqualTo(initial[i].PositionXZ));
                Assert.That(units[i].State.VelocityXZ, Is.EqualTo(initial[i].VelocityXZ));
            }

            yield return null;
            Assert.That(runner.Lifecycle, Is.EqualTo(BenchmarkLifecycle.Sampling));
            Assert.That(runner.CompletedWarmupSteps, Is.EqualTo(1));
            Assert.That(runner.CompletedSampleSteps, Is.Zero);
            AssertPositionsAfterSteps(units[0].State, initial[0], config, 1);
            Assert.That(units[0].transform.localPosition, Is.EqualTo(Vector3.zero));

            yield return null;
            Assert.That(runner.CompletedSampleSteps, Is.EqualTo(1));
            AssertPositionsAfterSteps(units[0].State, initial[0], config, 2);

            yield return null;
            Assert.That(runner.Lifecycle, Is.EqualTo(BenchmarkLifecycle.Validating));
            Assert.That(runner.CompletedSampleSteps, Is.EqualTo(2));
            AssertPositionsAfterSteps(units[0].State, initial[0], config, 3);

            yield return WaitForState(runner, BenchmarkLifecycle.Completed);
            Assert.That(runner.IsRunning, Is.False);
            Assert.That(runner.FinalStates.Length, Is.EqualTo(TestUnitCount));
            for (int i = 0; i < initial.Length; i++)
                AssertPositionsAfterSteps(runner.FinalStates[i], initial[i], config, 3);
            AssertSettingsRestored();

            yield return null;
            Assert.That(root == null, Is.True);
            Assert.That(runner.CompletedWarmupSteps, Is.EqualTo(1));
            Assert.That(runner.CompletedSampleSteps, Is.EqualTo(2));
            AssertPositionsAfterSteps(runner.FinalStates[0], initial[0], config, 3);
        }

        [UnityTest]
        public IEnumerator CancellationDeactivatesUnitsAndAllowsAnImmediateRestart()
        {
            var runner = CreateRunner(out _runnerObject);
            var firstConfig = new BenchmarkConfig(unitCount: TestUnitCount, warmupSteps: 0, sampleSteps: 4);
            Assert.That(runner.TryStart(firstConfig), Is.True);
            yield return WaitForState(runner, BenchmarkLifecycle.Sampling);
            yield return null;
            Assert.That(runner.CompletedSampleSteps, Is.EqualTo(1));

            Transform oldRoot = runner.transform.GetChild(0);
            var oldUnit = oldRoot.GetChild(0).GetComponent<MonoMovementUnit>();
            runner.Cancel();

            Assert.That(runner.Lifecycle, Is.EqualTo(BenchmarkLifecycle.Cancelled));
            Assert.That(oldRoot.gameObject.activeSelf, Is.False);
            Assert.That(oldUnit.isActiveAndEnabled, Is.False);
            Assert.That(runner.CompletedSampleSteps, Is.EqualTo(1));
            Assert.That(runner.FinalStates.IsEmpty, Is.True);
            AssertSettingsRestored();

            yield return null;
            Assert.That(oldUnit == null, Is.True);
            Assert.That(runner.CompletedSampleSteps, Is.EqualTo(1));
            Assert.That(runner.transform.childCount, Is.Zero);

            var secondConfig = new BenchmarkConfig(unitCount: TestUnitCount, warmupSteps: 0, sampleSteps: 1);
            Assert.That(runner.TryStart(secondConfig), Is.True);
            Assert.That(runner.CompletedSampleSteps, Is.Zero);
            yield return WaitForState(runner, BenchmarkLifecycle.Completed);
            Assert.That(runner.CompletedSampleSteps, Is.EqualTo(1));
            Assert.That(runner.FinalStates.Length, Is.EqualTo(TestUnitCount));
            AssertSettingsRestored();

            Assert.That(runner.TryStart(secondConfig), Is.True);
            yield return WaitForState(runner, BenchmarkLifecycle.Sampling);
            Transform secondRoot = runner.transform.GetChild(0);
            runner.Cancel();
            Assert.That(secondRoot.gameObject.activeSelf, Is.False);
            // Reserve the next run before Destroy processes the previous root.
            Assert.That(runner.TryStart(secondConfig), Is.True);
            Assert.That(runner.GetComponentsInChildren<MonoMovementUnit>().Length, Is.Zero);
            yield return WaitForState(runner, BenchmarkLifecycle.Completed);
            Assert.That(secondRoot == null, Is.True);
            Assert.That(runner.CompletedSampleSteps, Is.EqualTo(1));
            AssertSettingsRestored();
        }

        [UnityTest]
        public IEnumerator RejectsOverlappingRunsAndKeepsTheCurrentOwner()
        {
            var runner = CreateRunner(out _runnerObject);
            var other = CreateRunner(out _otherRunnerObject);
            var config = new BenchmarkConfig(unitCount: TestUnitCount, warmupSteps: 0, sampleSteps: 3);
            Assert.That(runner.TryStart(config), Is.True);
            Assert.That(other.TryStart(config), Is.False);
            yield return WaitForState(runner, BenchmarkLifecycle.Sampling);

            Assert.That(runner.TryStart(config), Is.False);
            Assert.That(other.TryStart(config), Is.False);
            Assert.That(other.transform.childCount, Is.Zero);
            Assert.That(Application.targetFrameRate, Is.EqualTo(-1));

            runner.Cancel();
            Assert.That(other.TryStart(config), Is.True);
            yield return WaitForState(other, BenchmarkLifecycle.Completed);
            Assert.That(other.CompletedSampleSteps, Is.EqualTo(3));
            AssertSettingsRestored();
        }

        [Test]
        public void RejectsUnsupportedModeBeforeChangingSettings()
        {
            var runner = CreateRunner(out _runnerObject);
            var config = new BenchmarkConfig(mode: BenchmarkMode.EcsParallelBurst, unitCount: TestUnitCount);
            Assert.That(runner.TryStart(config), Is.False);
            Assert.That(runner.Lifecycle, Is.EqualTo(BenchmarkLifecycle.Failed));
            Assert.That(runner.transform.childCount, Is.Zero);
            Assert.That(runner.IsRunning, Is.False);
            AssertSettingsRestored();
        }

        [Test]
        public void CanCancelBeforeInitializationWithoutChangingSettings()
        {
            var runner = CreateRunner(out _runnerObject);
            Assert.That(runner.TryStart(new BenchmarkConfig(unitCount: TestUnitCount)), Is.True);
            runner.Cancel();

            Assert.That(runner.Lifecycle, Is.EqualTo(BenchmarkLifecycle.Cancelled));
            Assert.That(runner.transform.childCount, Is.Zero);
            Assert.That(runner.CompletedWarmupSteps, Is.Zero);
            Assert.That(runner.CompletedSampleSteps, Is.Zero);
            AssertSettingsRestored();
        }

        [UnityTest]
        public IEnumerator DisablingRunnerRestoresSettingsAndStopsUnits()
        {
            var runner = CreateRunner(out _runnerObject);
            Assert.That(runner.TryStart(new BenchmarkConfig(unitCount: TestUnitCount)), Is.True);
            yield return WaitForState(runner, BenchmarkLifecycle.WarmingUp);
            runner.enabled = false;

            Assert.That(runner.Lifecycle, Is.EqualTo(BenchmarkLifecycle.Cancelled));
            Assert.That(runner.GetComponentsInChildren<MonoMovementUnit>().Length, Is.Zero);
            AssertSettingsRestored();
            yield return null;
            Assert.That(runner.transform.childCount, Is.Zero);
            Assert.That(runner.CompletedWarmupSteps, Is.Zero);
        }

        [UnityTest]
        public IEnumerator DestroyingRunnerRestoresSettings()
        {
            var runner = CreateRunner(out _runnerObject);
            Assert.That(runner.TryStart(new BenchmarkConfig(unitCount: TestUnitCount)), Is.True);
            yield return WaitForState(runner, BenchmarkLifecycle.WarmingUp);
            Transform root = runner.transform.GetChild(0);

            Object.Destroy(_runnerObject);
            yield return null;
            Assert.That(runner == null, Is.True);
            Assert.That(root == null, Is.True);
            AssertSettingsRestored();
        }

        [UnityTest]
        public IEnumerator LossOfFrameEndFailsAndCleansUp()
        {
            var runner = CreateRunner(out _runnerObject);
            Assert.That(runner.TryStart(new BenchmarkConfig(unitCount: TestUnitCount)), Is.True);
            yield return WaitForState(runner, BenchmarkLifecycle.WarmingUp);
            runner.GetComponent<BenchmarkFrameEnd>().enabled = false;

            yield return WaitForState(runner, BenchmarkLifecycle.Failed);
            Assert.That(runner.IsRunning, Is.False);
            Assert.That(runner.CompletedWarmupSteps, Is.Zero);
            Assert.That(runner.GetComponentsInChildren<MonoMovementUnit>().Length, Is.Zero);
            AssertSettingsRestored();
            yield return null;
            Assert.That(runner.transform.childCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator TemporarilyDisablingAUnitInvalidatesTheRun()
        {
            var runner = CreateRunner(out _runnerObject);
            var config = new BenchmarkConfig(unitCount: TestUnitCount, warmupSteps: 0, sampleSteps: 4);
            Assert.That(runner.TryStart(config), Is.True);
            yield return WaitForState(runner, BenchmarkLifecycle.Sampling);
            yield return null;
            Assert.That(runner.CompletedSampleSteps, Is.EqualTo(1));

            Transform root = runner.transform.GetChild(0);
            var unit = root.GetChild(0).GetComponent<MonoMovementUnit>();
            unit.enabled = false;
            unit.enabled = true;

            yield return WaitForState(runner, BenchmarkLifecycle.Failed);
            Assert.That(runner.Status, Does.Contain("unit was disabled"));
            Assert.That(runner.IsRunning, Is.False);
            Assert.That(runner.CompletedSampleSteps, Is.EqualTo(1));
            Assert.That(runner.FinalStates.IsEmpty, Is.True);
            AssertSettingsRestored();
            yield return null;
            Assert.That(root == null, Is.True);
        }

        private static BenchmarkRunner CreateRunner(out GameObject runnerObject)
        {
            runnerObject = new GameObject("Test Benchmark Runner");
            return runnerObject.AddComponent<BenchmarkRunner>();
        }

        private static IEnumerator WaitForState(BenchmarkRunner runner, BenchmarkLifecycle target)
        {
            for (int frame = 0; frame < TimeoutFrames && runner.Lifecycle != target; frame++)
                yield return null;

            Assert.That(runner.Lifecycle, Is.EqualTo(target), runner.Status);
        }

        private static void AssertSettingsRestored()
        {
            Assert.That(QualitySettings.vSyncCount, Is.EqualTo(SavedVSync));
            Assert.That(Application.targetFrameRate, Is.EqualTo(SavedTargetFrameRate));
        }

        private static void AssertPositionsAfterSteps(UnitState actual, UnitState initial, BenchmarkConfig config, int steps)
        {
            double time = (double)config.StepSeconds * steps;
            AssertAxis(actual.PositionXZ.x, actual.VelocityXZ.x, initial.PositionXZ.x, initial.VelocityXZ.x,
                config.MinXZ.x, config.MaxXZ.x, time);
            AssertAxis(actual.PositionXZ.y, actual.VelocityXZ.y, initial.PositionXZ.y, initial.VelocityXZ.y,
                config.MinXZ.y, config.MaxXZ.y, time);
        }

        private static void AssertAxis(float actualPosition, float actualVelocity, float start, float velocity,
            float min, float max, double time)
        {
            // Fold the unbounded trajectory as a triangular wave in double precision.
            double width = (double)max - min;
            double period = 2.0 * width;
            double phase = (start - (double)min + velocity * time) % period;
            if (phase < 0)
                phase += period;

            bool reflected = phase > width;
            double expectedPosition = min + (reflected ? period - phase : phase);
            Assert.That(actualPosition, Is.EqualTo(expectedPosition).Within(0.00004));
            Assert.That(actualVelocity, Is.EqualTo(reflected ? -velocity : velocity));
        }
    }
}
