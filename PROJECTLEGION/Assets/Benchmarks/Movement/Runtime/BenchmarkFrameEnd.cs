using UnityEngine;

namespace ProjectLegion.MovementBenchmark
{
    [DefaultExecutionOrder(100)]
    [DisallowMultipleComponent]
    public sealed class BenchmarkFrameEnd : MonoBehaviour
    {
        private BenchmarkRunner _runner;

        internal void Bind(BenchmarkRunner runner)
        {
            _runner = runner;
        }

        private void Update()
        {
            if (_runner != null)
                _runner.CloseStep();
        }
    }
}
