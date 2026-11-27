using Unity.Entities;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class UnitSpawnerAuthoring : MonoBehaviour
    {
        [SerializeField] private UnitAuthoring _unitPrefab = null;
        [SerializeField, Min(0)] private int _unitCount = 1000;
        [SerializeField, Min(0.01f)] private float _spacing = 0.7f;
        [SerializeField] private Vector3 _spawnOrigin = new Vector3(0f, 0f, 19f);

        private sealed class SpawnerBaker : Baker<UnitSpawnerAuthoring>
        {
            public override void Bake(UnitSpawnerAuthoring authoring)
            {
                if (authoring._unitPrefab == null)
                {
                    Debug.LogError("Assign a unit prefab to the RTS unit spawner.", authoring);
                    return;
                }

                AddComponent(GetEntity(TransformUsageFlags.None), new UnitSpawnRequest
                {
                    Prefab = GetEntity(authoring._unitPrefab, TransformUsageFlags.Dynamic),
                    Count = Mathf.Max(0, authoring._unitCount),
                    Spacing = Mathf.Max(0.01f, authoring._spacing),
                    Origin = authoring._spawnOrigin
                });
            }
        }
    }
}
