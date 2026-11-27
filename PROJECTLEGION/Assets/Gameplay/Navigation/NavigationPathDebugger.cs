using System.Diagnostics;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class NavigationPathDebugger : MonoBehaviour
    {
        [SerializeField] private NavigationGridAuthoring _authoring;
        [SerializeField] private Transform _start, _destination;
        [SerializeField] private bool _showPath = true, _recordExplored = true, _showExplored = true;
        [SerializeField, Range(1, 4096)] private int _pathDrawBudget = 512;
        [SerializeField, Range(1, 4096)] private int _explorationBudget = 512;

        private NavigationPathfinder _workspace;
        private NavigationGrid _searchedGrid;
        private NavigationPathResult _result;
        private int2 _startCell, _destinationCell;
        private double _milliseconds;

        private static readonly Color StartColor = new Color(0.2f, 1f, 1f);
        private static readonly Color DestinationColor = new Color(1f, 0.32f, 0.9f);
        private static readonly Color PathColor = new Color(1f, 0.8f, 0.12f);
        private static readonly ProfilerMarker DrawMarkerScope = new ProfilerMarker("NavigationPathDebugger.Draw");
#if UNITY_EDITOR
        private readonly Vector3[] _line = new Vector3[2];
#endif

        private bool HasResult => _searchedGrid != null && _authoring != null && _authoring.Grid == _searchedGrid;

        public string SearchSummary => !HasResult
            ? "Build the navigation grid, then run a path search."
            : $"{_result.Status} | Cost {_result.Cost:F3} | Length {_result.Length:F3} | Nodes {_result.PathCount}\n" +
              $"Expanded {_result.Expanded} | Recorded {_result.Recorded} | Reopened {_result.Reopened}\n" +
              $"Push {_result.Pushes} | Pop {_result.Pops} | Decrease {_result.Decreases} | {_milliseconds:F3} ms";

        [ContextMenu("Run Path Search")]
        public void RunSearch()
        {
            ClearSearch();
            if (_authoring == null || _authoring.Grid == null || _start == null || _destination == null)
                return;

            NavigationGrid grid = _authoring.Grid;
            int capacity = grid.Size.x * grid.Size.y;
            int explorationCapacity = Mathf.Clamp(_explorationBudget, 1, 4096);
            if (_workspace == null || _workspace.Capacity < capacity ||
                _workspace.ExplorationCapacity < explorationCapacity)
            {
                Release();
                _workspace = new NavigationPathfinder(capacity, explorationCapacity);
            }

            _startCell = Resolve(grid, _start.position);
            _destinationCell = Resolve(grid, _destination.position);
            long begin = Stopwatch.GetTimestamp();
            _result = _workspace.Search(grid, _startCell, _destinationCell, _recordExplored);
            _milliseconds = (Stopwatch.GetTimestamp() - begin) * 1000d / Stopwatch.Frequency;
            _searchedGrid = grid;
        }

        private static int2 Resolve(NavigationGrid grid, Vector3 point) =>
            grid.TryWorldToCell(new float2(point.x, point.z), out var cell) ? cell : new int2(-1);

        [ContextMenu("Clear Path")]
        public void ClearSearch()
        {
            _searchedGrid = null;
            _result = default;
        }

        private void Release()
        {
            ClearSearch();
            _workspace?.Dispose();
            _workspace = null;
        }

        private void OnDisable() => Release();
        private void OnDestroy() => Release();

        private void OnDrawGizmos()
        {
            if (!_showPath && !_showExplored)
                return;

            using var scope = DrawMarkerScope.Auto();
            if (HasResult && _showExplored)
            {
                Gizmos.color = new Color(0.45f, 0.65f, 1f, 0.55f);
                int count = Mathf.Min(_result.Recorded, Mathf.Clamp(_explorationBudget, 1, 4096));
                for (int i = 0; i < count; i++)
                    Gizmos.DrawCube(NodePosition(_workspace.ExploredNode(i)), Vector3.one * (_searchedGrid.CellSize * 0.15f));
            }

            if (!_showPath)
                return;

            if (HasResult)
            {
                int segments = Mathf.Min(_result.PathCount - 1, Mathf.Clamp(_pathDrawBudget, 1, 4096));
                for (int i = 0; i < segments; i++)
                {
                    Vector3 from = NodePosition(_workspace.PathNode(i));
                    Vector3 to = NodePosition(_workspace.PathNode(i + 1));
#if UNITY_EDITOR
                    UnityEditor.Handles.color = PathColor;
                    _line[0] = from;
                    _line[1] = to;
                    UnityEditor.Handles.DrawAAPolyLine(5f, _line);
#else
                    Gizmos.color = PathColor;
                    Gizmos.DrawLine(from, to);
#endif
                }
            }

            DrawMarker(_start, _startCell, StartColor, "START");
            DrawMarker(_destination, _destinationCell, DestinationColor, "DESTINATION");
        }

        private Vector3 NodePosition(int index) =>
            NodePosition(new int2(index % _searchedGrid.Size.x, index / _searchedGrid.Size.x));

        private Vector3 NodePosition(int2 cell)
        {
            _searchedGrid.TryGetNode(cell, out var node);
            float2 xz = _searchedGrid.Center(cell);
            return new Vector3(xz.x, node.Height + 0.15f, xz.y);
        }

        private void DrawMarker(Transform marker, int2 cell, Color color, string label)
        {
            if (marker == null)
                return;
            Vector3 position = marker.position;
            if (HasResult && _searchedGrid.TryGetNode(cell, out var node) && node.HasSurface)
                position = NodePosition(cell);
            Gizmos.color = color;
            Gizmos.DrawWireSphere(position, 0.24f);
            Gizmos.DrawLine(position, position + Vector3.up * 0.55f);
#if UNITY_EDITOR
            UnityEditor.Handles.Label(position + Vector3.up * 0.6f, label);
#endif
        }
    }
}
