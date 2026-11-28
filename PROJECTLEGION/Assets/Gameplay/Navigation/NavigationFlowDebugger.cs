using System;
using System.Diagnostics;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class NavigationFlowDebugger : MonoBehaviour
    {
        [Flags]
        private enum DebugLayers { Heatmap = 1, Arrows = 2, States = 4, Paths = 8 }

        [SerializeField] private NavigationGridAuthoring _authoring;
        [SerializeField] private Transform _destination, _focus;
        [SerializeField] private Transform[] _sampleStarts = Array.Empty<Transform>();
        [SerializeField] private DebugLayers _layers = DebugLayers.Heatmap | DebugLayers.Arrows |
            DebugLayers.States | DebugLayers.Paths;
        [SerializeField, Min(1f)] private float _radius = 15;
        [SerializeField, Range(1, 2048)] private int _nodeBudget = 512;
        [SerializeField, Range(1, 512)] private int _pathStepBudget = 128;

        private static readonly ProfilerMarker DrawMarker = new ProfilerMarker("Navigation.Flow.DebugDraw");
        private NavigationFlowFieldBuilder _builder;
        private NavigationFlowField _field;
        private JobHandle _retirement;
        private long _retiredBytes, _pendingBytes, _started;
        private int _workspaceNodes;
        private bool _discard;
        private double _submitMs;
        public string Summary { get; private set; } = "Build the navigation grid first.";

        public void BuildField()
        {
            if (_builder != null && _builder.Busy)
                return;
            if (_authoring == null || _authoring.gameObject != gameObject || _authoring.Grid == null ||
                _destination == null)
            {
                Summary = "Build the navigation grid and assign a goal marker.";
                return;
            }
            NavigationGrid grid = _authoring.Grid;
            Vector3 destination = _destination.position;
            if (!grid.TryWorldToCell(new float2(destination.x, destination.z), out var goal) ||
                !grid.TryGetNode(goal, out var node) || !node.Walkable)
            {
                Summary = "Goal is outside the grid or blocked.";
                return;
            }

            CollectRetired();
            int nodes = checked(grid.Size.x * grid.Size.y);
            long pending = (long)nodes * NavigationFlowField.BytesPerNode;
            long workspace = NavigationFlowFieldBuilder.WorkspaceBytes(nodes);
            long peak = _authoring.LiveGridBytes + _authoring.BatchPayloadBytes + _authoring.HierarchyPayloadBytes + workspace + pending +
                (_field?.PayloadBytes ?? 0) + _retiredBytes;
            if (peak > _authoring.MemoryBudgetBytes)
            {
                Summary = $"Build requires {peak:N0} B of navigation payload. Increase the budget or clear existing data.";
                return;
            }
            if (_workspaceNodes != nodes)
            {
                _builder?.Dispose();
                _builder = null;
                _workspaceNodes = 0;
                _builder = new NavigationFlowFieldBuilder(nodes);
                _workspaceNodes = nodes;
            }

            _discard = false;
            _started = Stopwatch.GetTimestamp();
            _builder.Begin(grid, goal, true);
            _submitMs = Elapsed(_started);
            _pendingBytes = pending;
            Summary = "Building Flow Field.";
            UpdateBudget();
        }

        public void ClearField()
        {
            _discard = true;
            RetireField();
            Summary = _builder != null && _builder.Busy ? "Cleared. In-flight build is draining." : "Field cleared.";
            ReleaseIdleWorkspace();
            UpdateBudget();
        }

        private void Pump()
        {
            var grid = _authoring != null ? _authoring.Grid : null;
            CollectRetired();
            if (_field != null && !_field.TryGetView(grid, out _))
            {
                RetireField();
                Summary = "Grid changed. Previous field invalidated.";
            }
            if (_builder != null && _builder.IsCompleted)
            {
                long publishStart = Stopwatch.GetTimestamp();
                bool finished = _builder.TryFinish(_discard ? null : grid, out var status, out var field);
                double publishMs = Elapsed(publishStart);
                if (!finished)
                    return;
                _pendingBytes = 0;
                if (field != null)
                {
                    RetireField();
                    _field = field;
                    Summary = $"Goal {field.Goal} | Reachable {field.Stats.Reachable:N0} | Max cost {field.Stats.MaxCost:F2}\n" +
                        $"Wall {Elapsed(_started):F2} ms | Schedule {_submitMs:F3} ms | Publish {publishMs:F3} ms\n" +
                        $"Field {field.PayloadBytes:N0} B | Workspace {_builder.PayloadBytes:N0} B";
                }
                else
                    Summary = _discard ? "Field cleared." : $"Build discarded: {status}.";
                if (_discard)
                    ReleaseIdleWorkspace();
            }
            UpdateBudget();
        }

        private void RetireField()
        {
            if (_field == null) return;
            long bytes = _field.PayloadBytes;
            JobHandle disposal = _field.Retire();
            _field = null;
            if (disposal.IsCompleted)
                disposal.Complete();
            else
            {
                _retirement = JobHandle.CombineDependencies(_retirement, disposal);
                _retiredBytes += bytes;
                JobHandle.ScheduleBatchedJobs();
            }
        }

        private void CollectRetired()
        {
            if (_retiredBytes == 0 || !_retirement.IsCompleted) return;
            _retirement.Complete();
            _retiredBytes = 0;
        }

        private void ReleaseIdleWorkspace()
        {
            if (_builder == null || _builder.Busy) return;
            _builder.Dispose();
            _builder = null;
            _workspaceNodes = 0;
        }

        private void UpdateBudget()
        {
            if (_authoring == null || _authoring.gameObject != gameObject) return;
            long payload = (_builder?.PayloadBytes ?? 0) + (_field?.PayloadBytes ?? 0) + _retiredBytes + _pendingBytes;
            _authoring.FlowPayloadBytes = payload;
            if (payload != 0)
                _authoring.ObservePayload(_authoring.LiveGridBytes + _authoring.BatchPayloadBytes + _authoring.HierarchyPayloadBytes + payload);
        }

        private static double Elapsed(long start) => (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;

        private void OnEnable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += EditorPump;
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorPump;
#endif
            _builder?.Dispose();
            _builder = null;
            _workspaceNodes = 0;
            _pendingBytes = 0;
            RetireField();
            _retirement.Complete();
            _retiredBytes = 0;
            UpdateBudget();
        }

#if UNITY_EDITOR
        private void EditorPump()
        {
            if (!Application.IsPlaying(gameObject) && (_builder != null || _field != null || _retiredBytes != 0))
                Pump();
        }
#endif

        private void Update()
        {
            if (Application.IsPlaying(gameObject) && (_builder != null || _field != null || _retiredBytes != 0))
                Pump();
        }

        private void OnDrawGizmosSelected()
        {
            if (_layers == 0 || _authoring == null) return;
            var grid = _authoring.Grid;
            if (Has(DebugLayers.States) && _destination != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(_destination.position + Vector3.up * 0.2f, 0.23f);
            }
            if (_field == null || !_field.TryGetView(grid, out var view)) return;

            using var scope = DrawMarker.Auto();
            float2 center = _focus != null ? new float2(_focus.position.x, _focus.position.z) : grid.Center(_field.Goal);
            float radius = math.max(1f, _radius);
            float2 low = (center - radius - grid.Origin) / grid.CellSize;
            float2 high = (center + radius - grid.Origin) / grid.CellSize;
            if (math.all(math.isfinite(low) & math.isfinite(high)) && !math.any(high < 0 | low >= (float2)grid.Size))
            {
                int2 min = (int2)math.clamp(math.floor(low), 0f, (float2)(grid.Size - 1));
                int2 max = (int2)math.clamp(math.floor(high), 0f, (float2)(grid.Size - 1));
                int2 span = max - min + 1;
                int stride = 1;
                int budget = Mathf.Clamp(_nodeBudget, 1, 2048);
                while ((long)((span.x + stride - 1) / stride) * ((span.y + stride - 1) / stride) > budget)
                    stride *= 2;
                for (int z = min.y; z <= max.y; z += stride)
                for (int x = min.x; x <= max.x; x += stride)
                    DrawCell(grid, view, new int2(x, z));
            }

            if (Has(DebugLayers.States))
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(Position(grid, _field.Goal), grid.CellSize * 0.4f);
            }

            if (Has(DebugLayers.Paths))
                for (int i = 0; i < Math.Min(8, _sampleStarts.Length); i++)
                    DrawPath(grid, view, _sampleStarts[i]);
        }

        private void DrawCell(NavigationGrid grid, NavigationFlowView view, int2 cell)
        {
            var state = view.Query(cell, out var next, out var cost);
            Vector3 position = Position(grid, cell);
            float size = grid.CellSize;
            if (state == NavigationFlowState.Direction)
            {
                if (Has(DebugLayers.Heatmap))
                {
                    float normalized = (float)(cost / Math.Max(double.Epsilon, _field.Stats.MaxCost));
                    Gizmos.color = Color.Lerp(new Color(0.06f, 0.8f, 0.75f), new Color(1f, 0.28f, 0.05f), normalized);
                    Gizmos.DrawCube(position, new Vector3(size * 0.78f, 0.028f, size * 0.78f));
                }
                if (Has(DebugLayers.Arrows))
                {
                    Vector3 from = position + Vector3.up * 0.08f;
                    Vector3 direction = (Position(grid, next) - position).normalized;
                    Vector3 tip = from + direction * (size * 0.37f);
                    Vector3 basePoint = tip - direction * (size * 0.12f);
                    Vector3 wing = Vector3.Cross(Vector3.up, direction).normalized * (size * 0.09f);
                    Gizmos.color = Color.white;
                    Gizmos.DrawLine(from - direction * (size * 0.14f), tip);
                    Gizmos.DrawLine(tip, basePoint + wing);
                    Gizmos.DrawLine(tip, basePoint - wing);
                }
            }
            else if (Has(DebugLayers.States) && state == NavigationFlowState.Blocked)
            {
                Gizmos.color = new Color(1f, 0.2f, 0.33f);
                Vector3 a = new Vector3(size, 0, size) * 0.23f;
                Vector3 b = new Vector3(-a.x, 0, a.z);
                Gizmos.DrawLine(position - a, position + a);
                Gizmos.DrawLine(position - b, position + b);
            }
            else if (Has(DebugLayers.States) && state == NavigationFlowState.Unreachable)
            {
                Gizmos.color = new Color(0.56f, 0.35f, 1f);
                Gizmos.DrawWireCube(position, new Vector3(size * 0.44f, 0.04f, size * 0.44f));
            }
        }

        private void DrawPath(NavigationGrid grid, NavigationFlowView view, Transform start)
        {
            if (start == null || !grid.TryWorldToCell(new float2(start.position.x, start.position.z), out var cell))
                return;
            Gizmos.color = new Color(1f, 0.38f, 0.95f);
            int budget = Mathf.Clamp(_pathStepBudget, 1, 512);
            for (int i = 0; i < budget && view.TryGetNext(cell, out var next); i++)
            {
                Gizmos.DrawLine(Position(grid, cell) + Vector3.up * 0.25f, Position(grid, next) + Vector3.up * 0.25f);
                cell = next;
            }
        }

        private bool Has(DebugLayers layer) => (_layers & layer) != 0;

        private Vector3 Position(NavigationGrid grid, int2 cell)
        {
            grid.TryGetNode(cell, out var node);
            float2 center = grid.Center(cell);
            return new Vector3(center.x, (node.HasSurface ? node.Height : _authoring.transform.position.y) + 0.1f, center.y);
        }
    }
}
