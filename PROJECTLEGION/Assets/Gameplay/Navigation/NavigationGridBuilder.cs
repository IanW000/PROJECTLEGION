using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Mathematics;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [Serializable]
    internal struct NavigationGridSettings
    {
        [SerializeField] internal Vector2Int Size;
        [SerializeField, Min(0.01f)] internal float CellSize;
        [SerializeField, Range(1, 8)] internal int SampleDensity;
        [SerializeField, Min(0.01f)] internal float ProbeAbove;
        [SerializeField, Min(0.01f)] internal float ProbeBelow;
        [SerializeField, Range(0, 89)] internal float MaxSlopeDegrees;
        [SerializeField, Min(0f)] internal float MaxStepHeight;
        [SerializeField, Min(0.01f)] internal float AgentRadius;
        [SerializeField, Min(0.02f)] internal float AgentHeight;
        [SerializeField] internal bool Diagonals;
        [SerializeField] internal LayerMask Surfaces;
        [SerializeField] internal LayerMask Blockers;

        internal static NavigationGridSettings Default => new NavigationGridSettings
        {
            Size = new Vector2Int(32, 32), CellSize = 1, SampleDensity = 2, ProbeAbove = 5, ProbeBelow = 5,
            MaxSlopeDegrees = 35, MaxStepHeight = 0.25f, AgentRadius = 0.35f, AgentHeight = 0.8f, Diagonals = true,
            Surfaces = 1, Blockers = 0
        };
    }

    internal struct NavigationBuildMetrics
    {
        internal int Samples, SurfaceRaycasts, AmbiguousProbes, SaturatedProbes;
        internal long SampleBufferBytes, QueryBufferPayloadBytes;
    }

    internal static class NavigationGridBuilder
    {
        private const int MaxSamples = 2_000_000;
        private const int HitCapacity = 32;
        private const float HeightEpsilon = 0.0001f;

        private struct Sample
        {
            internal float Height, Cost;
            internal float2 Grade;
            internal byte Flags;
            internal bool Valid => (Flags & 2) != 0;
        }

        internal static NavigationGrid Build(PhysicsScene physics, Vector3 origin, NavigationGridSettings settings,
            out NavigationBuildMetrics metrics)
        {
            int factor = Validate(physics, origin, settings);
            int2 size = new int2(settings.Size.x, settings.Size.y);
            int2 lattice = size * factor + 1;
            var grid = new NavigationGrid(size, new float2(origin.x, origin.z), settings.CellSize);
            try
            {
                using var samples = new NativeArray<Sample>(lattice.x * lattice.y, Allocator.TempJob);
                var builder = new Sampler(physics, origin, settings, samples, lattice.x, factor);
                for (int z = 0; z < size.y; z++)
                for (int x = 0; x < size.x; x++)
                {
                    int2 cell = new int2(x, z);
                    var sample = builder.Get(cell * factor + factor / 2);
                    grid.SetNode(cell, sample.Height, NavigationGrid.ValidateCost(sample.Cost), (byte)(sample.Flags & 3));
                }
                AddEdges(grid, builder, factor, false);
                if (settings.Diagonals)
                    AddEdges(grid, builder, factor, true);
                metrics = builder.Metrics;
                return grid;
            }
            catch
            {
                grid.Dispose();
                throw;
            }
        }

        private static int Validate(PhysicsScene physics, Vector3 origin, NavigationGridSettings s)
        {
            if (!physics.IsValid() || !math.all(math.isfinite((float3)origin)))
                throw new ArgumentException("A valid physics scene and finite grid origin are required.");
            if (s.Size.x <= 0 || s.Size.y <= 0 || s.SampleDensity < 1 || s.SampleDensity > 8 ||
                !math.isfinite(s.CellSize) || s.CellSize < 0.01f)
                throw new ArgumentOutOfRangeException(nameof(s), "Invalid grid dimensions, cell size or sample density.");
            if (!math.all(math.isfinite(new float4(s.ProbeAbove, s.ProbeBelow, s.AgentRadius, s.AgentHeight))) ||
                s.ProbeAbove <= 0 || s.ProbeBelow <= 0 || s.AgentRadius < 0.01f || s.AgentHeight < 2 * s.AgentRadius ||
                !math.isfinite(s.MaxStepHeight) || s.MaxStepHeight < 0 ||
                !math.isfinite(s.MaxSlopeDegrees) || s.MaxSlopeDegrees < 0 || s.MaxSlopeDegrees > 89 ||
                !math.isfinite(s.ProbeAbove + s.ProbeBelow))
                throw new ArgumentOutOfRangeException(nameof(s), "Invalid height, clearance or slope settings.");
            if (s.Surfaces.value == 0 || (s.Surfaces.value & s.Blockers.value) != 0)
                throw new ArgumentException("Use a nonempty surface mask and a disjoint blocker mask.", nameof(s));
            int factor = s.SampleDensity * 2;
            long width = (long)s.Size.x * factor + 1;
            long depth = (long)s.Size.y * factor + 1;
            if (width > MaxSamples || depth > MaxSamples / width)
                throw new ArgumentOutOfRangeException(nameof(s), "Sampling lattice exceeds the configured implementation limit.");
            float pitch = s.CellSize / factor;
            if (!math.isfinite(origin.x + s.Size.x * s.CellSize) ||
                !math.isfinite(origin.z + s.Size.y * s.CellSize) ||
                !math.isfinite(origin.y + s.ProbeAbove) || !math.isfinite(origin.y - s.ProbeBelow) ||
                origin.x + pitch == origin.x || origin.z + pitch == origin.z ||
                origin.x + s.Size.x * s.CellSize - pitch == origin.x + s.Size.x * s.CellSize ||
                origin.z + s.Size.y * s.CellSize - pitch == origin.z + s.Size.y * s.CellSize)
                throw new ArgumentOutOfRangeException(nameof(s), "Sampling pitch is unrepresentable within the world bounds.");
            return factor;
        }

        private static void AddEdges(NavigationGrid grid, Sampler builder, int factor, bool diagonals)
        {
            int first = diagonals ? 4 : 0;
            for (int z = 0; z < grid.Size.y; z++)
            for (int x = 0; x < grid.Size.x; x++)
            {
                int2 from = new int2(x, z);
                grid.TryGetNode(from, out var a);
                if (!a.Walkable)
                    continue;
                // Only two orientations per pass; Connect writes both directions.
                for (int direction = first; direction < first + 2; direction++)
                {
                    int2 offset = NavigationGrid.Offset(direction);
                    int2 to = from + offset;
                    if (!grid.TryGetNode(to, out var b) || !b.Walkable)
                        continue;
                    if (diagonals)
                    {
                        int horizontal = offset.x > 0 ? 0 : 2;
                        if (!grid.IsConnected(from, horizontal) || !grid.IsConnected(from, 1) ||
                            !grid.IsConnected(from + new int2(offset.x, 0), 1) ||
                            !grid.IsConnected(from + new int2(0, 1), horizontal))
                            continue;
                    }
                    if (builder.CanFollow(from * factor + factor / 2, offset))
                        grid.Connect(from, to, direction);
                }
            }
        }

        private sealed class Sampler
        {
            private readonly PhysicsScene _physics;
            private readonly NavigationGridSettings _settings;
            private readonly Vector3 _origin;
            private NativeArray<Sample> _samples;
            private readonly RaycastHit[] _hits = new RaycastHit[HitCapacity];
            private readonly Collider[] _overlap = new Collider[1];
            private readonly int _width, _factor;
            private readonly float _pitch, _minNormalY;
            internal NavigationBuildMetrics Metrics;

            internal Sampler(PhysicsScene physics, Vector3 origin, NavigationGridSettings settings,
                NativeArray<Sample> samples, int width, int factor)
            {
                _physics = physics;
                _origin = origin;
                _settings = settings;
                _samples = samples;
                _width = width;
                _factor = factor;
                _pitch = settings.CellSize / factor;
                _minNormalY = math.cos(math.radians(settings.MaxSlopeDegrees));
                Metrics.SampleBufferBytes = (long)samples.Length * UnsafeUtility.SizeOf<Sample>();
                Metrics.QueryBufferPayloadBytes = HitCapacity * UnsafeUtility.SizeOf<RaycastHit>() + IntPtr.Size;
            }

            internal Sample Get(int2 address)
            {
                int index = address.y * _width + address.x;
                var cached = _samples[index];
                if ((cached.Flags & 4) != 0)
                    return cached;

                Metrics.Samples++;
                bool isCenter = address.x % _factor == _factor / 2 && address.y % _factor == _factor / 2;
                float2 position = new float2(_origin.x, _origin.z) + (float2)address * _pitch;
                cached = ReadSurface(position, isCenter);
                if (cached.Valid && (!Clearance(position, cached.Height) || !SupportsFootprint(position, cached)))
                    cached.Flags &= 1;
                cached.Flags |= 4;
                _samples[index] = cached;
                return cached;
            }

            private Sample ReadSurface(float2 position, bool readCost)
            {
                Metrics.SurfaceRaycasts++;
                int count = _physics.Raycast(new Vector3(position.x, _origin.y + _settings.ProbeAbove, position.y),
                    Vector3.down, _hits, _settings.ProbeAbove + _settings.ProbeBelow, _settings.Surfaces.value,
                    QueryTriggerInteraction.Ignore);
                if (count == _hits.Length)
                {
                    Metrics.SaturatedProbes++;
                    return default;
                }

                // Use the lowest upward-facing surface. Never connect to an upper surface at
                // this XZ merely because the lower one is blocked.
                int best = -1;
                for (int i = 0; i < count; i++)
                {
                    if (_hits[i].normal.y <= 0f)
                        continue;
                    if (best < 0 || _hits[i].point.y < _hits[best].point.y ||
                        (_hits[i].point.y == _hits[best].point.y && _hits[i].colliderInstanceID < _hits[best].colliderInstanceID))
                        best = i;
                }
                if (best < 0)
                    return default;

                var hit = _hits[best];
                float overhead = float.PositiveInfinity;
                for (int i = 0; i < count; i++)
                {
                    if (_hits[i].point.y - hit.point.y <= HeightEpsilon)
                        continue;
                    Bounds bounds = _hits[i].collider.bounds;
                    // A shared footprint boundary can hit both step surfaces on the same ray.
                    // Only overlapping interiors count as overhead in this single-layer build.
                    if (position.x > bounds.min.x + HeightEpsilon && position.x < bounds.max.x - HeightEpsilon &&
                        position.y > bounds.min.z + HeightEpsilon && position.y < bounds.max.z - HeightEpsilon)
                        overhead = math.min(overhead, bounds.min.y - hit.point.y);
                }
                if (math.isfinite(overhead))
                    Metrics.AmbiguousProbes++;
                float cost = NavigationGrid.DefaultTraversalCost;
                if (readCost && hit.collider.TryGetComponent<NavigationSurfaceCost>(out var surface))
                    cost = surface.TraversalCost;
                var normal = hit.normal;
                return new Sample
                {
                    Height = hit.point.y,
                    Cost = cost,
                    Grade = new float2(-normal.x, -normal.z) / normal.y,
                    Flags = (byte)(normal.y + HeightEpsilon >= _minNormalY && overhead >= _settings.AgentHeight ? 3 : 1)
                };
            }

            private bool Clearance(float2 position, float height)
            {
                if (_settings.Blockers.value == 0)
                    return true;
                float r = _settings.AgentRadius;
                return _physics.OverlapCapsule(new Vector3(position.x, height + r + HeightEpsilon, position.y),
                    new Vector3(position.x, height + _settings.AgentHeight - r + HeightEpsilon, position.y), r,
                    _overlap, _settings.Blockers.value, QueryTriggerInteraction.Ignore) == 0;
            }

            private bool SupportsFootprint(float2 position, Sample center)
            {
                for (int direction = 0; direction < 4; direction++)
                {
                    float2 step = (float2)NavigationGrid.Offset(direction) * _settings.AgentRadius;
                    if (!CanFollow(center, ReadSurface(position + step, false), step))
                        return false;
                }
                return true;
            }

            internal bool CanFollow(int2 address, int2 direction)
            {
                var previous = Get(address);
                float2 step = (float2)direction * _pitch;
                for (int i = 1; i <= _factor; i++)
                {
                    var next = Get(address + direction * i);
                    if (!CanFollow(previous, next, step))
                        return false;
                    previous = next;
                }
                return true;
            }

            private bool CanFollow(Sample a, Sample b, float2 step)
            {
                if (!a.Valid || !b.Valid)
                    return false;
                float delta = b.Height - a.Height;
                float inclineA = math.dot(a.Grade, step);
                float inclineB = math.dot(b.Grade, step);
                // The sampled surface normals bound the continuous part of the elevation change.
                // Any elevation beyond that range is treated as a step.
                float low = math.min(inclineA, inclineB);
                float high = math.max(inclineA, inclineB);
                return math.max(low - delta, delta - high) <= _settings.MaxStepHeight + HeightEpsilon;
            }
        }
    }
}
