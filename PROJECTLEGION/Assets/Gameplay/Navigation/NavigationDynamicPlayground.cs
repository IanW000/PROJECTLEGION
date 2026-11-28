using Unity.Mathematics;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [DisallowMultipleComponent]
    public sealed class NavigationDynamicPlayground : MonoBehaviour
    {
        [SerializeField] private NavigationTopologyController _topology;
        [SerializeField] private NavigationDynamicElement _bridge, _obstacle, _shortcut, _secondObstacle;
        [SerializeField] private Transform _groundFrom, _groundTo, _deckFrom, _deckTo;
        [SerializeField, Range(1, 2048)] private int _drawBudget = 512;
        private NavigationPathScheduler _scheduler;

        private void Start()
        {
            if (_topology == null) return;
            _topology.CaptureEdgePreview = true;
            _topology.BuildFull();
        }

        private void Update()
        {
            SyncGeometry(_bridge);
            SyncGeometry(_obstacle);
            SyncGeometry(_secondObstacle);
            if (_scheduler == null) return;
            _scheduler.Tick(2, 2, 256);
            while (_scheduler.TryTake(out var result)) _scheduler.Release(result.Handle);
        }

        private static void SyncGeometry(NavigationDynamicElement element)
        {
            if (element == null) return;
            foreach (var collider in element.Colliders)
                if (collider != null && collider.TryGetComponent<Renderer>(out var renderer) &&
                    renderer.enabled != collider.enabled)
                    renderer.enabled = collider.enabled;
        }

        private void OnGUI()
        {
            if (_topology == null) return;
            GUILayout.BeginArea(new Rect(12, 12, 410, 365), GUI.skin.box);
            GUILayout.Label($"Navigation | {_topology.State} | Revision {_topology.Revision} | Snapshot {_topology.SnapshotGeneration}");
            GUILayout.Label(_topology.Diagnostic);
            var dirty = _topology.Dirty;
            GUILayout.Label($"Dirty sample / repair {dirty?.SampleCount ?? 0} / {dirty?.RepairCount ?? 0}");
            GUILayout.Label($"Retained {_topology.Retained} | Revoked {_topology.Revoked} | Merged {_topology.Coalesced}");
            GUILayout.Label($"Last {_topology.AppliedStrategy} | Main thread {_topology.LastPumpMs:F2} ms | Peak {_topology.PeakBytes:N0} B");
            if (_scheduler != null) GUILayout.Label($"Search: active {_scheduler.Active} | cancelled {_scheduler.Cancelled}");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Destroy bridge")) SetBridge(false);
            if (GUILayout.Button("Restore bridge")) SetBridge(true);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Place obstacle")) SetObstacle(true);
            if (GUILayout.Button("Remove obstacle")) SetObstacle(false);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Enable shortcut")) SetShortcut(true);
            if (GUILayout.Button("Disable shortcut")) SetShortcut(false);
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Batch: place both obstacles"))
            {
                SetObstacle(true);
                _topology.SetPresent(_secondObstacle, true);
            }
            if (GUILayout.Button("Clear both obstacles"))
            {
                SetObstacle(false);
                _topology.SetPresent(_secondObstacle, false);
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Track paths")) TrackPaths();
            if (GUILayout.Button("Search then break bridge")) SearchThenBreak();
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Retry full rebuild")) _topology.Retry();
            if (GUILayout.Button("Roll back owned edits")) _topology.Rollback();
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        public void SetBridge(bool present) => _topology.SetPresent(_bridge, present);
        public void SetObstacle(bool present) => _topology.SetPresent(_obstacle, present);
        public void SetShortcut(bool present) => _topology.SetPresent(_shortcut, present);

        public void TrackPaths()
        {
            var graph = _topology.Layered;
            if (graph == null || LayeredSearchWorkspace.Budget(graph.NodeCount) > 2L * 1024 * 1024) return;
            if (!_topology.TryReserveDerivedPayload(LayeredSearchWorkspace.Budget(graph.NodeCount), out var lease))
                return;
            using (lease)
            {
                _topology.ClearRoutes();
                using var query = new LayeredSearchWorkspace(graph.NodeCount);
                Track(graph, query, _groundFrom, _groundTo);
                Track(graph, query, _deckFrom, _deckTo);
            }
        }

        private void Track(LayeredNavigationSnapshot graph, LayeredSearchWorkspace workspace, Transform from, Transform goal)
        {
            if (from == null || goal == null || !graph.Resolve(from.position, .25f, out var start) ||
                !graph.Resolve(goal.position, .25f, out var end)) return;
            var result = workspace.Search(graph, start, end);
            _topology.TrackPath(workspace, result, _topology.Revision, graph.Generation, true);
        }

        private void SearchThenBreak()
        {
            var graph = _topology.Layered;
            if (graph == null || _deckFrom == null || _deckTo == null ||
                !graph.Resolve(_deckFrom.position, .25f, out var from) || !graph.Resolve(_deckTo.position, .25f, out var to))
                return;
            if (_scheduler == null)
            {
                _scheduler = new NavigationPathScheduler(2, 256 * 1024, 8, 4, 16);
                _topology.BindScheduler(_scheduler);
            }
            _scheduler.SubmitLayered(from, to, out _);
            _scheduler.Tick(2, 2, 64);
            SetBridge(false);
        }

        private void OnDisable()
        {
            if (_scheduler == null) return;
            if (_topology != null) _topology.UnbindScheduler(_scheduler);
            _scheduler.Dispose();
            _scheduler = null;
        }

        private void OnDrawGizmos()
        {
            if (_topology == null) return;
            int budget = Mathf.Clamp(_drawBudget, 1, 2048);
            var dirty = _topology.Dirty;
            if (dirty != null)
            {
                int width = _topology.GridWidth;
                float cell = _topology.CellSize;
                int stride = Mathf.Max(1, (dirty.Sample.Length + budget - 1) / budget);
                for (int i = 0; i < dirty.Sample.Length; i += stride)
                {
                    if (!dirty.Repair[i]) continue;
                    Gizmos.color = dirty.Sample[i] ? new Color(1f, .18f, .19f, .29f) : new Color(1f, .66f, .12f, .25f);
                    var center = _topology.transform.position +
                        new Vector3((i % width + .5f) * cell, .1f, (i / width + .5f) * cell);
                    Gizmos.DrawCube(center, new Vector3(cell * .92f, .03f, cell * .92f));
                }
            }

            foreach (var path in _topology.Routes)
            {
                Gizmos.color = _topology.State != TopologyState.Ready ? Color.gray :
                    path.Status == RouteValidity.Invalid ? Color.red :
                    path.Status == RouteValidity.ValidOptimal ? Color.green : Color.cyan;
                for (int i = 1; i < math.min(path.Count, budget); i++)
                    Gizmos.DrawLine((Vector3)path.OriginalPosition(i - 1) + Vector3.up * .2f,
                        (Vector3)path.OriginalPosition(i) + Vector3.up * .2f);
            }
            foreach (var edge in _topology.EdgePreview)
            {
                Gizmos.color = edge.Added ? new Color(.54f, .95f, .26f) : new Color(1f, .35f, .16f);
                Gizmos.DrawLine((Vector3)edge.From + Vector3.up * .06f, (Vector3)edge.To + Vector3.up * .06f);
            }
            DrawLink(_bridge);
            DrawLink(_shortcut);
        }

        private static void DrawLink(NavigationDynamicElement element)
        {
            if (element == null) return;
            foreach (var link in element.Links)
            {
                if (link == null || link.Source == null || link.Destination == null) continue;
                var start = link.Source.position + Vector3.up * .28f;
                var end = link.Destination.position + Vector3.up * .28f;
                var direction = (end - start).normalized;
                Gizmos.color = link.isActiveAndEnabled ? Color.magenta : new Color(.9f, .34f, .32f, .55f);
                Gizmos.DrawLine(start, end);
                var side = Vector3.Cross(direction, Vector3.up) * .23f;
                Gizmos.DrawLine(end, end - direction * .6f + side);
                Gizmos.DrawLine(end, end - direction * .6f - side);
            }
        }
    }
}
