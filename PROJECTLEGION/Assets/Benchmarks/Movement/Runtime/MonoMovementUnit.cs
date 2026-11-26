using UnityEngine;

namespace ProjectLegion.MovementBenchmark
{
    [DefaultExecutionOrder(0)]
    [AddComponentMenu("")]
    [DisallowMultipleComponent]
    public sealed class MonoMovementUnit : MonoBehaviour
    {
        private BenchmarkRunner _runner;
        private BenchmarkConfig _config;
        private UnitState _state;

        public UnitState State => _state;

        internal void Initialize(BenchmarkRunner runner, BenchmarkConfig config, UnitState state)
        {
            _runner = runner;
            _config = config;
            _state = state;
        }

        private void Update()
        {
            // The runner opens one shared step before unit callbacks and closes it after all of them.
            if (_runner.IsStepOpen)
                UnitMovement.Advance(ref _state, _config.MinXZ, _config.MaxXZ, _config.StepSeconds);
        }

        private void OnDisable()
        {
            if (_runner != null)
                _runner.ReportUnitDisabled();
        }
    }
}
