using Unity.Entities;
using Unity.Mathematics;

namespace ProjectLegion.Gameplay
{
    public struct UnitTag : IComponentData { }

    public struct UnitSelected : IComponentData, IEnableableComponent { }

    public struct UnitMoveTarget : IComponentData, IEnableableComponent
    {
        public float2 DestinationXZ;
    }

    public struct UnitId : IComponentData
    {
        public ulong Value;
    }

    public struct UnitStats : IComponentData
    {
        public float MovementSpeed;
    }

    internal struct UnitAvoidanceVelocity : IComponentData
    {
        internal float2 Velocity;
        internal AvoidanceMove Mode;
    }

    internal struct UnitOverlapCorrection : IComponentData
    {
        internal float2 Displacement;
    }

    internal struct UnitSpawnRequest : IComponentData
    {
        public Entity Prefab;
        public int Count;
        public float Spacing;
        public float3 Origin;
    }
}
