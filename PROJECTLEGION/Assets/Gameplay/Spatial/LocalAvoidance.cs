using System;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [Serializable]
    internal struct LocalAvoidanceSettings
    {
        internal const int MaxSupportedNeighbors = 16;

        [SerializeField] internal bool EnableLocalAvoidance;
        [SerializeField, Min(0.01f)] internal float CollisionRadius;
        [SerializeField, Min(0.02f)] internal float NeighborRadius;
        [SerializeField, Min(0f)] internal float SeparationStrength;
        [SerializeField, Min(0f)] internal float SteeringStrength;
        [SerializeField, Min(0f)] internal float PredictionHorizon;
        [SerializeField, Range(1, MaxSupportedNeighbors)] internal int MaxNeighbors;

        internal static LocalAvoidanceSettings Default => new LocalAvoidanceSettings
        {
            EnableLocalAvoidance = true,
            CollisionRadius = 0.35f,
            NeighborRadius = 1.8f,
            SeparationStrength = 1f,
            SteeringStrength = 0.75f,
            PredictionHorizon = 0.25f,
            MaxNeighbors = 8
        };

        internal LocalAvoidanceSettings Validated()
        {
            var settings = this;
            settings.CollisionRadius = ClampFinite(CollisionRadius, Default.CollisionRadius, 0.01f, 100f);
            settings.NeighborRadius = ClampFinite(NeighborRadius, Default.NeighborRadius, 2f * settings.CollisionRadius, 200f);
            settings.SeparationStrength = ClampFinite(SeparationStrength, Default.SeparationStrength, 0f, 8f);
            settings.SteeringStrength = ClampFinite(SteeringStrength, Default.SteeringStrength, 0f, 8f);
            settings.PredictionHorizon = ClampFinite(PredictionHorizon, Default.PredictionHorizon, 0f, 2f);
            settings.MaxNeighbors = math.clamp(MaxNeighbors, 1, MaxSupportedNeighbors);
            return settings;
        }

        private static float ClampFinite(float value, float fallback, float min, float max)
            => math.clamp(math.isfinite(value) ? value : fallback, min, max);
    }

    internal enum AvoidanceMove : byte
    {
        Direct,
        Steer,
        Hold
    }

    internal static class LocalAvoidance
    {
        private const float DirectionEpsilonSquared = 0.00000001f;
        private const float PredictionClearanceFactor = 1.1f;
        private const float HeadOnSideThreshold = 0.05f;
        private const float CrossingAlignmentThreshold = 0.75f;
        internal const int SparseCrossingNeighborLimit = 2;

        internal struct Neighbor
        {
            internal SpatialGridEntry Entry;
            internal float DistanceSquared;
        }

        internal static FixedList512Bytes<Neighbor> FindNearest(SpatialGrid grid, Entity entity,
            float2 position, LocalAvoidanceSettings settings, out long candidates)
        {
            FixedList512Bytes<Neighbor> nearest = default;
            candidates = 0;
            if (!grid.TryQuery(position, settings.NeighborRadius, entity, out var neighbors))
                return nearest;

            while (neighbors.MoveNext(out var entry))
            {
                var neighbor = new Neighbor
                {
                    Entry = entry,
                    DistanceSquared = math.distancesq(position, entry.PositionXZ)
                };
                int insertion = nearest.Length;
                if (insertion == settings.MaxNeighbors)
                {
                    if (!ComesBefore(neighbor, nearest[insertion - 1]))
                        continue;
                    insertion--;
                }
                else
                {
                    nearest.Add(default);
                }

                while (insertion > 0 && ComesBefore(neighbor, nearest[insertion - 1]))
                {
                    nearest[insertion] = nearest[insertion - 1];
                    insertion--;
                }
                nearest[insertion] = neighbor;
            }
            candidates = neighbors.CandidateCount;
            return nearest;
        }

        internal static float2 DesiredVelocity(float2 offset, float speed, float deltaTime)
        {
            float distance = math.length(offset);
            if (!math.isfinite(distance) || !(distance > 0f) || !math.isfinite(speed) || !(speed > 0f))
                return float2.zero;

            return (offset / distance) * math.min(speed, distance / deltaTime);
        }

        internal static float2 Steering(Entity entity, float2 position, float2 desired, Entity other,
            float2 otherPosition, float2 otherDesired, LocalAvoidanceSettings settings, bool allowCrossingYield)
        {
            float2 offset = otherPosition - position;
            float distanceSquared = math.lengthsq(offset);
            float distance = math.sqrt(distanceSquared);
            float diameter = 2f * settings.CollisionRadius;
            float2 steering = float2.zero;
            if (distance < diameter && settings.SeparationStrength > 0f)
            {
                float2 away = distanceSquared > DirectionEpsilonSquared ? -offset / distance : CoincidentAxis(entity, other);
                steering = away * ((1f - distance / diameter) * settings.SeparationStrength);
            }

            float2 relative = otherDesired - desired;
            float relativeSquared = math.lengthsq(relative);
            float approach = math.dot(offset, relative);
            if (relativeSquared <= DirectionEpsilonSquared || approach >= 0f ||
                settings.PredictionHorizon <= 0f || settings.SteeringStrength <= 0f)
                return steering;

            float closestTime = math.min(-approach / relativeSquared, settings.PredictionHorizon);
            float clearance = diameter * PredictionClearanceFactor;
            float closestDistance = math.length(offset + relative * closestTime);
            if (closestDistance >= clearance)
                return steering;

            float urgency = 1f - closestDistance / clearance;
            float2 heading = math.normalizesafe(desired);
            float alignment = math.dot(heading, math.normalizesafe(otherDesired));
            if (allowCrossingYield && math.lengthsq(otherDesired) > DirectionEpsilonSquared &&
                math.abs(alignment) < CrossingAlignmentThreshold)
            {
                // Pick one yielding participant. Two symmetric turns can keep a crossing on course.
                return EntityComesBefore(entity, other)
                    ? steering
                    : steering - heading * (urgency * settings.SteeringStrength);
            }

            float2 right = new float2(heading.y, -heading.x);
            float side = math.dot(offset, right);
            float lateralSign = side > HeadOnSideThreshold ? -1f : 1f;
            return steering + right * (lateralSign * urgency * settings.SteeringStrength);
        }

        internal static float2 ClampLength(float2 value, float maxLength)
        {
            float length = math.length(value);
            if (!math.isfinite(length))
                return float2.zero;
            return length > maxLength ? (value / length) * maxLength : value;
        }

        private static bool ComesBefore(Neighbor left, Neighbor right)
        {
            if (left.DistanceSquared != right.DistanceSquared)
                return left.DistanceSquared < right.DistanceSquared;
            return EntityComesBefore(left.Entry.Entity, right.Entry.Entity);
        }

        private static bool EntityComesBefore(Entity left, Entity right)
            => left.Index < right.Index || (left.Index == right.Index && left.Version < right.Version);

        internal static float2 CoincidentAxis(Entity entity, Entity other)
        {
            bool first = EntityComesBefore(entity, other);
            var pair = first ? new int2(entity.Index, other.Index) : new int2(other.Index, entity.Index);
            uint hash = math.hash(pair);
            float2 axis = (hash & 1u) == 0u ? new float2(1f, 0f) : new float2(0f, 1f);
            return first ? axis : -axis;
        }
    }
}
