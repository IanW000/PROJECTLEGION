using UnityEngine;

namespace ProjectLegion.MovementBenchmark
{
    [CreateAssetMenu(menuName = "Project Legion/Movement Benchmark Settings")]
    public sealed class MovementBenchmarkSettings : ScriptableObject
    {
        [SerializeField, Tooltip("A: MonoBehaviour. B/C: ECS Run. D/E: ECS ScheduleParallel. C/E use Burst.")]
        private BenchmarkMode _mode = BenchmarkMode.MonoBehaviour;
        [SerializeField, Tooltip("Supported counts: 100, 500, 1000, 2500, 5000, 10000.")]
        private int _unitCount = 1000;
        [SerializeField, Min(0)] private int _warmupSteps = 300;
        [SerializeField, Min(1)] private int _sampleSteps = 1000;

        public BenchmarkMode Mode => _mode;
        public int UnitCount => _unitCount;

        internal BenchmarkConfig CreateConfig()
        {
            return new BenchmarkConfig(mode: _mode, unitCount: _unitCount, warmupSteps: _warmupSteps,
                sampleSteps: _sampleSteps);
        }
    }
}
