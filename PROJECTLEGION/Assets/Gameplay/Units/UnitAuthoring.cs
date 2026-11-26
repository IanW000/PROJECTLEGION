using Unity.Entities;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class UnitAuthoring : MonoBehaviour
    {
        [SerializeField, Min(0f)] private float _movementSpeed = 4f;

        private sealed class UnitBaker : Baker<UnitAuthoring>
        {
            public override void Bake(UnitAuthoring authoring)
            {
                Entity entity = GetEntity(TransformUsageFlags.Dynamic);
                AddComponent<UnitTag>(entity);
                AddComponent(entity, new UnitId());
                AddComponent(entity, new UnitStats { MovementSpeed = Mathf.Max(0f, authoring._movementSpeed) });
            }
        }
    }
}
