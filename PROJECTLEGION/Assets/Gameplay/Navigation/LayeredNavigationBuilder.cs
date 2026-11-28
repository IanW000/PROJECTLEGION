using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Mathematics;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [Serializable]
    internal struct LayerNavigationSettings
    {
        [SerializeField] internal NavigationGridSettings Grid;
        [SerializeField, Range(1, 16)] internal int Layers;
        [SerializeField, Range(2, 128)] internal int HitCapacity;
        [SerializeField, Min(0.001f)] internal float HeightSeparation;
        [SerializeField, Min(0.001f)] internal float EndpointTolerance;

        internal static LayerNavigationSettings Default => new LayerNavigationSettings { Grid = NavigationGridSettings.Default,
            Layers = 4, HitCapacity = 32, HeightSeparation = 0.02f, EndpointTolerance = 0.4f };
    }

    internal struct LayerSamplingStats
    {
        internal long Rays, Capsules, RejectedCandidates, ContinuityRejections;
        internal long TemporaryPayload;
        internal double ExtractionMs, ConnectionsMs, RemapMs, PackMs;
        internal long ResampledColumns;
    }

    internal sealed class LayeredNavigationBuilder
    {
        internal struct Sample
        {
            internal Collider Collider;
            internal int Key;
            internal float Height, Cost;
            internal float3 Normal;
            internal bool Walkable;
        }

        internal sealed class SampleCache
        {
            internal Sample[] Samples;
            internal long Generation;
            internal long PayloadBytes => 48L * Samples.Length;
        }

        private readonly PhysicsScene _physics;
        private static readonly Comparison<Sample> SampleOrder = Compare;
        private readonly float3 _origin;
        private readonly LayerNavigationSettings _settings;
        private readonly RaycastHit[] _hits;
        private readonly Collider[] _overlaps;
        private readonly Dictionary<int, Collider> _keys = new Dictionary<int, Collider>();
        private readonly List<Sample> _column = new List<Sample>();
        private readonly List<Sample> _intermediate = new List<Sample>();
        private readonly List<Sample> _foot = new List<Sample>();
        private readonly List<LayerNode> _nodes = new List<LayerNode>();
        private readonly List<Sample> _supports = new List<Sample>();
        private readonly List<LayerEdge> _edges = new List<LayerEdge>();
        private readonly int[] _columns;
        private readonly int2 _size;
        private readonly long _budget;

        private LayerSamplingStats _stats;
        private LayeredNavigationSnapshot _previous;
        private NavigationDirtyColumns _dirty;
        private int[] _oldToNew;

        internal LayeredNavigationBuilder(PhysicsScene physics, float3 origin, LayerNavigationSettings settings, long budget)
        {
            var s = settings.Grid;
            if (!physics.IsValid() || !math.all(math.isfinite(origin)) || s.Size.x <= 0 || s.Size.y <= 0 ||
                !math.all(math.isfinite(new float4(s.CellSize, s.ProbeAbove, s.ProbeBelow, s.MaxStepHeight))) ||
                !math.all(math.isfinite(new float4(s.AgentHeight, s.AgentRadius, s.MaxSlopeDegrees, settings.HeightSeparation))) ||
                !math.isfinite(settings.EndpointTolerance) || s.CellSize < .01f || s.ProbeAbove <= 0 || s.ProbeBelow <= 0 ||
                s.MaxSlopeDegrees < 0 || s.MaxSlopeDegrees > 89 || s.MaxStepHeight < 0 || s.AgentRadius < .01f ||
                s.AgentHeight < s.AgentRadius * 2 || s.Surfaces.value == 0 || (s.Surfaces.value & s.Blockers.value) != 0 ||
                settings.HeightSeparation <= 0 || settings.EndpointTolerance <= 0 || s.SampleDensity < 1 ||
                s.SampleDensity > 8 || settings.Layers < 1 || settings.Layers > 16 || settings.HitCapacity < 2 ||
                settings.HitCapacity > 128)
                throw new ArgumentException("Invalid layered sampling settings.");

            _physics = physics; _origin = origin; _settings = settings; _budget = budget;
            _size = new int2(s.Size.x, s.Size.y);
            long columns = (long)_size.x * _size.y;
            if (columns > 2_000_000 || (columns + 1) * 4 > budget)
                throw new ArgumentException("Layered column capacity exceeds budget.");
            _columns = new int[(int)columns + 1];
            _hits = new RaycastHit[settings.HitCapacity];
            _overlaps = new Collider[settings.HitCapacity];
        }

        internal LayeredNavigationSnapshot Build(LayerNavigationLink[] links, out LayerSamplingStats stats)
            => BuildCore(links, null, null, null, false, out stats, out _);

        internal LayeredNavigationSnapshot BuildUpdate(LayerNavigationLink[] links, LayeredNavigationSnapshot previous,
            SampleCache priorSamples, NavigationDirtyColumns dirty, out LayerSamplingStats stats, out SampleCache cache)
            => BuildCore(links, previous, priorSamples, dirty, true, out stats, out cache);

        private LayeredNavigationSnapshot BuildCore(LayerNavigationLink[] links, LayeredNavigationSnapshot previous,
            SampleCache priorSamples, NavigationDirtyColumns dirty, bool keepSamples, out LayerSamplingStats stats,
            out SampleCache cache)
        {
            cache = null;
            _previous = previous;
            _dirty = dirty;
            if (_previous != null)
            {
                if (priorSamples == null || priorSamples.Generation != previous.Generation ||
                    !previous.Size.Equals(_size) || !previous.Origin.Equals(_origin.xz) ||
                    previous.CellSize != _settings.Grid.CellSize || dirty == null)
                    throw new ArgumentException("Partial input is incompatible with the current sampling domain.");
                _oldToNew = new int[previous.NodeCount];
                Array.Fill(_oldToNew, -1);
                // Validate reused author keys before sampling any replacement column.
                foreach (var sample in priorSamples.Samples)
                    if (sample.Collider != null)
                        _keys[sample.Key] = sample.Collider;
            }
            long begin = Stopwatch.GetTimestamp();
            for (int column = 0; column < _columns.Length - 1; column++)
            {
                _columns[column] = _nodes.Count;
                if (previous != null && !dirty.Sample[column])
                {
                    var view = previous.View();
                    for (int old = previous.ColumnStart(column); old < previous.ColumnStart(column + 1); old++)
                    {
                        GuardMemory(_nodes.Count + 1, _edges.Count);
                        _oldToNew[old] = _nodes.Count;
                        _nodes.Add(view.Nodes[old]);
                        _supports.Add(priorSamples.Samples[old]);
                    }
                    continue;
                }
                _stats.ResampledColumns++;
                float2 position = Center(column);
                Read(position, _column);

                for (int i = 0; i < _column.Count; i++)
                {
                    var sample = _column[i];
                    sample.Walkable = BodyClear(position, sample) && Supported(position, sample);
                    if (!sample.Walkable) _stats.RejectedCandidates++;

                    if (i >= _settings.Layers)
                        throw new ArgumentException($"Layer limit exceeded in column {column}.");
                    GuardMemory(_nodes.Count + 1, _edges.Count);
                    _nodes.Add(new LayerNode { Position = new float3(position.x, sample.Height, position.y),
                        Cost = sample.Cost, Walkable = sample.Walkable, SurfaceKey = sample.Key });
                    _supports.Add(sample);
                }
            }
            _columns[_columns.Length - 1] = _nodes.Count;
            _stats.ExtractionMs = Elapsed(begin);

            begin = Stopwatch.GetTimestamp();
            if (previous != null)
            {
                long remapBegin = Stopwatch.GetTimestamp();
                // Match replaced column samples by stable surface and exact height solely for remapping.
                var view = previous.View();
                for (int col = 0; col < _columns.Length - 1; col++)
                {
                    if (!dirty.Sample[col]) continue;
                    for (int old = previous.ColumnStart(col); old < previous.ColumnStart(col + 1); old++)
                    for (int node = _columns[col]; node < _columns[col + 1]; node++)
                    {
                        var a = view.Nodes[old];
                        var b = _nodes[node];
                        if (a.SurfaceKey == b.SurfaceKey && a.Position.Equals(b.Position))
                            _oldToNew[old] = node;
                    }
                }
                _stats.RemapMs = Elapsed(remapBegin);
            }
            BuildOrdinary(0, 4);
            if (_settings.Grid.Diagonals) BuildOrdinary(4, 8);
            AddLinks(links);
            GuardMemory(_nodes.Count, _edges.Count);
            _stats.ConnectionsMs = Elapsed(begin) - _stats.RemapMs;
            begin = Stopwatch.GetTimestamp();
            long retainedCache = keepSamples ? 48L * _supports.Count : 0;
            var snapshot = new LayeredNavigationSnapshot(_size, _origin.xz, _settings.Grid.CellSize, _nodes.ToArray(),
                _columns, _edges, _budget - _stats.TemporaryPayload - retainedCache);
            if (keepSamples)
            {
                try { cache = new SampleCache { Samples = _supports.ToArray(), Generation = snapshot.Generation }; }
                catch { snapshot.Dispose(); throw; }
            }
            _stats.PackMs = Elapsed(begin);
            stats = _stats;
            return snapshot;
        }

        private static double Elapsed(long begin) => (Stopwatch.GetTimestamp() - begin) * 1000d / Stopwatch.Frequency;

        private void GuardMemory(int nodes, int edges)
        {
            // Conservative staging allowance including List growth, collider key registry and final cursor.
            long scratch = 4L * _columns.Length + 200L * _settings.HitCapacity + 112L * nodes + 64L * edges +
                192L * _keys.Count + 4L * (_oldToNew?.Length ?? 0);
            _stats.TemporaryPayload = Math.Max(_stats.TemporaryPayload, scratch);
            if (LayeredNavigationSnapshot.Estimate(_size, nodes, edges) + _stats.TemporaryPayload > _budget)
                throw new ArgumentException("Layered sampling and snapshot exceed budget.");
        }

        private float2 Center(int column) => _origin.xz +
            (new float2(column % _size.x, column / _size.x) + 0.5f) * _settings.Grid.CellSize;

        private void Read(float2 position, List<Sample> output)
        {

            output.Clear();
            _stats.Rays++;
            var s = _settings.Grid;
            int count = _physics.Raycast(new Vector3(position.x, _origin.y + s.ProbeAbove, position.y),
                Vector3.down, _hits, s.ProbeAbove + s.ProbeBelow, s.Surfaces.value, QueryTriggerInteraction.Ignore);
            if (count == _hits.Length) throw new ArgumentException($"Ray hit buffer saturated at {position}.");
            for (int i = 0; i < count; i++)
            {
                var hit = _hits[i];
                if (hit.normal.y <= 0) continue;
                var collider = hit.collider;
                if (!collider.TryGetComponent<LayerNavigationSurface>(out var source) || source.Key <= 0 ||
                    (!_keys.ContainsKey(source.Key) && collider.GetComponents<Collider>().Length != 1))
                    throw new ArgumentException($"Surface {collider.name} requires one Collider and a stable nonzero key.");
                if (_keys.TryGetValue(source.Key, out var previous) && previous != collider)
                    throw new ArgumentException($"Duplicate surface key {source.Key}.");
                _keys[source.Key] = collider;
                output.Add(new Sample { Collider = collider, Height = hit.point.y, Key = source.Key,
                    Cost = source.Cost, Normal = hit.normal });
            }
            output.Sort(SampleOrder);
            for (int i = 1; i < output.Count; i++)
                if (output[i].Height - output[i - 1].Height < _settings.HeightSeparation)
                    throw new ArgumentException($"Ambiguous near-height surfaces at {position}.");
        }

        private static int Compare(Sample a, Sample b)
        {
            int sign = a.Height.CompareTo(b.Height);
            return sign != 0 ? sign : a.Key.CompareTo(b.Key);
        }

        private bool BodyClear(float2 position, Sample support)
        {
            var settings = _settings.Grid;
            if (support.Normal.y < math.cos(math.radians(settings.MaxSlopeDegrees))) return false;
            float radius = settings.AgentRadius;
            float floor = support.Height + 0.003f;
            _stats.Capsules++;
            int count = _physics.OverlapCapsule(new Vector3(position.x, floor + radius, position.y),
                new Vector3(position.x, floor + settings.AgentHeight - radius, position.y), radius, _overlaps,
                settings.Surfaces.value | settings.Blockers.value, QueryTriggerInteraction.Ignore);
            if (count == _overlaps.Length) throw new ArgumentException($"Clearance hit buffer saturated at {position}.");
            for (int i = 0; i < count; i++)
                if (_overlaps[i] != support.Collider && !HasAdjacentSupport(position, support, _overlaps[i]))
                    return false;
            return true;
        }

        private bool HasAdjacentSupport(float2 position, Sample support, Collider neighbor)
        {
            if (!neighbor.TryGetComponent<LayerNavigationSurface>(out _)) return false;
            for (int i = 0; i < 4; i++)
            {
                float2 step = (float2)NavigationGrid.Offset(i) * _settings.Grid.AgentRadius;
                Read(position + step, _foot);
                if (Choose(support, step, _foot, out var sample) && sample.Collider == neighbor)
                    return true;
            }
            return false;
        }

        private bool Supported(float2 position, Sample support)
        {
            for (int i = 0; i < 4; i++)
            {
                float2 step = (float2)NavigationGrid.Offset(i) * _settings.Grid.AgentRadius;
                Read(position + step, _foot);
                if (!Choose(support, step, _foot, out _)) return false;
            }
            return true;
        }

        private bool Choose(Sample from, float2 step, List<Sample> candidates, out Sample chosen)
        {
            chosen = default;
            int count = 0;
            foreach (var next in candidates)
            {
                if (!Compatible(from, next, step)) continue;
                chosen = next;
                count++;
            }
            return count == 1;
        }

        private bool Compatible(Sample from, Sample to, float2 step)
        {
            if (to.Normal.y < math.cos(math.radians(_settings.Grid.MaxSlopeDegrees))) return false;
            float a = -math.dot(from.Normal.xz, step) / from.Normal.y;
            float b = -math.dot(to.Normal.xz, step) / to.Normal.y;
            float rise = to.Height - from.Height;
            return math.max(math.min(a, b) - rise, rise - math.max(a, b)) <= _settings.Grid.MaxStepHeight + .0001f;
        }

        private void BuildOrdinary(int first, int end)
        {
            int cardinalEnd = _edges.Count;
            // Cardinal edges are recorded in source-node order for diagonal checks.
            var cardinal = first == 4 ? new int[_nodes.Count + 1] : null;
            if (cardinal != null)
            {
                for (int i = 0; i < cardinalEnd; i++) cardinal[_edges[i].From + 1]++;
                for (int i = 1; i < cardinal.Length; i++) cardinal[i] += cardinal[i - 1];
            }
            for (int col = 0; col < _columns.Length - 1; col++)
            {
                if (_previous != null && !_dirty.Repair[col])
                {
                    var view = _previous.View();
                    for (int old = _previous.ColumnStart(col); old < _previous.ColumnStart(col + 1); old++)
                    for (int e = view.Outgoing[old]; e < view.Outgoing[old + 1]; e++)
                    {
                        var edge = view.Edges[e];
                        if (edge.Type != LayerConnectionType.Surface) continue;
                        var delta = view.Nodes[edge.To].Position.xz - view.Nodes[old].Position.xz;
                        bool diagonal = delta.x != 0 && delta.y != 0;
                        if (diagonal != (first == 4)) continue;
                        edge.From = _oldToNew[edge.From];
                        edge.To = _oldToNew[edge.To];
                        if (edge.From < 0 || edge.To < 0) throw new ArgumentException("Dirty support exceeded its repair halo.");
                        Add(edge);
                    }
                    continue;
                }
                int2 cell = new int2(col % _size.x, col / _size.x);
                for (int from = _columns[col]; from < _columns[col + 1]; from++)
                {
                    if (!_nodes[from].Walkable) continue;
                    for (int direction = first; direction < end; direction++)
                    {
                        int2 offset = NavigationGrid.Offset(direction);
                        int2 next = cell + offset;
                        if (math.any(next < 0 | next >= _size)) continue;
                        int nextColumn = next.y * _size.x + next.x;
                        int dest = Follow(from, Center(col), nextColumn);
                        if (dest < 0)
                        {
                            _stats.ContinuityRejections++;
                            continue;
                        }
                        if (cardinal != null)
                        {
                            int sideways = col + offset.x;
                            int vertical = col + offset.y * _size.x;
                            if (!TwoSteps(from, dest, sideways, cardinal) || !TwoSteps(from, dest, vertical, cardinal))
                            {
                                _stats.ContinuityRejections++;
                                continue;
                            }
                        }
                        double length = math.distance((double3)_nodes[from].Position, (double3)_nodes[dest].Position);
                        Add(new LayerEdge { From = from, To = dest, Cost = length * ((double)_nodes[from].Cost + _nodes[dest].Cost) / 2 });
                    }
                }
            }
        }

        private bool TwoSteps(int from, int goal, int column, int[] cardinal)
        {
            for (int i = cardinal[from]; i < cardinal[from + 1]; i++)
            {
                int middle = _edges[i].To;
                if (middle < _columns[column] || middle >= _columns[column + 1]) continue;
                for (int j = cardinal[middle]; j < cardinal[middle + 1]; j++)
                    if (_edges[j].To == goal) return true;
            }
            return false;
        }

        private int Follow(int from, float2 position, int destinationColumn)
        {
            var previous = _supports[from];
            int steps = 2 * _settings.Grid.SampleDensity;
            float2 step = (Center(destinationColumn) - position) / steps;
            for (int i = 1; i < steps; i++)
            {
                float2 at = position + i * step;
                Read(at, _intermediate);
                if (!Choose(previous, step, _intermediate, out previous) || !BodyClear(at, previous) ||
                    !Supported(at, previous)) return -1;
            }
            int destination = -1;
            for (int node = _columns[destinationColumn]; node < _columns[destinationColumn + 1]; node++)
            {
                if (!_nodes[node].Walkable || !Compatible(previous, _supports[node], step)) continue;
                if (destination >= 0) return -1;
                destination = node;
            }
            return destination;
        }

        private void Add(LayerEdge edge)
        {
            GuardMemory(_nodes.Count, _edges.Count + 1);
            _edges.Add(edge);
        }

        private void AddLinks(LayerNavigationLink[] links)
        {
            if (links == null) return;
            foreach (var link in links)
                if (link == null) throw new ArgumentException("Missing static link in the authoring list.");
            Array.Sort(links, (a, b) => a.Key.CompareTo(b.Key));
            int previous = 0;
            foreach (var link in links)
            {
                if (!link.isActiveAndEnabled) continue;
                if (link.Key <= 0 || link.Key == previous || link.Source == null || link.Destination == null ||
                    link.Type == LayerConnectionType.Surface || !math.isfinite(link.Cost) || link.Cost <= 0 ||
                    (link.Bidirectional && (!math.isfinite(link.ReturnCost) || link.ReturnCost <= 0)))
                    throw new ArgumentException($"Invalid or duplicate static link {link.name}.");
                previous = link.Key;
                int start = Resolve(link.Source.position), end = Resolve(link.Destination.position);
                if (start < 0 || end < 0 || start == end)
                    throw new ArgumentException($"Unresolved or identical endpoints on static link {link.name}.");
                int owner = link.Owner;
                Add(new LayerEdge { From = start, To = end, Cost = link.Cost, Type = link.Type, Owner = owner,
                    StableKey = link.Key });
                if (link.Bidirectional)
                    Add(new LayerEdge { From = end, To = start, Cost = link.ReturnCost, Type = link.Type, Owner = owner,
                        StableKey = link.Key });
            }
        }

        private int Resolve(float3 position)
        {
            if (!math.all(math.isfinite(position))) return -1;
            int2 cell = (int2)math.floor((position.xz - _origin.xz) / _settings.Grid.CellSize);
            if (math.any(cell < 0 | cell >= _size)) return -1;
            int col = cell.y * _size.x + cell.x;
            int result = -1;
            float delta = _settings.EndpointTolerance;
            for (int n = _columns[col]; n < _columns[col + 1]; n++)
            {
                if (!_nodes[n].Walkable) continue;
                float difference = math.abs(_nodes[n].Position.y - position.y);
                if (difference > delta) continue;
                if (difference == delta && result >= 0) return -1;
                result = n;
                delta = difference;
            }
            return result;
        }
    }
}
