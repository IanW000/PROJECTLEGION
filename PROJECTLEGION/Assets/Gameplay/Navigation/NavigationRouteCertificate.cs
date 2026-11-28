using System;
using Unity.Mathematics;

namespace ProjectLegion.Gameplay
{
    internal enum RouteValidity { Invalid, ValidUnverified, ValidOptimal }

    internal sealed class NavigationRouteCertificate
    {
        private readonly LayerNode[] _nodes;
        private readonly LayerEdge[] _edges;
        private readonly LayerNodeHandle[] _currentNodes;
        private readonly LayerEdgeHandle[] _currentEdges;
        internal RouteValidity Status { get; private set; }
        internal long Generation { get; private set; }
        internal int Count => _nodes.Length;
        internal long PayloadBytes => 96L * _nodes.Length;

        internal NavigationRouteCertificate(LayeredNavigationSnapshot snapshot, LayeredSearchWorkspace workspace,
            NavigationPathResult path, bool provenOptimal)
        {
            if (path.Status != NavigationPathStatus.Success) throw new ArgumentException("Route must be complete.");
            _nodes = new LayerNode[path.PathCount];
            _edges = new LayerEdge[path.PathCount - 1];
            _currentNodes = new LayerNodeHandle[_nodes.Length];
            _currentEdges = new LayerEdgeHandle[_edges.Length];
            var view = snapshot.View();
            for (int i = 0; i < _nodes.Length; i++)
            {
                if (!workspace.TryNode(i, out var node) || !snapshot.Matches(node))
                    throw new ArgumentException("Route belongs to another snapshot.");
                _nodes[i] = view.Nodes[node.Id];
                _currentNodes[i] = node;
                if (i == _edges.Length) continue;
                if (!workspace.TryStep(i, out var edge) || !snapshot.TryGetEdge(edge, out _edges[i]))
                    throw new ArgumentException("Route edge belongs to another snapshot.");
                _currentEdges[i] = edge;
            }
            Generation = snapshot.Generation;
            Status = provenOptimal ? RouteValidity.ValidOptimal : RouteValidity.ValidUnverified;
        }

        internal RouteValidity Revalidate(LayeredNavigationSnapshot next, bool noImprovement)
        {
            if (Status == RouteValidity.Invalid) return Status;
            bool optimal = Status == RouteValidity.ValidOptimal && noImprovement;
            Status = RouteValidity.Invalid;
            Generation = 0;
            var graph = next.View();
            for (int i = 0; i < _nodes.Length; i++)
            {
                var signature = _nodes[i];
                if (signature.SurfaceKey <= 0) return Status;
                int match = next.ResolveSurface(signature.Position, signature.SurfaceKey);
                if (match < 0) return Status;
                _currentNodes[i] = new LayerNodeHandle(next.Generation, match);
            }

            for (int i = 0; i < _edges.Length; i++)
            {
                var old = _edges[i];
                int from = _currentNodes[i].Id, to = _currentNodes[i + 1].Id;
                int match = -1;
                for (int edge = graph.Outgoing[from]; edge < graph.Outgoing[from + 1]; edge++)
                {
                    var now = graph.Edges[edge];
                    if (now.To != to || now.Type != old.Type || now.Owner != old.Owner || now.StableKey != old.StableKey)
                        continue;
                    if (match >= 0) return Status;
                    match = edge;
                    optimal &= now.Cost == old.Cost;
                }
                if (match < 0) return Status;
                _currentEdges[i] = new LayerEdgeHandle(next.Generation, match);
            }

            Status = optimal ? RouteValidity.ValidOptimal : RouteValidity.ValidUnverified;
            Generation = next.Generation;
            return Status;
        }

        internal bool TryNode(long currentGeneration, int index, out LayerNodeHandle node)
        {
            node = default;
            if (Status == RouteValidity.Invalid || Generation != currentGeneration || (uint)index >= (uint)_nodes.Length)
                return false;
            node = _currentNodes[index];
            return true;
        }

        internal bool TryEdge(long currentGeneration, int index, out LayerEdgeHandle edge)
        {
            edge = default;
            if (Status == RouteValidity.Invalid || Generation != currentGeneration || (uint)index >= (uint)_edges.Length)
                return false;
            edge = _currentEdges[index];
            return true;
        }

        internal float3 OriginalPosition(int index) => _nodes[index].Position;
    }
}
