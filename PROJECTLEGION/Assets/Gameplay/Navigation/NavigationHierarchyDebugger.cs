using System;
using System.Diagnostics;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class NavigationHierarchyDebugger : MonoBehaviour
    {
        [Flags]
        private enum Layers { Clusters = 1, Portals = 2, Crossing = 4, IntraCluster = 8,
            AbstractPath = 16, RefinedPath = 32, AStar = 64 }

        [SerializeField] private NavigationGridAuthoring _authoring;
        [SerializeField] private Transform _start, _destination, _focus;
        [SerializeField] private Vector2Int _clusterSize = new Vector2Int(8, 6);
        [SerializeField] private NavigationPortalMode _portalMode;
        [SerializeField, Range(1, 16)] private int _workers = 4;
        [SerializeField, Min(1)] private int _outputNodes = 8192;
        [SerializeField] private bool _compareAStar;
        [SerializeField] private Layers _layers = Layers.Clusters | Layers.Portals | Layers.Crossing |
            Layers.AbstractPath | Layers.RefinedPath | Layers.AStar;
        [SerializeField, Min(1)] private float _radius = 17;
        [SerializeField, Range(1, 512)] private int _clusterBudget = 96;
        [SerializeField, Range(1, 2048)] private int _portalBudget = 384;
        [SerializeField, Range(1, 2048)] private int _edgeBudget = 512;
        [SerializeField, Range(1, 2048)] private int _pathBudget = 512;

        private static readonly ProfilerMarker DrawMarker = new ProfilerMarker("Navigation.HPA.DebugDraw");
        private NavigationHierarchyBuilder _builder;
        private NavigationHierarchyGraph _graph;
        private NavigationHierarchyPathfinder _query;
        private NavigationPathfinder _baseline;
        private NavigationGrid _queryGrid;
        private JobHandle _retirement, _baselineJob;
        private bool _discard, _hasPath, _baselinePending;
        private double _buildMs, _queryMs, _baselineMs;
        private long _retiredBytes, _buildStart, _queryStart, _baselineStart;
        private int2 _from, _goal;
        private NavigationHierarchyResult _result;
        private NavigationPathResult _baselineResult;

        public string Summary { get; private set; } = "Build the navigation grid first.";

        public void BuildGraph()
        {
            if (_builder != null && _builder.Busy || _query != null && _query.Busy || _baselinePending)
                return;
            if (_authoring == null || _authoring.gameObject != gameObject || _authoring.Grid == null)
            {
                Summary = "Build the navigation grid first.";
                return;
            }
            CollectRetired();
            ClearQuery();
            _discard = false;
            _builder ??= new NavigationHierarchyBuilder();
            _buildStart = Stopwatch.GetTimestamp();
            var status = _builder.Begin(_authoring.Grid, new int2(_clusterSize.x, _clusterSize.y), _portalMode,
                Mathf.Clamp(_workers, 1, 16), Available - (_graph?.PayloadBytes ?? 0) - _retiredBytes, true);
            Summary = status == NavigationHierarchyBuildStatus.Success ? "Building hierarchy." : $"Build rejected: {status}.";
            UpdateBudget();
        }

        public void Search()
        {
            if (_discard || _builder != null && _builder.Busy || _query != null && _query.Busy || _baselinePending)
                return;
            NavigationGrid grid = _authoring != null ? _authoring.Grid : null;
            if (_graph == null || !_graph.IsCurrent(grid) || _start == null || _destination == null)
            {
                Summary = "Build the hierarchy and assign both endpoint markers.";
                return;
            }
            _from = ToCell(grid, _start.position);
            _goal = ToCell(grid, _destination.position);
            _hasPath = false;
            _baselineResult = default;
            _baselineResult.Status = NavigationPathStatus.Unsearched;
            int capacity = Mathf.Max(1, _outputNodes);
            if (_query == null || _query.OutputCapacity != capacity)
            {
                _query?.Dispose();
                _query = null;
                long budget = Available - _graph.PayloadBytes - _retiredBytes - (_baseline?.PayloadBytes ?? 0);
                try
                {
                    if (budget < 1) throw new ArgumentException();
                    _query = new NavigationHierarchyPathfinder(_graph, capacity, budget);
                }
                catch (ArgumentException)
                {
                    Summary = "The configured complete-path workspace exceeds the navigation budget.";
                    UpdateBudget();
                    return;
                }
            }
            _queryGrid = grid;
            _queryStart = Stopwatch.GetTimestamp();
            _query.Begin(grid, _graph, _from, _goal, true);
            Summary = "Searching.";
            UpdateBudget();
        }

        private static int2 ToCell(NavigationGrid grid, Vector3 point) =>
            grid.TryWorldToCell(new float2(point.x, point.z), out var result) ? result : new int2(-1);

        public void ClearGraph()
        {
            _discard = true;
            _hasPath = false;
            RetireGraph();
            if (!(_query?.Busy ?? false) && !_baselinePending) ClearQuery();
            Summary = "Hierarchy cleared. Any in-flight jobs will drain.";
            UpdateBudget();
        }

        private long Available => _authoring.MemoryBudgetBytes - _authoring.LiveGridBytes -
            _authoring.BatchPayloadBytes - _authoring.FlowPayloadBytes;

        private void Pump()
        {
            if (_authoring == null) return;
            var grid = _authoring.Grid;
            CollectRetired();
            if (_graph != null && !_graph.IsCurrent(grid))
            {
                RetireGraph();
                _hasPath = false;
                Summary = "Grid changed. Hierarchy invalidated.";
            }

            long other = (_graph?.PayloadBytes ?? 0) + _retiredBytes + (_query?.PayloadBytes ?? 0) +
                (_baseline?.PayloadBytes ?? 0);
            if (_builder != null && _builder.TryFinish(_discard ? null : grid, out var status, out var graph, Available - other))
            {
                if (graph != null)
                {
                    _buildMs = Elapsed(_buildStart);
                    RetireGraph();
                    _graph = graph;
                    Describe();
                }
                else Summary = _discard ? "Hierarchy cleared." : $"Build ended: {status}.";
            }

            if (_query != null && _query.TryFinish(grid, _graph, out var result, Available - Payload))
            {
                _queryMs = Elapsed(_queryStart);
                _result = result;
                _hasPath = result.Status == NavigationHierarchyStatus.Success;
                if (_hasPath && _compareAStar) StartBaseline(grid);
                Describe();
            }

            if (_baselinePending && _baselineJob.IsCompleted)
            {
                _baselineJob.Complete();
                _baselinePending = false;
                if (_hasPath && _graph != null && _graph.IsCurrent(grid) && grid == _queryGrid)
                {
                    _baselineMs = Elapsed(_baselineStart);
                    _baselineResult = _baseline.ReadResult();
                    Describe();
                }
            }
            if (_graph == null && !(_query?.Busy ?? false) && !_baselinePending) ClearQuery();
            UpdateBudget();
        }

        private void StartBaseline(NavigationGrid grid)
        {
            int cells = grid.Size.x * grid.Size.y;
            if (NavigationPathScheduler.WorkspaceSize(cells) > Available - Payload + (_baseline?.PayloadBytes ?? 0))
                return;
            if (_baseline == null || _baseline.Capacity < cells)
            {
                _baseline?.Dispose();
                _baseline = new NavigationPathfinder(cells);
            }
            _baselineStart = Stopwatch.GetTimestamp();
            _baselineJob = _baseline.Schedule(grid, _from, _goal);
            _baselinePending = true;
            JobHandle.ScheduleBatchedJobs();
        }

        private void Describe()
        {
            if (_graph == null) return;
            var stats = _graph.Stats;
            Summary = $"{_graph.Mode} | Clusters {stats.Clusters:N0} | Entrances {stats.Entrances:N0} | " +
                $"Portals {stats.Portals:N0} | Edges {stats.Edges:N0} | Crossing {stats.CrossEdges:N0}/{stats.CrossCandidates:N0}\n" +
                $"Build {_buildMs:F2} ms | Graph {_graph.PayloadBytes:N0} B | Build peak {stats.PeakBytes:N0} B | " +
                $"Query buffers {_query?.PayloadBytes ?? 0:N0} B";
            if (_queryGrid == null) return;
            Summary += $"\n{_result.Status} | Path {_result.PathCount:N0} nodes | Cost {_result.Cost:F4} | Query {_queryMs:F2} ms\n" +
                $"Expanded connect/high/refine {_result.ConnectExpanded:N0}/{_result.HighExpanded:N0}/{_result.RefineExpanded:N0} | " +
                $"Fallback {_result.Fallback} ({_result.FallbackMilliseconds:F2} ms)";
            if (_baselineResult.Status == NavigationPathStatus.Success)
            {
                double extra = _result.Cost - _baselineResult.Cost;
                double percent = _baselineResult.Cost > 0 ? 100 * extra / _baselineResult.Cost : 0;
                Summary += $"\nA* {_baselineMs:F2} ms | Cost {_baselineResult.Cost:F4} | Extra {extra:F4} ({percent:F2}%)";
            }
        }

        private void RetireGraph()
        {
            if (_graph == null) return;
            long bytes = _graph.PayloadBytes;
            var handle = _graph.Retire();
            _graph = null;
            if (handle.IsCompleted) handle.Complete();
            else
            {
                _retirement = JobHandle.CombineDependencies(_retirement, handle);
                _retiredBytes += bytes;
            }
        }

        private void CollectRetired()
        {
            if (_retiredBytes == 0 || !_retirement.IsCompleted) return;
            _retirement.Complete();
            _retiredBytes = 0;
        }

        private void ClearQuery()
        {
            _hasPath = false;
            _queryGrid = null;
            _query?.Dispose();
            _query = null;
            _baseline?.Dispose();
            _baseline = null;
            _baselineResult = default;
        }

        private long Payload => (_builder?.PayloadBytes ?? 0) + (_graph?.PayloadBytes ?? 0) + _retiredBytes +
            (_query?.PayloadBytes ?? 0) + (_baseline?.PayloadBytes ?? 0);

        private void UpdateBudget()
        {
            if (_authoring == null || _authoring.gameObject != gameObject) return;
            _authoring.HierarchyPayloadBytes = Payload;
            _authoring.ObservePayload(_authoring.LiveGridBytes + _authoring.BatchPayloadBytes + _authoring.FlowPayloadBytes + Payload);
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
            if (_baselinePending) _baselineJob.Complete();
            _baselinePending = false;
            ClearQuery();
            _builder?.Dispose();
            _builder = null;
            RetireGraph();
            _retirement.Complete();
            _retiredBytes = 0;
            UpdateBudget();
        }

#if UNITY_EDITOR
        private void EditorPump()
        {
            if (!Application.IsPlaying(gameObject) && (_builder != null || _graph != null || _retiredBytes != 0))
                Pump();
        }
#endif

        private void Update()
        {
            if (Application.IsPlaying(gameObject) && (_builder != null || _graph != null || _retiredBytes != 0))
                Pump();
        }

        private void OnDrawGizmosSelected()
        {
            if (_layers == 0 || _authoring == null) return;
            var grid = _authoring.Grid;
            if (_graph == null || !_graph.TryGetView(grid, out var view)) return;
            using var span = DrawMarker.Auto();
            DrawGraph(grid, view);
            if (Has(Layers.AbstractPath | Layers.RefinedPath | Layers.AStar))
            {
                Gizmos.color = Color.cyan;
                if (_start != null) Gizmos.DrawWireSphere(_start.position + Vector3.up * 0.25f, grid.CellSize * 0.22f);
                Gizmos.color = Color.yellow;
                if (_destination != null) Gizmos.DrawWireSphere(_destination.position + Vector3.up * 0.25f, grid.CellSize * 0.24f);
            }
            if (!_hasPath || grid != _queryGrid) return;
            if (Has(Layers.AbstractPath)) DrawPath(grid, _result.AbstractCount, 0, new Color(1f, 0.5f, 0.05f));
            if (Has(Layers.RefinedPath)) DrawPath(grid, _result.PathCount, 1, Color.cyan);
            if (Has(Layers.AStar) && _compareAStar && _baselineResult.Status == NavigationPathStatus.Success)
                DrawPath(grid, _baselineResult.PathCount, 2, new Color(1f, 0.3f, 0.9f));
        }

        private void DrawGraph(NavigationGrid grid, NavigationHierarchyView view)
        {
            if (!Has(Layers.Clusters | Layers.Portals | Layers.Crossing | Layers.IntraCluster)) return;
            Vector3 focus = _focus != null ? _focus.position : _authoring.transform.position +
                new Vector3(grid.Size.x * grid.CellSize / 2, 0, grid.Size.y * grid.CellSize / 2);
            float2 center = new float2(focus.x, focus.z);
            float2 span = math.max(1, _radius);
            float2 min = (center - span - grid.Origin) / grid.CellSize;
            float2 max = (center + span - grid.Origin) / grid.CellSize;
            if (!math.all(math.isfinite(min) & math.isfinite(max)) || math.any(max < 0 | min >= (float2)grid.Size)) return;
            int2 first = (int2)math.clamp(math.floor(min), 0, (float2)(grid.Size - 1)) / view.Partition.ClusterSize;
            int2 last = (int2)math.clamp(math.floor(max), 0, (float2)(grid.Size - 1)) / view.Partition.ClusterSize;
            int clusters = Mathf.Clamp(_clusterBudget, 1, 512), portals = Mathf.Clamp(_portalBudget, 1, 2048);
            int edges = Mathf.Clamp(_edgeBudget, 1, 2048), inspections = edges * 8;
            for (int z = first.y; z <= last.y && clusters > 0; z++)
            for (int x = first.x; x <= last.x && clusters > 0; x++)
            {
                clusters--;
                int id = z * view.Partition.Counts.x + x;
                var region = view.Partition.Region(id);
                if (Has(Layers.Clusters))
                {
                    float2 middle = grid.Origin + ((float2)region.Origin + (float2)region.Size * 0.5f) * grid.CellSize;
                    Gizmos.color = new Color(0.82f, 0.63f, 0.25f);
                    Gizmos.DrawWireCube(new Vector3(middle.x, _authoring.transform.position.y + 0.16f, middle.y),
                        new Vector3(region.Size.x * grid.CellSize, 0, region.Size.y * grid.CellSize));
                }
                var c = view.Clusters[id];
                for (int p = c.PortalStart; p < c.PortalStart + c.PortalCount && portals > 0; p++, portals--)
                {
                    Vector3 point = Position(grid, view.Portals[p], 0.16f);
                    if (Has(Layers.Portals))
                    {
                        Gizmos.color = new Color(0.7f, 1f, 0.28f);
                        Gizmos.DrawSphere(point, grid.CellSize * 0.13f);
                    }
                    if (!Has(Layers.Crossing | Layers.IntraCluster)) continue;
                    for (int j = view.Offsets[p]; j < view.Offsets[p + 1] && edges > 0 && inspections > 0; j++, inspections--)
                    {
                        var edge = view.Edges[j];
                        if (!Has(edge.Crossing ? Layers.Crossing : Layers.IntraCluster)) continue;
                        Gizmos.color = edge.Crossing ? new Color(0.62f, 0.95f, 0.3f) : new Color(0.58f, 0.58f, 0.85f, 0.55f);
                        var end = Position(grid, view.Portals[edge.To], 0.16f);
                        Gizmos.DrawLine(point, end);
                        if (edge.Crossing)
                            Gizmos.DrawSphere(Vector3.Lerp(point, end, 0.75f), grid.CellSize * 0.055f);
                        edges--;
                    }
                }
            }
        }

        private void DrawPath(NavigationGrid grid, int count, int kind, Color color)
        {
            int resolve(int index) => kind == 0 ? _query.AbstractNode(index) : kind == 1 ? _query.PathNode(index) : _baseline.PathNode(index);
            Gizmos.color = color;
            float rise = 0.26f + 0.09f * kind;
            int limit = Math.Min(count, Mathf.Clamp(_pathBudget, 1, 2048) + 1);
            for (int i = 1; i < limit; i++)
                Gizmos.DrawLine(Position(grid, resolve(i - 1), rise), Position(grid, resolve(i), rise));
        }

        private static Vector3 Position(NavigationGrid grid, int index, float rise)
        {
            var cell = new int2(index % grid.Size.x, index / grid.Size.x);
            grid.TryGetNode(cell, out var node);
            float2 center = grid.Center(cell);
            return new Vector3(center.x, node.Height + rise, center.y);
        }

        private bool Has(Layers layer) => (_layers & layer) != 0;
    }
}
