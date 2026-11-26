using System;
using Unity.Mathematics;
using UnityEngine;

namespace ProjectLegion.MovementBenchmark
{
    public enum BenchmarkLifecycle
    {
        Idle,
        Initializing,
        WarmingUp,
        Sampling,
        Validating,
        CleaningUp,
        Completed,
        Cancelled,
        Failed
    }

    [DefaultExecutionOrder(-100)]
    [RequireComponent(typeof(BenchmarkFrameEnd))]
    [DisallowMultipleComponent]
    public sealed class BenchmarkRunner : MonoBehaviour
    {
        private const string UnitDisabledStatus = "A benchmark unit was disabled during the run.";
        private static BenchmarkRunner _owner;

        [SerializeField] private MovementBenchmarkSettings _settings = null;

        private BenchmarkConfig _config;
        private BenchmarkFrameEnd _frameEnd;
        private GameObject _unitRoot;
        private MonoMovementUnit[] _units;
        private EcsMovementWorld _ecsWorld;
        private UnitState[] _states;
        private bool _cancelRequested;
        private bool _unitWasDisabled;
        private bool _ownsRuntimeSettings;
        private int _originalVSyncCount;
        private int _originalTargetFrameRate;

        public BenchmarkLifecycle Lifecycle { get; private set; }
        public BenchmarkMode Mode => _config?.Mode ?? (_settings != null ? _settings.Mode : BenchmarkMode.MonoBehaviour);
        public int UnitCount => _config?.UnitCount ?? (_settings != null ? _settings.UnitCount : 0);
        public int CompletedWarmupSteps { get; private set; }
        public int CompletedSampleSteps { get; private set; }
        public string Status { get; private set; } = "Idle.";
        public bool IsRunning => _owner == this;
        public ReadOnlySpan<UnitState> FinalStates => Lifecycle == BenchmarkLifecycle.Completed ? _states : default;

        internal bool IsStepOpen { get; private set; }

        public bool TryStartConfiguredRun()
        {
            if (!CanStart())
                return false;

            ResetRunProgress();
            try
            {
                if (_settings == null)
                    throw new InvalidOperationException("Assign a movement benchmark settings asset.");
                return BeginRun(_settings.CreateConfig());
            }
            catch (Exception exception)
            {
                Finish(BenchmarkLifecycle.Failed, exception.Message);
                return false;
            }
        }

        public bool TryStart(BenchmarkConfig config)
        {
            if (!CanStart())
                return false;

            ResetRunProgress();
            return BeginRun(config);
        }

        private void ResetRunProgress()
        {
            _config = null;
            _states = null;
            CompletedWarmupSteps = 0;
            CompletedSampleSteps = 0;
        }

        private bool BeginRun(BenchmarkConfig config)
        {
            _config = config;
            if (config == null)
            {
                Finish(BenchmarkLifecycle.Failed, "A benchmark configuration is required.");
                return false;
            }

            _owner = this;
            Lifecycle = BenchmarkLifecycle.Initializing;
            Status = "Initializing.";
            return true;
        }

        private bool CanStart()
        {
            if (!Application.isPlaying || !isActiveAndEnabled || IsRunning)
                return false;
            if (_owner != null)
            {
                Status = "Another benchmark run is active.";
                return false;
            }
            return true;
        }

        public void Cancel()
        {
            if (!IsRunning)
                return;

            // If unit callbacks have started, let all of them finish the same step before stopping.
            if (IsStepOpen)
            {
                _cancelRequested = true;
                return;
            }

            Finish(BenchmarkLifecycle.Cancelled, "Cancelled.");
        }

        private void Update()
        {
            if (!IsRunning)
                return;

            if (FailIfUnitWasDisabled())
                return;

            try
            {
                switch (Lifecycle)
                {
                    case BenchmarkLifecycle.Initializing:
                        InitializeRun();
                        break;
                    case BenchmarkLifecycle.WarmingUp:
                    case BenchmarkLifecycle.Sampling:
                        if (IsStepOpen || _frameEnd == null || !_frameEnd.isActiveAndEnabled)
                            throw new InvalidOperationException("The benchmark frame-end callback is unavailable.");
                        IsStepOpen = true;
                        _ecsWorld?.StartStep();
                        break;
                    case BenchmarkLifecycle.Validating:
                        ValidateResults();
                        Finish(BenchmarkLifecycle.Completed, "Completed.");
                        break;
                }
            }
            catch (Exception exception)
            {
                Finish(BenchmarkLifecycle.Failed, exception.Message);
            }
        }

        private void InitializeRun()
        {
            _frameEnd = GetComponent<BenchmarkFrameEnd>();
            if (_frameEnd == null || !_frameEnd.isActiveAndEnabled)
                throw new InvalidOperationException("Enable the benchmark frame-end callback before starting.");
            _frameEnd.Bind(this);

            _originalVSyncCount = QualitySettings.vSyncCount;
            _originalTargetFrameRate = Application.targetFrameRate;
            _ownsRuntimeSettings = true;
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;

            _states = new UnitState[_config.UnitCount];
            UnitInitialization.Fill(_config, _states);
            if (_config.Mode == BenchmarkMode.MonoBehaviour)
                InitializeMonoUnits();
            else
                _ecsWorld = new EcsMovementWorld(_config, _states);

            Lifecycle = _config.WarmupSteps > 0 ? BenchmarkLifecycle.WarmingUp : BenchmarkLifecycle.Sampling;
            Status = _config.WarmupSteps > 0 ? "Warming up." : "Sampling steps.";
            // No step is opened here, even if Unity dispatches Update to newly enabled units this frame.
        }

        private void InitializeMonoUnits()
        {
            _units = new MonoMovementUnit[_config.UnitCount];
            _unitRoot = new GameObject("Benchmark Units");
            _unitRoot.SetActive(false);
            _unitRoot.transform.SetParent(transform, false);

            for (int i = 0; i < _units.Length; i++)
            {
                var unitObject = new GameObject("Movement Unit");
                unitObject.transform.SetParent(_unitRoot.transform, false);
                var unit = unitObject.AddComponent<MonoMovementUnit>();
                unit.Initialize(this, _config, _states[i]);
                _units[i] = unit;
            }

            _unitRoot.SetActive(true);
        }

        internal void CloseStep()
        {
            if (!IsStepOpen)
                return;

            IsStepOpen = false;
            try
            {
                _ecsWorld?.CompleteStep();
            }
            catch (Exception exception)
            {
                Finish(BenchmarkLifecycle.Failed, exception.Message);
                return;
            }

            if (FailIfUnitWasDisabled())
                return;

            if (Lifecycle == BenchmarkLifecycle.WarmingUp)
            {
                CompletedWarmupSteps++;
                if (CompletedWarmupSteps == _config.WarmupSteps)
                {
                    Lifecycle = BenchmarkLifecycle.Sampling;
                    Status = "Sampling steps.";
                }
            }
            else
            {
                CompletedSampleSteps++;
                if (CompletedSampleSteps == _config.SampleSteps)
                {
                    Lifecycle = BenchmarkLifecycle.Validating;
                    Status = "Validating.";
                }
            }

            if (_cancelRequested)
                Finish(BenchmarkLifecycle.Cancelled, "Cancelled.");
        }

        internal void ReportUnitDisabled()
        {
            // Keep the failure even if the unit is enabled again before the next callback.
            if (IsRunning && Lifecycle != BenchmarkLifecycle.CleaningUp)
                _unitWasDisabled = true;
        }

        private bool FailIfUnitWasDisabled()
        {
            if (!_unitWasDisabled)
                return false;

            Finish(BenchmarkLifecycle.Failed, UnitDisabledStatus);
            return true;
        }

        private void ValidateResults()
        {
            if (CompletedWarmupSteps != _config.WarmupSteps || CompletedSampleSteps != _config.SampleSteps)
                throw new InvalidOperationException("Unexpected simulation step count.");

            _ecsWorld?.CopyStates(_states);
            for (int i = 0; i < _states.Length; i++)
            {
                if (_units != null)
                {
                    var unit = _units[i];
                    if (unit == null || !unit.isActiveAndEnabled)
                        throw new InvalidOperationException("A benchmark unit was removed or disabled.");

                    _states[i] = unit.State;
                }

                UnitState state = _states[i];
                if (!math.all(math.isfinite(state.PositionXZ)) || !math.all(math.isfinite(state.VelocityXZ)) ||
                    !math.all(state.PositionXZ >= _config.MinXZ & state.PositionXZ <= _config.MaxXZ))
                {
                    throw new InvalidOperationException("A benchmark unit has invalid simulation state.");
                }
            }
        }

        private void Finish(BenchmarkLifecycle terminalState, string status)
        {
            Lifecycle = BenchmarkLifecycle.CleaningUp;
            IsStepOpen = false;
            _cancelRequested = false;
            _unitWasDisabled = false;

            try
            {
                _ecsWorld?.Dispose();
            }
            catch (Exception exception)
            {
                terminalState = BenchmarkLifecycle.Failed;
                status = $"{status} ECS cleanup failed: {exception.Message}";
            }
            finally
            {
                _ecsWorld = null;
            }

            if (_unitRoot != null)
            {
                // Destroy is deferred. Deactivate first so old units cannot run during a new run.
                _unitRoot.SetActive(false);
                Destroy(_unitRoot);
                _unitRoot = null;
            }
            _units = null;
            if (terminalState != BenchmarkLifecycle.Completed)
                _states = null;

            if (_ownsRuntimeSettings)
            {
                QualitySettings.vSyncCount = _originalVSyncCount;
                Application.targetFrameRate = _originalTargetFrameRate;
                _ownsRuntimeSettings = false;
            }
            if (_owner == this)
                _owner = null;

            Lifecycle = terminalState;
            Status = status;
        }

        private void OnDisable()
        {
            if (IsRunning)
                Finish(BenchmarkLifecycle.Cancelled, "Runner disabled or destroyed.");
        }
    }
}
