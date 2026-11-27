using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace ProjectLegion.Gameplay
{
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]
    public partial class UnitSpawnSystem : SystemBase
    {
        private EntityQuery _requests;
        private EntityQuery _source;
        private EntityQuery _owned;
        private ulong _lastUnitId;
        private int _resetCount;
        internal uint Generation { get; private set; }
        internal bool ResetPending => _resetCount != 0;
        internal bool CanReset => !_source.IsEmptyIgnoreFilter;

        internal bool RequestReset(int count)
        {
            if ((count != 100 && count != 1000 && count != 5000 && count != 10000) || ResetPending)
                return false;
            _resetCount = count;
            return true;
        }

        protected override void OnCreate()
        {
            _requests = GetEntityQuery(ComponentType.ReadOnly<UnitSpawnRequest>());
            _source = GetEntityQuery(ComponentType.ReadOnly<PrototypeSpawnSource>());
            _owned = GetEntityQuery(ComponentType.ReadOnly<PrototypeUnit>());
        }

        protected override void OnUpdate()
        {
            if (ResetPending && !_source.IsEmptyIgnoreFilter)
            {
                int count = _resetCount;
                _resetCount = 0;
                using var sources = _source.ToEntityArray(Allocator.Temp);
                Entity spawner = sources[0];
                var request = EntityManager.GetComponentData<PrototypeSpawnSource>(spawner).Request;
                if (EntityManager.HasComponent<Prefab>(request.Prefab) && EntityManager.HasComponent<UnitTag>(request.Prefab))
                {
                    World.GetExistingSystemManaged<UnitMoveCommandSystem>()?.CancelPending();
                    World.GetExistingSystemManaged<UnitSpatialGridSystem>()?.Invalidate();
                    World.GetExistingSystemManaged<UnitOverlapResolutionSystem>()?.Invalidate();
                    World.GetExistingSystemManaged<UnitArrivalSystem>()?.Invalidate();
                    EntityManager.DestroyEntity(_owned);
                    if (EntityManager.HasComponent<UnitSpawnRequest>(spawner))
                        EntityManager.RemoveComponent<UnitSpawnRequest>(spawner);
                    request.Count = count;
                    Spawn(spawner, request);
                    Generation++;
                }
            }
            if (_requests.IsEmptyIgnoreFilter)
                return;
            // Snapshot the requests because instantiation and removal change archetypes.
            using var spawners = _requests.ToEntityArray(Allocator.Temp);
            foreach (Entity spawner in spawners)
            {
                UnitSpawnRequest request = EntityManager.GetComponentData<UnitSpawnRequest>(spawner);
                if (request.Count > 0)
                {
                    if (EntityManager.HasComponent<Prefab>(request.Prefab) &&
                        EntityManager.HasComponent<UnitTag>(request.Prefab))
                        Spawn(spawner, request);
                    else
                        UnityEngine.Debug.LogError("The RTS unit spawner requires a baked unit entity prefab.");
                }

                EntityManager.RemoveComponent<UnitSpawnRequest>(spawner);
            }
        }

        private void Spawn(Entity spawner, UnitSpawnRequest request)
        {
            LocalTransform template = EntityManager.GetComponentData<LocalTransform>(request.Prefab);
            using var units = new NativeArray<Entity>(request.Count, Allocator.Temp, NativeArrayOptions.UninitializedMemory);
            EntityManager.Instantiate(request.Prefab, units);
            if (EntityManager.HasComponent<PrototypeSpawnSource>(spawner))
                EntityManager.AddComponent(units, ComponentType.ReadWrite<PrototypeUnit>());

            // Match the owning scene section so Unity unloads the units with the spawner.
            if (EntityManager.HasComponent<SceneTag>(spawner))
            {
                SceneTag owner = EntityManager.GetSharedComponent<SceneTag>(spawner);
                if (!EntityManager.HasComponent<SceneTag>(units[0]))
                    EntityManager.AddSharedComponent(units, owner);
                else if (!EntityManager.GetSharedComponent<SceneTag>(units[0]).Equals(owner))
                    EntityManager.SetSharedComponent(units, owner);
            }

            int columns = (int)math.ceil(math.sqrt((float)request.Count));
            int rows = (request.Count - 1) / columns + 1;
            for (int i = 0; i < units.Length; i++)
            {
                int row = i / columns;
                int column = i % columns;
                int unitsInRow = math.min(columns, request.Count - row * columns);
                template.Position = request.Origin + new float3(
                    column - (unitsInRow - 1) * 0.5f, 0f, row - (rows - 1) * 0.5f) * request.Spacing;

                EntityManager.SetComponentData(units[i], template);
                EntityManager.SetComponentData(units[i], new UnitId { Value = ++_lastUnitId });
            }
        }
    }
}
