using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ProjectLegion.Gameplay
{
    public enum TopologyState { Ready, Updating, Blocked }
    public enum TopologyStrategy { Full, LinkOnly, Partial }

    // This owner is the gate for all runtime consumers. Borrowed views are scoped to a
    // revision and acquire/schedule/register must happen on the same owner thread.
    [DisallowMultipleComponent]
    public sealed class NavigationTopologyController : MonoBehaviour
    {
        [SerializeField] private bool _layered = true;
        [SerializeField] private LayerNavigationSettings _settings = LayerNavigationSettings.Default;
        [SerializeField] private LayerNavigationLink[] _links = Array.Empty<LayerNavigationLink>();
        [SerializeField] private NavigationDynamicElement[] _elements = Array.Empty<NavigationDynamicElement>();
        [SerializeField] private TopologyStrategy _strategy = TopologyStrategy.Partial;
        [SerializeField, Min(1)] private int _budgetMiB = 96;

        private readonly SortedDictionary<int, Edit> _queue = new SortedDictionary<int, Edit>();
        private readonly Dictionary<Collider, bool> _rollbackColliders = new Dictionary<Collider, bool>();
        private readonly Dictionary<LayerNavigationLink, bool> _rollbackLinks = new Dictionary<LayerNavigationLink, bool>();
        private readonly List<NavigationRouteCertificate> _routes = new List<NavigationRouteCertificate>();
        private readonly List<NavigationPathScheduler> _schedulers = new List<NavigationPathScheduler>();
        private readonly List<GameObject> _roots = new List<GameObject>();
        private readonly List<Collider> _colliders = new List<Collider>();
        private readonly List<Collider> _rootColliders = new List<Collider>();
        private readonly List<(float3 From, float3 To, bool Added)> _edgePreview =
            new List<(float3 From, float3 To, bool Added)>();
        private LayeredNavigationSnapshot _graph;
        private NavigationGrid _single;
        private LayeredNavigationBuilder.SampleCache _cache;
        private Dictionary<Collider, ColliderProof> _proof;
        private LayerNavigationSettings _builtSettings;
        private float3 _builtOrigin;
        private bool _builtLayered;
        private JobHandle _retired;
        private long _retiredBytes, _retiredStart, _waitStart;
        private bool _forceFull, _rollbackRequested, _physicsApplied;
        private long _routeBytes;
        private long _externalBytes;

        public TopologyState State { get; private set; } = TopologyState.Blocked;
        public long Revision { get; private set; }
        public long SnapshotGeneration => _graph?.Generation ?? _single?.Generation ?? 0;
        public string Diagnostic { get; private set; } = "No published navigation snapshot.";
        public TopologyStrategy AppliedStrategy { get; private set; }
        public int Coalesced { get; private set; }
        public int Revoked { get; private set; }
        public int Retained { get; private set; }
        public int Published { get; private set; }
        public int Failures { get; private set; }
        public double LastReaderWaitMs { get; private set; }
        public double LastRetirementMs { get; private set; }
        public double LastDirtyMs { get; private set; }
        public double LastSyncMs { get; private set; }
        public double LastPublishMs { get; private set; }
        public double LastRevalidateMs { get; private set; }
        public double LastPumpMs { get; private set; }
        public long PeakBytes { get; private set; }
        internal LayerSamplingStats LastSampling { get; private set; }
        internal NavigationDirtyColumns Dirty { get; private set; }
        internal IReadOnlyList<NavigationRouteCertificate> Routes => _routes;
        internal IReadOnlyList<(float3 From, float3 To, bool Added)> EdgePreview => _edgePreview;
        internal bool CaptureEdgePreview { get; set; }
        internal float CellSize => _settings.Grid.CellSize;
        internal int GridWidth => _settings.Grid.Size.x;
        internal LayeredNavigationSnapshot Layered => State == TopologyState.Ready ? _graph : null;
        internal NavigationGrid Single => State == TopologyState.Ready ? _single : null;
        internal long BudgetOverride { get; set; }
        private long Budget => BudgetOverride > 0 ? BudgetOverride : (long)Mathf.Max(1, _budgetMiB) * 1024 * 1024;
        private long GraphBytes => (_graph?.PayloadBytes ?? 0) + (_single?.PayloadBytes ?? 0);
        private long RetainedBytes => GraphBytes + _retiredBytes + (_cache?.PayloadBytes ?? 0) +
            _routeBytes + 128L * (_proof?.Count ?? 0);

        private struct Edit
        {
            internal NavigationDynamicElement Element;
            internal bool Present;
        }

        private struct ColliderProof : IEquatable<ColliderProof>
        {
            internal Matrix4x4 Transform;
            internal Vector3 Center, Size;
            internal bool Enabled, Active, Trigger;
            internal int Layer, SurfaceKey, Owner;
            internal float Cost;

            public bool Equals(ColliderProof other) => Transform.Equals(other.Transform) && Center.Equals(other.Center) &&
                Size.Equals(other.Size) && Enabled == other.Enabled && Active == other.Active &&
                Trigger == other.Trigger && Layer == other.Layer && SurfaceKey == other.SurfaceKey &&
                Owner == other.Owner && Cost == other.Cost;
        }

        public void BuildFull()
        {
            _forceFull = true;
            Gate();
        }

        public void Retry()
        {
            if (State == TopologyState.Blocked) BuildFull();
        }

        public void Rollback()
        {
            if (State == TopologyState.Ready || !_physicsApplied) return;
            _queue.Clear();
            _rollbackRequested = true;
            BuildFull();
        }

        public bool SetPresent(NavigationDynamicElement element, bool present)
        {
            if (element == null || element.Owner <= 0 || Array.IndexOf(_elements, element) < 0) return false;
            if (_queue.TryGetValue(element.Owner, out var existing))
            {
                if (existing.Element != element) return false;
                Coalesced++;
            }
            else if (State == TopologyState.Updating) Coalesced++;
            _queue[element.Owner] = new Edit { Element = element, Present = present };
            Gate();
            return true;
        }

        private void Gate()
        {
            if (State != TopologyState.Updating) Revision = checked(Revision + 1);
            State = TopologyState.Updating;
            _graph?.GateQueries();
            _single?.GateQueries();
            Diagnostic = "Revision gated while the update is pending.";
            foreach (var scheduler in _schedulers) scheduler.SetLayeredSnapshot(null);
        }

        internal bool IsCurrent(long revision, long generation) => State == TopologyState.Ready &&
            Revision == revision && SnapshotGeneration == generation && generation != 0;

        internal void BindScheduler(NavigationPathScheduler scheduler)
        {
            if (_schedulers.Contains(scheduler)) return;
            _schedulers.Add(scheduler);
            Bind(scheduler);
        }

        internal void UnbindScheduler(NavigationPathScheduler scheduler)
        {
            if (_schedulers.Remove(scheduler)) scheduler.SetLayeredSnapshot(null);
        }

        private void Bind(NavigationPathScheduler scheduler)
        {
            if (Layered != null) scheduler.SetLayeredSnapshot(Layered);
            else scheduler.SetGrid(Single);
        }

        internal NavigationRouteCertificate TrackPath(LayeredSearchWorkspace search, NavigationPathResult result,
            long revision, long generation, bool provenOptimal)
        {
            if (!IsCurrent(revision, generation) || Layered == null || result.Status != NavigationPathStatus.Success ||
                _routes.Count >= 16 || AccountedBytes + LayeredSearchWorkspace.Budget(Layered.NodeCount) +
                96L * result.PathCount > Budget)
                return null;
            var route = new NavigationRouteCertificate(Layered, search, result, provenOptimal);
            _routes.Add(route);
            _routeBytes += route.PayloadBytes;
            return route;
        }

        internal void ClearRoutes()
        {
            _routes.Clear();
            _routeBytes = 0;
        }

        internal bool TryRouteNode(NavigationRouteCertificate route, int index, out LayerNodeHandle node)
        {
            node = default;
            return State == TopologyState.Ready && _routes.Contains(route) &&
                route.TryNode(SnapshotGeneration, index, out node);
        }

        internal bool TryRouteEdge(NavigationRouteCertificate route, int index, out LayerEdgeHandle edge)
        {
            edge = default;
            return State == TopologyState.Ready && _routes.Contains(route) &&
                route.TryEdge(SnapshotGeneration, index, out edge);
        }

        private long SchedulerBytes
        {
            get
            {
                long total = 0;
                foreach (var scheduler in _schedulers) total += scheduler.PayloadBytes;
                return total;
            }
        }

        internal long AccountedBytes => RetainedBytes + SchedulerBytes + _externalBytes;

        // External derived owners reserve their payload before allocation and release after
        // disposing their result arrays and completing their own readers.
        internal bool TryReserveDerivedPayload(long bytes, out IDisposable lease)
        {
            lease = null;
            if (State != TopologyState.Ready || bytes <= 0 || bytes > Budget - AccountedBytes) return false;
            _externalBytes += bytes;
            PeakBytes = Math.Max(PeakBytes, AccountedBytes);
            lease = new PayloadLease(this, bytes);
            return true;
        }

        private sealed class PayloadLease : IDisposable
        {
            private NavigationTopologyController _owner;
            private readonly long _bytes;
            internal PayloadLease(NavigationTopologyController owner, long bytes) { _owner = owner; _bytes = bytes; }
            public void Dispose()
            {
                if (!ReferenceEquals(_owner, null)) _owner._externalBytes -= _bytes;
                _owner = null;
            }
        }

        private void Update() => Pump();

        internal void Pump()
        {
            long begin = Stopwatch.GetTimestamp();
            try
            {
                CollectRetired();
                if (State != TopologyState.Updating) return;
                if (_retiredBytes != 0)
                {
                    if (_waitStart == 0) _waitStart = begin;
                    Diagnostic = "Waiting for the retiring snapshot. Edits remain queued.";
                    return;
                }
                if (_waitStart != 0)
                {
                    LastReaderWaitMs = Ms(_waitStart);
                    _waitStart = 0;
                }
                try { ApplyAndPublish(); }
                catch (Exception error) when (error is ArgumentException || error is InvalidOperationException || error is UnityException)
                {
                    Failures++;
                    State = TopologyState.Blocked;
                    _forceFull = true;
                    Diagnostic = "Blocked. " + error.Message + " Retry or roll back owned changes.";
                }
            }
            finally { LastPumpMs = Ms(begin); }
        }

        private void ApplyAndPublish()
        {
            ValidateOwners();
            long dirtyStart = Stopwatch.GetTimestamp();
            var size = new int2(_settings.Grid.Size.x, _settings.Grid.Size.y);
            long columnCount = (long)size.x * size.y;
            if (math.any(size <= 0) || columnCount > 2_000_000 || columnCount * 2 > Budget - AccountedBytes ||
                !math.isfinite(_settings.Grid.CellSize) || _settings.Grid.CellSize < .01f ||
                !math.isfinite(_settings.Grid.AgentRadius) || _settings.Grid.AgentRadius <= 0)
                throw new ArgumentException("Navigation domain or dirty workspace exceeds its budget.");
            var position = (float3)transform.position;
            bool sameDomain = _settings.Equals(_builtSettings) && position.Equals(_builtOrigin) && _layered == _builtLayered;
            var observed = CaptureProof();
            bool physicsProven = ProofEquals(_proof, observed);
            Dirty = new NavigationDirtyColumns(size, position.xz, _settings.Grid.CellSize, _settings.Grid.AgentRadius);
            bool colliderChanged = false;
            if (_rollbackRequested)
            {
                foreach (var pair in _rollbackColliders) if (pair.Key != null) pair.Key.enabled = pair.Value;
                foreach (var pair in _rollbackLinks) if (pair.Key != null) pair.Key.enabled = pair.Value;
                _rollbackRequested = false;
                colliderChanged = true;
            }
            foreach (var entry in _queue.Values)
            {
                foreach (var collider in entry.Element.Colliders)
                {
                    if (collider.enabled == entry.Present) continue;
                    if (!_rollbackColliders.ContainsKey(collider)) _rollbackColliders.Add(collider, collider.enabled);
                    if (collider.enabled) Dirty.Add(collider.bounds);
                    collider.enabled = entry.Present;
                    colliderChanged = _physicsApplied = true;
                }
                foreach (var link in entry.Element.Links)
                {
                    if (link.enabled == entry.Present) continue;
                    if (!_rollbackLinks.ContainsKey(link)) _rollbackLinks.Add(link, link.enabled);
                    link.enabled = entry.Present;
                    _physicsApplied = true;
                }
            }

            LastDirtyMs = Ms(dirtyStart);
            long syncStart = Stopwatch.GetTimestamp();
            // Refresh transforms even when a third party moved a collider since the last proof.
            Physics.SyncTransforms();
            LastSyncMs = Ms(syncStart);

            dirtyStart = Stopwatch.GetTimestamp();
            foreach (var entry in _queue.Values)
                foreach (var collider in entry.Element.Colliders)
                    if (collider.enabled && collider.gameObject.activeInHierarchy) Dirty.Add(collider.bounds);
            LastDirtyMs += Ms(dirtyStart);
            _queue.Clear();

            var physics = gameObject.scene.GetPhysicsScene();
            var strategy = TopologyStrategy.Full;
            if (!_forceFull && sameDomain && physicsProven && _graph != null && _cache != null)
            {
                if (!colliderChanged && _strategy != TopologyStrategy.Full) strategy = TopologyStrategy.LinkOnly;
                else if (colliderChanged && _strategy == TopologyStrategy.Partial && Dirty.RepairCount * 2L < columnCount)
                    strategy = TopologyStrategy.Partial;
            }
            // A link-only build visits no physics-dependent columns.
            if (strategy == TopologyStrategy.LinkOnly)
                Dirty = new NavigationDirtyColumns(size, position.xz, _settings.Grid.CellSize, _settings.Grid.AgentRadius);
            var nextProof = CaptureProof();
            if (strategy == TopologyStrategy.LinkOnly && !ProofEquals(observed, nextProof))
                strategy = TopologyStrategy.Full;
            long fixedBytes = AccountedBytes + Dirty.PayloadBytes + 128L *
                ((observed?.Count ?? 0) + (nextProof?.Count ?? 0));
            long available = Budget - fixedBytes;
            LayeredNavigationSnapshot layered = null;
            NavigationGrid single = null;
            LayeredNavigationBuilder.SampleCache cache = null;
            LayerSamplingStats stats = default;
            try
            {
                if (_layered)
                {
                    var builder = new LayeredNavigationBuilder(physics, position, _settings, available);
                    try
                    {
                        layered = builder.BuildUpdate((LayerNavigationLink[])_links.Clone(),
                            strategy == TopologyStrategy.Full ? null : _graph, _cache, Dirty, out stats, out cache);
                    }
                    catch (ArgumentException) when (strategy != TopologyStrategy.Full)
                    {
                        strategy = TopologyStrategy.Full;
                        builder = new LayeredNavigationBuilder(physics, position, _settings, available);
                        layered = builder.BuildUpdate((LayerNavigationLink[])_links.Clone(), null, null, Dirty, out stats, out cache);
                    }
                    PeakBytes = Math.Max(PeakBytes, fixedBytes + layered.PayloadBytes + stats.TemporaryPayload + cache.PayloadBytes);
                    if (fixedBytes + layered.PayloadBytes + stats.TemporaryPayload + cache.PayloadBytes > Budget)
                        throw new ArgumentException("Retained sampling cache exceeds navigation memory budget.");
                }
                else
                {
                    long estimate = NavigationGridBuilder.EstimateBuildPayload(physics, position, _settings.Grid);
                    if (estimate > available) throw new ArgumentException("Single-layer build exceeds navigation memory budget.");
                    single = NavigationGridBuilder.Build(physics, position, _settings.Grid, out _);
                    PeakBytes = Math.Max(PeakBytes, fixedBytes + estimate);
                }

                _edgePreview.Clear();
                if (CaptureEdgePreview && _graph != null && layered != null)
                {
                    PreviewMissingEdges(_graph, layered, false);
                    PreviewMissingEdges(layered, _graph, true);
                }
                long start = Stopwatch.GetTimestamp();
                bool noImprovement = strategy == TopologyStrategy.LinkOnly && IsEdgeSubset(_graph, layered);
                Retained = Revoked = 0;
                if (layered != null)
                    foreach (var route in _routes)
                    {
                        bool wasValid = route.Status != RouteValidity.Invalid;
                        if (route.Revalidate(layered, noImprovement) == RouteValidity.Invalid)
                        {
                            if (wasValid) Revoked++;
                        }
                        else Retained++;
                    }
                else
                {
                    Revoked = _routes.Count;
                    ClearRoutes();
                }
                LastRevalidateMs = Ms(start);

                start = Stopwatch.GetTimestamp();
                RetireCurrent();
                _graph = layered;
                _single = single;
                layered = null;
                single = null;
                _cache = cache;
                _proof = nextProof;
                _builtSettings = _settings;
                _builtOrigin = position;
                _builtLayered = _layered;
                _rollbackColliders.Clear();
                _rollbackLinks.Clear();
                _physicsApplied = _forceFull = false;
                State = TopologyState.Ready;
                AppliedStrategy = strategy;
                LastSampling = stats;
                Published++;
                Diagnostic = $"Published {strategy} at revision {Revision}.";
                foreach (var scheduler in _schedulers) Bind(scheduler);
                LastPublishMs = Ms(start);
            }
            finally
            {
                layered?.Dispose();
                single?.Dispose();
            }
        }

        private void ValidateOwners()
        {
            var keys = new HashSet<int>();
            var ownedColliders = new HashSet<Collider>();
            var ownedLinks = new HashSet<LayerNavigationLink>();
            foreach (var element in _elements)
            {
                if (element == null || element.Owner <= 0 || !keys.Add(element.Owner))
                    throw new ArgumentException("Dynamic owners must have distinct positive keys.");
                foreach (var collider in element.Colliders)
                    if (collider == null || collider.gameObject.scene != gameObject.scene ||
                        !ownedColliders.Add(collider) ||
                        (collider.TryGetComponent<LayerNavigationSurface>(out var surface) && surface.Owner != element.Owner))
                        throw new ArgumentException($"Missing, foreign or mismatched collider on owner {element.Owner}.");
                foreach (var link in element.Links)
                    if (link == null || link.Owner != element.Owner || Array.IndexOf(_links, link) < 0 ||
                        !ownedLinks.Add(link))
                        throw new ArgumentException($"Missing or mismatched link on owner {element.Owner}.");
            }
        }

        private Dictionary<Collider, ColliderProof> CaptureProof()
        {
            var proof = new Dictionary<Collider, ColliderProof>();
            int mask = _settings.Grid.Surfaces.value | _settings.Grid.Blockers.value;
            var physics = gameObject.scene.GetPhysicsScene();
            _colliders.Clear();
            if (physics.Equals(Physics.defaultPhysicsScene))
            {
                // Additive scenes can share the default physics scene.
                foreach (var collider in FindObjectsByType<Collider>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (collider.gameObject.scene.GetPhysicsScene().Equals(physics)) _colliders.Add(collider);
            }
            else
            {
                gameObject.scene.GetRootGameObjects(_roots);
                foreach (var root in _roots)
                {
                    _rootColliders.Clear();
                    root.GetComponentsInChildren(true, _rootColliders);
                    _colliders.AddRange(_rootColliders);
                }
            }
            foreach (var collider in _colliders)
            {
                int layer = collider.gameObject.layer;
                if ((mask & (1 << layer)) == 0) continue;
                // Physics-driven poses are not necessarily represented by Transform.
                if (!(collider is BoxCollider box) || collider.attachedRigidbody != null ||
                    collider.attachedArticulationBody != null) return null;
                collider.TryGetComponent<LayerNavigationSurface>(out var source);
                proof.Add(collider, new ColliderProof { Transform = collider.transform.localToWorldMatrix,
                    Center = box.center, Size = box.size, Enabled = collider.enabled,
                    Active = collider.gameObject.activeInHierarchy, Trigger = collider.isTrigger, Layer = layer,
                    SurfaceKey = source != null ? source.Key : 0, Owner = source != null ? source.Owner : 0,
                    Cost = source != null ? source.Cost : 1 });
            }
            return proof;
        }

        private static bool ProofEquals(Dictionary<Collider, ColliderProof> first, Dictionary<Collider, ColliderProof> second)
        {
            if (first == null || second == null || first.Count != second.Count) return false;
            foreach (var pair in first)
                if (!second.TryGetValue(pair.Key, out var match) || !pair.Value.Equals(match)) return false;
            return true;
        }

        // Link-only copy preserves the entire node layout. Every new directed edge must be present
        // with the same semantics and cost before we can retain an already proven optimum.
        private static bool IsEdgeSubset(LayeredNavigationSnapshot old, LayeredNavigationSnapshot next)
        {
            var a = old.View(); var b = next.View();
            if (a.Nodes.Length != b.Nodes.Length || b.Edges.Length > a.Edges.Length) return false;
            int previous = 0;
            for (int i = 0; i < b.Edges.Length; i++)
            {
                var edge = b.Edges[i];
                while (previous < a.Edges.Length && !SameEdge(a.Edges[previous], edge)) previous++;
                if (previous == a.Edges.Length) return false;
                previous++;
            }
            return true;
        }

        private static bool SameEdge(LayerEdge a, LayerEdge b) => a.From == b.From && a.To == b.To &&
            a.Type == b.Type && a.Owner == b.Owner && a.StableKey == b.StableKey && a.Cost == b.Cost;

        private void PreviewMissingEdges(LayeredNavigationSnapshot source, LayeredNavigationSnapshot other, bool added)
        {
            const int drawCap = 96;
            const int scanCap = 16384;
            int limit = _edgePreview.Count + drawCap;
            var graph = source.View();
            var target = other.View();
            for (int i = 0; i < math.min(graph.Edges.Length, scanCap) && _edgePreview.Count < limit; i++)
            {
                var edge = graph.Edges[i];
                var from = graph.Nodes[edge.From];
                var to = graph.Nodes[edge.To];
                int start = other.ResolveSurface(from.Position, from.SurfaceKey);
                int end = other.ResolveSurface(to.Position, to.SurfaceKey);
                bool found = false;
                if (start >= 0 && end >= 0)
                    for (int j = target.Outgoing[start]; j < target.Outgoing[start + 1]; j++)
                    {
                        var candidate = target.Edges[j];
                        if (candidate.To != end || candidate.Type != edge.Type || candidate.Owner != edge.Owner ||
                            candidate.StableKey != edge.StableKey || candidate.Cost != edge.Cost) continue;
                        found = true;
                        break;
                    }
                if (!found) _edgePreview.Add((from.Position, to.Position, added));
            }
        }

        private void RetireCurrent()
        {
            long bytes = GraphBytes;
            var handle = _graph != null ? _graph.Retire() : _single != null ? _single.Retire() : default;
            _graph = null;
            _single = null;
            if (handle.IsCompleted) handle.Complete();
            else
            {
                _retired = handle;
                _retiredStart = Stopwatch.GetTimestamp();
                _retiredBytes = bytes;
                JobHandle.ScheduleBatchedJobs();
            }
        }

        private void CollectRetired()
        {
            if (_retiredBytes == 0 || !_retired.IsCompleted) return;
            _retired.Complete();
            _retiredBytes = 0;
            LastRetirementMs = Ms(_retiredStart);
        }

        private void OnDisable()
        {
            foreach (var scheduler in _schedulers) scheduler.SetLayeredSnapshot(null);
            _schedulers.Clear();
            _retired.Complete();
            _retiredBytes = 0;
            RetireCurrent();
            _retired.Complete();
            _retiredBytes = 0;
            _queue.Clear();
            _cache = null;
            _proof = null;
            _edgePreview.Clear();
            ClearRoutes();
            _rollbackColliders.Clear();
            _rollbackLinks.Clear();
            _physicsApplied = false;
            State = TopologyState.Blocked;
            Diagnostic = "Navigation owner disabled.";
        }

        private static double Ms(long start) => (Stopwatch.GetTimestamp() - start) * 1000d / Stopwatch.Frequency;
    }
}
