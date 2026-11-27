using System.Collections.Generic;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    // The GameObject controller invokes this system on the main thread after updating the camera.
    [DisableAutoCreation]
    internal partial class UnitSelectionSystem : SystemBase
    {
        private EntityQuery _selected;

        protected override void OnCreate() =>
            _selected = GetEntityQuery(ComponentType.ReadOnly<UnitTag>(), ComponentType.ReadWrite<UnitSelected>());

        protected override void OnUpdate() { }

        internal void Clear() => EntityManager.SetComponentEnabled<UnitSelected>(_selected, false);

        internal void SelectSingle(Ray ray, float maxDistance, bool toggle)
        {
            Entity nearest = Entity.Null;
            float nearestDistance = maxDistance;
            ulong nearestId = ulong.MaxValue;
            foreach (var (renderBounds, id, entity) in SystemAPI
                         .Query<RefRO<WorldRenderBounds>, RefRO<UnitId>>()
                         .WithAll<UnitTag, UnitSelected>()
                         .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState).WithEntityAccess())
            {
                var aabb = renderBounds.ValueRO.Value;
                var bounds = new Bounds(aabb.Center, aabb.Size);
                if (!bounds.IntersectRay(ray, out float distance) || distance > nearestDistance ||
                    (distance == nearestDistance && id.ValueRO.Value >= nearestId))
                    continue;

                nearest = entity;
                nearestDistance = distance;
                nearestId = id.ValueRO.Value;
            }

            if (!toggle || nearest == Entity.Null)
                Clear();
            if (nearest != Entity.Null)
                EntityManager.SetComponentEnabled<UnitSelected>(nearest,
                    !toggle || !EntityManager.IsComponentEnabled<UnitSelected>(nearest));
        }

        internal void SelectBox(Camera camera, Rect screenRect, bool additive)
        {
            if (!additive)
                Clear();

            Rect viewport = camera.pixelRect;
            Vector2 minimum = (screenRect.min - viewport.min) * 2f / viewport.size - Vector2.one;
            Vector2 maximum = (screenRect.max - viewport.min) * 2f / viewport.size - Vector2.one;
            Matrix4x4 view = camera.worldToCameraMatrix;
            Matrix4x4 viewProjection = camera.projectionMatrix * view;
            float near = camera.nearClipPlane;
            float far = camera.farClipPlane;
            foreach (var (transform, selected) in SystemAPI
                         .Query<RefRO<LocalToWorld>, EnabledRefRW<UnitSelected>>().WithAll<UnitTag>()
                         .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState))
            {
                Vector3 position = transform.ValueRO.Position;
                float depth = -view.MultiplyPoint3x4(position).z;
                if (depth < near || depth > far)
                    continue;

                Vector4 clip = viewProjection * new Vector4(position.x, position.y, position.z, 1f);
                if (clip.w > 0f && clip.x >= minimum.x * clip.w && clip.x <= maximum.x * clip.w &&
                    clip.y >= minimum.y * clip.w && clip.y <= maximum.y * clip.w)
                    selected.ValueRW = true;
            }
        }

        internal void CollectIndicatorMatrices(List<Matrix4x4> matrices, float radius, float height)
        {
            matrices.Clear();
            var scale = new Vector3(radius, radius, radius);
            foreach (var transform in SystemAPI.Query<RefRO<LocalToWorld>>().WithAll<UnitTag, UnitSelected>())
            {
                Vector3 position = transform.ValueRO.Position;
                position.y += height;
                matrices.Add(Matrix4x4.TRS(position, Quaternion.identity, scale));
            }
        }
    }
}
