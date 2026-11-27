using System;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProjectLegion.Gameplay
{
    [DisallowMultipleComponent]
    [ExecuteAlways]
    public sealed class NavigationGridAuthoring : MonoBehaviour
    {
        [Flags]
        private enum DebugLayers
        {
            GridBoundaries = 1,
            WalkableNodes = 2,
            BlockedNodes = 4,
            NodeHeight = 8,
            ValidConnections = 16,
            InvalidConnections = 32,
            TraversalCost = 64
        }

        [SerializeField] private NavigationGridSettings _settings = NavigationGridSettings.Default;
        [SerializeField] private DebugLayers _debugLayers = DebugLayers.GridBoundaries | DebugLayers.WalkableNodes;
        [SerializeField] private Transform _debugFocus;
        [SerializeField, Min(1f)] private float _debugRadius = 15;
        [SerializeField, Range(1, 2048)] private int _debugNodeBudget = 512;
        [SerializeField] private bool _showNumericLabels;
        [SerializeField, Min(1)] private int _navigationMemoryMiB = 96;
        private NavigationGrid _grid;
        private JobHandle _retirement;
        private long _retiredBytes;
        private float _builtOriginY;
        private bool _builtDiagonals;
        internal NavigationGrid Grid => _grid;
        internal long BatchPayloadBytes { get; set; }
        internal long FlowPayloadBytes { get; set; }
        internal long MemoryBudgetBytes => (long)Mathf.Max(1, _navigationMemoryMiB) * 1024 * 1024;
        internal long PeakPayloadBytes { get; private set; }
        internal long LiveGridBytes
        {
            get
            {
                CollectRetiredGrid();
                return (_grid?.PayloadBytes ?? 0) + _retiredBytes;
            }
        }
        internal double FinalDisposalMilliseconds { get; private set; }

        [ContextMenu("Build Navigation Grid")]
        public void Build()
        {
            var physics = gameObject.scene.GetPhysicsScene();
            CollectRetiredGrid();
            if (_retiredBytes != 0)
            {
                Debug.LogWarning("Grid rebuild deferred. Retry after old grid readers finish.", this);
                return;
            }
            long peak = LiveGridBytes + BatchPayloadBytes + FlowPayloadBytes +
                NavigationGridBuilder.EstimateBuildPayload(physics, transform.position, _settings);
            if (peak > MemoryBudgetBytes)
            {
                Debug.LogWarning($"Grid rebuild requires {peak:N0} B of payload, above the navigation budget.", this);
                return;
            }
            ObservePayload(peak);
            Physics.SyncTransforms();
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            NavigationGrid replacement = NavigationGridBuilder.Build(physics, transform.position, _settings, out var metrics);
            Release();
            _grid = replacement;
            _builtOriginY = transform.position.y;
            _builtDiagonals = _settings.Diagonals;
            Debug.Log($"Navigation grid {_grid.Size.x} × {_grid.Size.y}, " +
                $"{stopwatch.Elapsed.TotalMilliseconds:F2} ms. Persistent payload {_grid.PayloadBytes:N0} B; " +
                $"temporary payload {metrics.SampleBufferBytes + metrics.QueryBufferPayloadBytes:N0} B. " +
                $"Samples {metrics.Samples:N0}, surface rays {metrics.SurfaceRaycasts:N0}, " +
                $"multilevel probes {metrics.AmbiguousProbes:N0}, saturated probes {metrics.SaturatedProbes:N0}.", this);
        }

        [ContextMenu("Clear Navigation Grid")]
        public void Release()
        {
            if (_grid == null)
                return;
            long bytes = _grid.PayloadBytes;
            JobHandle handle = _grid.Retire();
            _grid = null;
            if (handle.IsCompleted)
            {
                handle.Complete();
                return;
            }
            _retirement = JobHandle.CombineDependencies(_retirement, handle);
            _retiredBytes += bytes;
            JobHandle.ScheduleBatchedJobs();
        }

        internal void ObservePayload(long bytes) => PeakPayloadBytes = Math.Max(PeakPayloadBytes, bytes);

        private void CollectRetiredGrid()
        {
            if (_retiredBytes != 0 && _retirement.IsCompleted)
            {
                _retirement.Complete();
                _retiredBytes = 0;
            }
        }

        private void OnDisable() => Release();

        private void OnDestroy()
        {
            Release();
            long begin = System.Diagnostics.Stopwatch.GetTimestamp();
            _retirement.Complete();
            FinalDisposalMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - begin) *
                1000d / System.Diagnostics.Stopwatch.Frequency;
            _retiredBytes = 0;
        }

        private void OnDrawGizmosSelected()
        {
            if (_grid == null || _debugLayers == 0)
                return;

            float2 extent = (float2)_grid.Size * _grid.CellSize;
            if (Has(DebugLayers.GridBoundaries))
            {
                Gizmos.color = new Color(0.35f, 0.7f, 1f, 0.7f);
                Gizmos.DrawWireCube(At(_grid.Origin + extent * 0.5f, _builtOriginY),
                    new Vector3(extent.x, 0f, extent.y));
            }

            float2 center = _debugFocus != null
                ? new float2(_debugFocus.position.x, _debugFocus.position.z)
                : _grid.Origin + extent * 0.5f;
            float radius = Mathf.Max(1f, _debugRadius);
            float2 low = (center - radius - _grid.Origin) / _grid.CellSize;
            float2 high = (center + radius - _grid.Origin) / _grid.CellSize;
            if (!math.all(math.isfinite(low) & math.isfinite(high)) ||
                math.any(high < 0f | low >= (float2)_grid.Size))
                return;
            int2 min = (int2)math.clamp(math.floor(low), 0f, (float2)(_grid.Size - 1));
            int2 max = (int2)math.clamp(math.floor(high), 0f, (float2)(_grid.Size - 1));
            int budget = Mathf.Clamp(_debugNodeBudget, 1, 2048);
            int2 span = max - min + 1;
            int stride = 1;
            while (((span.x + stride - 1) / stride) * ((span.y + stride - 1) / stride) > budget)
                stride *= 2;

            for (int z = min.y; z <= max.y; z += stride)
            for (int x = min.x; x <= max.x; x += stride)
                DrawCell(new int2(x, z));
        }

        private void DrawCell(int2 cell)
        {
            _grid.TryGetNode(cell, out var node);
            float2 center = _grid.Center(cell);
            var position = At(center, node.HasSurface ? node.Height + 0.02f : _builtOriginY + 0.02f);
            float size = _grid.CellSize;
            if (Has(DebugLayers.GridBoundaries))
            {
                Gizmos.color = new Color(0.35f, 0.7f, 1f, 0.4f);
                Gizmos.DrawWireCube(position, new Vector3(size, 0f, size));
            }
            if (node.Walkable ? Has(DebugLayers.WalkableNodes) : Has(DebugLayers.BlockedNodes))
            {
                Gizmos.color = node.Walkable ? Color.green : Color.red;
                Gizmos.DrawSphere(position, size * 0.11f);
            }
            if (node.HasSurface && Has(DebugLayers.NodeHeight))
            {
                Gizmos.color = Color.cyan;
                Gizmos.DrawLine(At(center, _builtOriginY), position);
                Gizmos.DrawWireSphere(position, size * 0.17f);
            }
            if (node.Walkable && Has(DebugLayers.TraversalCost))
            {
                float value = Mathf.Log(node.TraversalCost, NavigationGrid.MaxTraversalCost);
                Gizmos.color = Color.Lerp(Color.cyan, new Color(1f, 0.4f, 0.1f), value);
                Gizmos.DrawCube(position + Vector3.up * 0.03f, new Vector3(size * 0.4f, 0.025f, size * 0.4f));
            }
#if UNITY_EDITOR
            if (_showNumericLabels && node.HasSurface && (Has(DebugLayers.NodeHeight) || Has(DebugLayers.TraversalCost)))
            {
                string label = Has(DebugLayers.NodeHeight) ? $"Y {node.Height:F2}" : "";
                if (node.Walkable && Has(DebugLayers.TraversalCost))
                    label += $"  ×{node.TraversalCost:F2}";
                UnityEditor.Handles.Label(position + Vector3.up * 0.08f, label);
            }
#endif
            if (!Has(DebugLayers.ValidConnections) && !Has(DebugLayers.InvalidConnections))
                return;
            DrawEdge(cell, 0, position);
            DrawEdge(cell, 1, position);
            if (_builtDiagonals)
            {
                DrawEdge(cell, 4, position);
                DrawEdge(cell, 5, position);
            }
        }

        private void DrawEdge(int2 cell, int direction, Vector3 position)
        {
            var neighbor = cell + NavigationGrid.Offset(direction);
            if (!_grid.TryGetNode(neighbor, out var other))
                return;
            bool valid = _grid.IsConnected(cell, direction);
            if (!(valid ? Has(DebugLayers.ValidConnections) : Has(DebugLayers.InvalidConnections)))
                return;
            Gizmos.color = valid ? Color.green : new Color(1f, 0.25f, 0.25f, 0.8f);
            var end = At(_grid.Center(neighbor), other.HasSurface ? other.Height + 0.02f : _builtOriginY + 0.02f);
            Gizmos.DrawLine(Vector3.Lerp(position, end, 0.14f), Vector3.Lerp(position, end, valid ? 0.86f : 0.45f));
        }

        private bool Has(DebugLayers layer) => (_debugLayers & layer) != 0;
        private static Vector3 At(float2 xz, float y) => new Vector3(xz.x, y, xz.y);
    }
}
