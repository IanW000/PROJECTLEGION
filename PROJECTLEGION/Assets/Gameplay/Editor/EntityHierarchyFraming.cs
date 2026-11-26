using System.Collections.Generic;
using System.Reflection;
using Unity.Entities;
using Unity.Rendering;
using Unity.Transforms;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace ProjectLegion.Gameplay.Editor
{
    [InitializeOnLoad]
    internal static class EntityHierarchyFraming
    {
        private const string SupportedEntitiesVersion = "1.4.8";
        private const double WindowDiscoveryInterval = 0.5;
        private static readonly Dictionary<EditorWindow, VisualElement> Roots = new();
        private static readonly List<EditorWindow> StaleWindows = new();
        private static EntityHierarchyRowAdapter _adapter;
        private static bool _bindingFailed;
        private static double _nextDiscovery;

        static EntityHierarchyFraming()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload += Shutdown;
            if (EditorApplication.isPlaying)
                StartWatching();
        }

        private static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.EnteredPlayMode)
                StartWatching();
            else if (state == PlayModeStateChange.ExitingPlayMode)
                StopWatching();
        }

        private static void StartWatching()
        {
            if (_bindingFailed)
                return;

            _nextDiscovery = 0;
            EditorApplication.update -= DiscoverWindows;
            EditorApplication.update += DiscoverWindows;
        }

        private static void DiscoverWindows()
        {
            double now = EditorApplication.timeSinceStartup;
            if (now < _nextDiscovery)
                return;

            _nextDiscovery = now + WindowDiscoveryInterval;
            foreach (var pair in Roots)
                if (!pair.Key || pair.Key.rootVisualElement != pair.Value)
                    StaleWindows.Add(pair.Key);

            foreach (var window in StaleWindows)
            {
                Roots[window].UnregisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
                Roots.Remove(window);
            }

            StaleWindows.Clear();
            foreach (var window in Resources.FindObjectsOfTypeAll<EditorWindow>())
            {
                if (!EntityHierarchyRowAdapter.IsHierarchyWindow(window.GetType()) || Roots.ContainsKey(window))
                    continue;

                if (_adapter == null)
                {
                    var package = UnityEditor.PackageManager.PackageInfo.FindForAssembly(window.GetType().Assembly);
                    if (package?.version != SupportedEntitiesVersion)
                    {
                        DisableIntegration();
                        return;
                    }

                    _adapter = EntityHierarchyRowAdapter.Bind(window.GetType().Assembly);
                    if (_adapter == null)
                    {
                        DisableIntegration();
                        return;
                    }
                }

                var root = window.rootVisualElement;
                root.RegisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
                Roots.Add(window, root);
            }
        }

        private static void OnPointerDown(PointerDownEvent evt)
        {
            if (evt.pointerId != PointerId.mousePointerId || evt.button != (int)MouseButton.LeftMouse ||
                evt.clickCount != 2 || evt.modifiers != EventModifiers.None ||
                evt.target is not VisualElement target)
                return;

            Entity entity;
            World world;
            try
            {
                if (!_adapter.TryGetEntity(target, out entity, out world))
                    return;
            }
            catch (TargetInvocationException)
            {
                DisableIntegration();
                return;
            }

            // Let the hierarchy finish handling selection before moving the Scene View camera.
            EditorApplication.delayCall += () =>
            {
                if (EditorApplication.isPlaying && SceneView.lastActiveSceneView is { } sceneView &&
                    sceneView && TryGetBounds(world, entity, out var bounds))
                    sceneView.Frame(bounds, true);
            };
        }

        internal static bool TryGetBounds(World world, Entity entity, out Bounds bounds)
        {
            bounds = default;
            if (world is not { IsCreated: true } || entity == Entity.Null)
                return false;

            var manager = world.EntityManager;
            if (!manager.Exists(entity) || manager.HasComponent<Prefab>(entity))
                return false;

            if (manager.HasComponent<WorldRenderBounds>(entity))
            {
                var aabb = manager.GetComponentData<WorldRenderBounds>(entity).Value;
                var size = (Vector3)aabb.Size;
                var center = (Vector3)aabb.Center;
                if (IsFinite(center) && IsFinite(size) && float.IsFinite(size.sqrMagnitude) &&
                    size.x >= 0 && size.y >= 0 && size.z >= 0 && size.sqrMagnitude > 0)
                {
                    bounds = new Bounds(center, size);
                    return true;
                }
            }

            Vector3 position;
            if (manager.HasComponent<LocalToWorld>(entity))
                position = manager.GetComponentData<LocalToWorld>(entity).Position;
            else if (!manager.HasComponent<Parent>(entity) && manager.HasComponent<LocalTransform>(entity))
                position = manager.GetComponentData<LocalTransform>(entity).Position;
            else
                return false;

            if (!IsFinite(position))
                return false;

            bounds = new Bounds(position, Vector3.one);
            return true;
        }

        private static bool IsFinite(Vector3 value) =>
            float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

        private static void DisableIntegration()
        {
            _bindingFailed = true;
            StopWatching();
            Debug.LogWarning("Entity double-click framing could not bind to Entities Hierarchy. " +
                $"The integration targets Entities {SupportedEntitiesVersion}.");
        }

        private static void StopWatching()
        {
            EditorApplication.update -= DiscoverWindows;
            foreach (var root in Roots.Values)
                root.UnregisterCallback<PointerDownEvent>(OnPointerDown, TrickleDown.TrickleDown);
            Roots.Clear();
        }

        private static void Shutdown()
        {
            StopWatching();
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
            AssemblyReloadEvents.beforeAssemblyReload -= Shutdown;
        }
    }
}
