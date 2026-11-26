using Unity.Entities;

namespace ProjectLegion.MovementBenchmark
{
    internal struct MovementState : IComponentData
    {
        public UnitState Value;
    }

    // Present only on units created for execution diagnostics.
    internal struct MovementExecutionProbe : IComponentData
    {
        public int Backend;
    }
}
