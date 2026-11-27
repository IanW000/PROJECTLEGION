using System;
using System.Reflection;
using Unity.Entities;
using UnityEngine.UIElements;

namespace ProjectLegion.Gameplay.Editor
{
    // Entities 1.4.8 does not expose its window or bound row types publicly.
    internal sealed class EntityHierarchyRowAdapter
    {
        private readonly Type _rowType;
        private readonly PropertyInfo _handle;
        private readonly FieldInfo _kind;
        private readonly object _entityKind;
        private readonly PropertyInfo _entity;
        private readonly PropertyInfo _world;

        private EntityHierarchyRowAdapter(Type rowType, PropertyInfo handle, FieldInfo kind,
            object entityKind, PropertyInfo entity, PropertyInfo world)
        {
            _rowType = rowType;
            _handle = handle;
            _kind = kind;
            _entityKind = entityKind;
            _entity = entity;
            _world = world;
        }

        internal static bool IsHierarchyWindow(Type type) =>
            type.FullName == "Unity.Entities.Editor.HierarchyWindow" &&
            type.Assembly.GetName().Name == "Unity.Entities.Editor";

        internal static EntityHierarchyRowAdapter Bind(Assembly assembly)
        {
            var rowType = assembly.GetType("Unity.Entities.Editor.HierarchyListViewItem");
            var handle = rowType?.GetProperty("Handle");
            var kind = handle?.PropertyType.GetField("Kind");
            var entity = rowType?.GetProperty("Entity");
            var world = rowType?.GetProperty("World");
            if (rowType == null || !typeof(VisualElement).IsAssignableFrom(rowType) ||
                kind == null || !kind.FieldType.IsEnum || !Enum.IsDefined(kind.FieldType, "Entity") ||
                entity?.PropertyType != typeof(Entity) || world?.PropertyType != typeof(World))
                return null;

            return new EntityHierarchyRowAdapter(rowType, handle, kind,
                Enum.Parse(kind.FieldType, "Entity"), entity, world);
        }

        internal bool TryGetEntity(VisualElement target, out Entity entity, out World world)
        {
            entity = Entity.Null;
            world = null;
            for (var element = target; element != null; element = element.parent)
            {
                if (element is Toggle || element is Button || element is TextField ||
                    element.ClassListContains("hierarchy-item__system-button") ||
                    element.ClassListContains("hierarchy-item__ping-gameobject-button") ||
                    element.ClassListContains("hierarchy-item__prefab-stage-button"))
                    return false;

                if (!_rowType.IsInstanceOfType(element))
                    continue;

                // A SubScene header can also return an Entity. Only accept actual Entity rows.
                if (!Equals(_kind.GetValue(_handle.GetValue(element)), _entityKind))
                    return false;

                entity = (Entity)_entity.GetValue(element);
                world = (World)_world.GetValue(element);
                return entity != Entity.Null && world is { IsCreated: true };
            }

            return false;
        }
    }
}
