using Unity.Entities;
using Unity.Mathematics;

namespace ProjectLegion.Gameplay
{
    public struct UnitTag : IComponentData { }

    public struct UnitId : IComponentData
    {
        public ulong Value;
    }

    public struct UnitStats : IComponentData
    {
        public float MovementSpeed;
    }

    internal struct UnitSpawnRequest : IComponentData
    {
        public Entity Prefab;
        public int Count;
        public float Spacing;
        public float3 Origin;
    }
}
