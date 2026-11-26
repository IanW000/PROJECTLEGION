using Unity.Entities;

namespace ProjectLegion.MovementBenchmark
{
    [DisableAutoCreation]
    internal partial class EcsMovementSystem : SystemBase
    {
        private EntityQuery _query;
        private MovementChunkData _data;
        private BenchmarkMode _mode;

        internal int ChunkCount => _query.CalculateChunkCount();

        protected override void OnCreate()
        {
            // Queries acquired through SystemBase belong to the system.
            _query = GetEntityQuery(ComponentType.ReadWrite<MovementState>());
            _data.StateHandle = GetComponentTypeHandle<MovementState>(isReadOnly: false);
            _data.ProbeHandle = GetComponentTypeHandle<MovementExecutionProbe>(isReadOnly: false);
        }

        internal void Configure(BenchmarkConfig config, bool recordExecution)
        {
            _mode = config.Mode;
            _data.MinXZ = config.MinXZ;
            _data.MaxXZ = config.MaxXZ;
            _data.StepSeconds = config.StepSeconds;
            _data.RecordExecution = recordExecution;
        }

        protected override void OnUpdate()
        {
            _data.StateHandle.Update(this);
            _data.ProbeHandle.Update(this);

            switch (_mode)
            {
                case BenchmarkMode.EcsSequential:
                    CompleteDependency();
                    new ManagedMovementChunkJob { Data = _data }.Run(_query);
                    break;
                case BenchmarkMode.EcsSequentialBurst:
                    CompleteDependency();
                    new BurstMovementChunkJob { Data = _data }.Run(_query);
                    break;
                case BenchmarkMode.EcsParallel:
                    Dependency = new ManagedMovementChunkJob { Data = _data }.ScheduleParallel(_query, Dependency);
                    break;
                case BenchmarkMode.EcsParallelBurst:
                    Dependency = new BurstMovementChunkJob { Data = _data }.ScheduleParallel(_query, Dependency);
                    break;
                default:
                    throw new System.InvalidOperationException("An ECS movement mode is required.");
            }
        }

        internal void CompleteStep()
        {
            CompleteDependency();
        }
    }
}
