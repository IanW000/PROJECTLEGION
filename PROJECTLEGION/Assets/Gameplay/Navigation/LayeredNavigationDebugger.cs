using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProjectLegion.Gameplay
{
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class LayeredNavigationDebugger : MonoBehaviour
    {
        [SerializeField] private LayerNavigationSettings _settings = LayerNavigationSettings.Default;
        [SerializeField] private LayerNavigationLink[] _links = Array.Empty<LayerNavigationLink>();
        [SerializeField] private Transform _groundStart, _groundGoal, _bridgeStart, _bridgeGoal, _inspect;
        [SerializeField, Min(1)] private int _budgetMiB = 96;
        [SerializeField, Min(-1)] private int _layer = -1;
        [SerializeField, Range(1, 4096)] private int _drawBudget = 512;
        [SerializeField] private bool _showNodes = true, _showSurfaceEdges, _showSpecialEdges = true, _showPaths = true;
        private LayeredNavigationSnapshot _graph;
        private JobHandle _retirement;
        private long _retiredBytes;
        private readonly List<Vector3> _groundRoute = new List<Vector3>();
        private readonly List<Vector3> _bridgeRoute = new List<Vector3>();
        private readonly List<int> _specialEdges = new List<int>();
        private static readonly ProfilerMarker DrawMarker = new ProfilerMarker("Navigation.Layered.DebugDraw");
        public string Summary { get; private set; } = "Build the multilayer snapshot.";
        public string Inspection { get; private set; } = "";
        private long Budget => (long)Mathf.Max(1, _budgetMiB) * 1024 * 1024;
        private long LiveBytes => (_graph?.PayloadBytes ?? 0) + _retiredBytes;

        public void Build()
        {
            CollectRetired();
            if (_retiredBytes != 0)
            {
                Summary = "A previous snapshot is retiring. Retry once readers finish.";
                return;
            }

            long begin = Stopwatch.GetTimestamp();
            try
            {
                Physics.SyncTransforms();
                var builder = new LayeredNavigationBuilder(gameObject.scene.GetPhysicsScene(), transform.position,
                    _settings, Budget - LiveBytes);
                var next = builder.Build((LayerNavigationLink[])_links.Clone(), out var stats);
                long peak = LiveBytes + next.PayloadBytes + stats.TemporaryPayload;
                Clear();
                _graph = next;
                var view = next.View();
                for (int i = 0; i < view.Edges.Length && _specialEdges.Count < 4096; i++)
                    if (view.Edges[i].Type != LayerConnectionType.Surface) _specialEdges.Add(i);
                Summary = $"Snapshot {next.Generation} | Nodes {next.NodeCount:N0} | Edges {next.EdgeCount:N0}\n" +
                    $"Build {Elapsed(begin):F3} ms | Rays {stats.Rays:N0} | Capsules {stats.Capsules:N0}\n" +
                    $"Extract / connect / pack {stats.ExtractionMs:F3}/{stats.ConnectionsMs:F3}/{stats.PackMs:F3} ms\n" +
                    $"Native {next.PayloadBytes:N0} B | Estimated staging overlap {peak:N0} B\n" +
                    $"Blocked candidates {stats.RejectedCandidates:N0} | Continuity rejections {stats.ContinuityRejections:N0}\n" +
                    (next.GeometricHeuristic ? "A* Euclidean heuristic" : "A* zero heuristic for this snapshot");
                Inspect();
            }
            catch (ArgumentException error)
            {
                Summary = "Build rejected. " + error.Message;
            }
        }

        public void RunPaths()
        {
            _groundRoute.Clear();
            _bridgeRoute.Clear();
            if (_graph == null) { Summary = "Build the multilayer snapshot."; return; }
            if (LiveBytes + LayeredSearchWorkspace.Budget(_graph.NodeCount) > Budget)
            { Summary = "Path workspace exceeds the navigation budget."; return; }

            using var search = new LayeredSearchWorkspace(_graph.NodeCount);
            string ground = Route(search, _groundStart, _groundGoal, _groundRoute);
            string bridge = Route(search, _bridgeStart, _bridgeGoal, _bridgeRoute);
            Summary = $"Snapshot {_graph.Generation} | Ground {ground}\nBridge {bridge}";
            Inspect();
        }

        private string Route(LayeredSearchWorkspace search, Transform start, Transform goal, List<Vector3> output)
        {
            if (start == null || goal == null || !_graph.Resolve(start.position, _settings.EndpointTolerance, out var from) ||
                !_graph.Resolve(goal.position, _settings.EndpointTolerance, out var to)) return "invalid endpoint";
            long begin = Stopwatch.GetTimestamp();
            var result = search.Search(_graph, from, to);
            var graph = _graph.View();
            for (int i = 0; i < Math.Min(result.PathCount, _drawBudget + 1); i++)
                output.Add(graph.Nodes[search.Node(i)].Position);
            return $"{result.Status}, {result.PathCount} nodes, cost {result.Cost:F3}, {Elapsed(begin):F3} ms";
        }

        public void Inspect()
        {
            Inspection = "";
            if (_graph == null || _inspect == null ||
                !_graph.Resolve(_inspect.position, _settings.EndpointTolerance, out var found)) return;
            var view = _graph.View();
            var node = view.Nodes[found.Id];
            Inspection = $"Node {found.Id} @ generation {found.Generation} | {node.Position}";
            int start = view.Outgoing[found.Id];
            for (int i = start; i < Math.Min(view.Outgoing[found.Id + 1], start + 16); i++)
            {
                var edge = view.Edges[i];
                Inspection += $"\nEdge {i} → node {edge.To} | {edge.Type} | cost {edge.Cost:F3} | owner {edge.Owner}";
            }
        }

        public void Clear()
        {
            if (_graph != null)
            {
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
            _groundRoute.Clear();
            _bridgeRoute.Clear();
            Inspection = "";
            _specialEdges.Clear();
            Summary = "Snapshot cleared.";
        }

        private void CollectRetired()
        {
            if (_retiredBytes == 0 || !_retirement.IsCompleted) return;
            _retirement.Complete();
            _retiredBytes = 0;
        }

        private void OnDisable()
        {
            Clear();
            _retirement.Complete();
            _retiredBytes = 0;
        }

        private static double Elapsed(long start) => (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;

        private void OnDrawGizmosSelected()
        {
            if (_graph == null || !_graph.IsCreated) return;
            using var scope = DrawMarker.Auto();
            var graph = _graph.View();
            int budget = Mathf.Clamp(_drawBudget, 1, 4096);
            int nodeStride = Mathf.Max(1, (graph.Nodes.Length + budget - 1) / budget);
            if (_showNodes)
                for (int i = 0; i < graph.Nodes.Length; i += nodeStride)
                {
                    int layer = _graph.LayerIndex(i);
                    if (_layer >= 0 && layer != _layer) continue;
                    var node = graph.Nodes[i];
                    Gizmos.color = !node.Walkable ? Color.red : layer == 0 ? new Color(.4f, .9f, .35f) : new Color(.3f, .7f, 1f);
                    Gizmos.DrawSphere((Vector3)node.Position + Vector3.up * .06f, .085f);
                }

            int edgeStride = Mathf.Max(1, (graph.Edges.Length + budget - 1) / budget);
            if (_showSurfaceEdges)
                for (int i = 0; i < graph.Edges.Length; i += edgeStride)
                    if (graph.Edges[i].Type == LayerConnectionType.Surface) DrawEdge(graph, graph.Edges[i], false);
            if (_showSpecialEdges)
                for (int i = 0; i < _specialEdges.Count && i < budget; i++)
                    DrawEdge(graph, graph.Edges[_specialEdges[i]], true);
            if (_showPaths)
            {
                DrawPath(_groundRoute, Color.green);
                DrawPath(_bridgeRoute, Color.cyan);
            }
        }

        private void DrawEdge(LayerGraphView graph, LayerEdge edge, bool special)
        {
            if (_layer >= 0 && _graph.LayerIndex(edge.From) != _layer) return;
            Vector3 a = (Vector3)graph.Nodes[edge.From].Position + Vector3.up * .1f;
            Vector3 b = (Vector3)graph.Nodes[edge.To].Position + Vector3.up * .1f;
            Gizmos.color = special ? new Color(1f, .58f, .1f) : new Color(.55f, .7f, .85f, .55f);
            Gizmos.DrawLine(a, b);
            if (!special) return;
            Vector3 direction = (b - a).normalized;
            Vector3 wing = Vector3.Cross(direction, Vector3.up).normalized * .15f;
            if (wing == Vector3.zero) wing = Vector3.right * .15f;
            Vector3 basePoint = b - direction * .3f;
            Gizmos.DrawLine(b, basePoint + wing);
            Gizmos.DrawLine(b, basePoint - wing);
        }

        private static void DrawPath(List<Vector3> route, Color color)
        {
            Gizmos.color = color;
            for (int i = 1; i < route.Count; i++)
                Gizmos.DrawLine(route[i - 1] + Vector3.up * .25f, route[i] + Vector3.up * .25f);
        }
    }
}
