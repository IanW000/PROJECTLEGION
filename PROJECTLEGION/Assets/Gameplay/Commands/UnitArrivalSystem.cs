using System;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Transforms;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [Serializable]
    internal struct ArrivalManagementSettings
    {
        internal const int MaxBudget = 512;
        internal const int MaxCandidates = 16;
        [SerializeField] internal bool EnableArrivalManagement;
        [SerializeField, Min(0.1f)] internal float ObservationRadius;
        [SerializeField, Min(0.1f)] internal float OccupancySeconds;
        [SerializeField, Min(0.1f)] internal float SearchRadius;
        [SerializeField, Range(1, MaxCandidates)] internal int CandidateLimit;
        [SerializeField, Range(1, MaxBudget)] internal int RequestBudget;
        [SerializeField, Min(0.05f)] internal float BatchInterval;
        [SerializeField, Min(0.05f)] internal float RetryCooldown;

        internal static ArrivalManagementSettings Default => new ArrivalManagementSettings
        {
            EnableArrivalManagement = true,
            ObservationRadius = 1.8f,
            OccupancySeconds = 1f,
            SearchRadius = 1.8f,
            CandidateLimit = MaxCandidates,
            RequestBudget = 128,
            BatchInterval = 0.1f,
            RetryCooldown = 0.35f
        };

        internal ArrivalManagementSettings Validated()
        {
            var result = this;
            result.ObservationRadius = Clamp(ObservationRadius, 1.8f, 0.1f, 8f);
            result.OccupancySeconds = Clamp(OccupancySeconds, 1f, 0.1f, 20f);
            result.SearchRadius = Clamp(SearchRadius, 1.8f, 0.1f, 8f);
            result.CandidateLimit = math.clamp(CandidateLimit, 1, MaxCandidates);
            result.RequestBudget = math.clamp(RequestBudget, 1, MaxBudget);
            result.BatchInterval = Clamp(BatchInterval, 0.1f, 0.05f, 1f);
            result.RetryCooldown = Clamp(RetryCooldown, 0.35f, 0.05f, 10f);
            return result;
        }

        private static float Clamp(float value, float fallback, float min, float max)
            => math.clamp(math.isfinite(value) ? value : fallback, min, max);
    }

    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(UnitMoveCommandSystem))]
    [UpdateAfter(typeof(UnitSpatialGridSystem))]
    [UpdateBefore(typeof(UnitWaitingProbeSystem))]
    [UpdateBefore(typeof(UnitMovementSystem))]
    internal partial class UnitArrivalSystem : SystemBase
    {
        private const int InitialCapacity = 128;
        private EntityQuery _units;
        private NativeList<Request> _requests;
        private NativeArray<FixedList32Bytes<byte>> _candidates;
        private NativeParallelMultiHashMap<int2, Claim> _claims;
        private NativeArray<BatchMetrics> _metrics;
        private JobHandle _bufferUse;
        private int _capacity;
        private bool _restore;
        private double _nextBatch;
        private ArrivalManagementSettings _settings = ArrivalManagementSettings.Default;

        internal bool Managing => Enabled && _settings.EnableArrivalManagement;

        internal struct BatchMetrics
        {
            internal int Queued, Served, Reserved, Conflicts;
            internal double MaxQueueDelay;
        }

        // Includes hash entries, their links and the minimum power-of-two bucket table.
        // Native headers, allocator overhead and per-entity ECS state are reported separately.
        internal long PersistentBufferBytes => !_requests.IsCreated ? 0 :
            (long)_capacity * UnsafeUtility.SizeOf<Request>() +
            (long)_capacity * 2 * (UnsafeUtility.SizeOf<Claim>() + UnsafeUtility.SizeOf<int2>() + sizeof(int)) +
            math.ceilpow2(_capacity * 4) * (long)sizeof(int) +
            (long)_candidates.Length * UnsafeUtility.SizeOf<FixedList32Bytes<byte>>() + UnsafeUtility.SizeOf<BatchMetrics>();

        internal BatchMetrics ReadMetrics()
        {
            if (!_metrics.IsCreated)
                return default;
            _bufferUse.Complete();
            return _metrics[0];
        }

        internal void Configure(ArrivalManagementSettings settings)
        {
            if (_settings.EnableArrivalManagement != settings.EnableArrivalManagement)
            {
                _restore |= !settings.EnableArrivalManagement;
                _nextBatch = 0;
            }
            _settings = settings.Validated();
        }

        protected override void OnCreate()
        {
            _units = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<UnitTag, LocalTransform>()
                .WithAllRW<UnitMoveTarget>().Build(this);
        }

        protected override void OnUpdate()
        {
            if (_restore)
            {
                var restore = new RestoreJob
                {
                    Targets = GetComponentTypeHandle<UnitMoveTarget>(),
                    Arrival = GetComponentTypeHandle<UnitArrivalState>(),
                    Progress = GetComponentTypeHandle<UnitMovementProgress>(),
                };
                var restored = restore.ScheduleParallel(_units, Dependency);
                if (_requests.IsCreated)
                    _bufferUse = NewClearJob().Schedule(JobHandle.CombineDependencies(restored, _bufferUse));
                Dependency = restored;
                _restore = false;
                return;
            }

            double now = SystemAPI.Time.ElapsedTime;
            if (!Managing || now < _nextBatch || !(SystemAPI.Time.DeltaTime > 0f) ||
                World.GetExistingSystemManaged<UnitSpatialGridSystem>() is not { Enabled: true } grid)
                return;

            int count = _units.CalculateEntityCountWithoutFiltering();
            if (count == 0)
                return;

            ReserveBuffers(count);
            _nextBatch = now + _settings.BatchInterval;
            var snapshot = grid.GetSnapshot();
            float diameter = 2f * (World.GetExistingSystemManaged<UnitMovementSystem>()?.CollisionRadius ??
                LocalAvoidanceSettings.Default.CollisionRadius);
            var gather = new ObserveJob
            {
                Grid = snapshot.Grid,
                Entities = GetEntityTypeHandle(),
                Transforms = GetComponentTypeHandle<LocalTransform>(true),
                Targets = GetComponentTypeHandle<UnitMoveTarget>(),
                Arrival = GetComponentTypeHandle<UnitArrivalState>(),
                Progress = GetComponentTypeHandle<UnitMovementProgress>(),
                Id = GetComponentLookup<UnitId>(true),
                Stats = GetComponentLookup<UnitStats>(true),
                Claims = _claims.AsParallelWriter(),
                Requests = _requests.AsParallelWriter(),
                Settings = _settings,
                Diameter = diameter,
                Now = now
            };
            var input = JobHandle.CombineDependencies(Dependency, snapshot.Build);
            var clear = NewClearJob().Schedule(_bufferUse);
            var observed = gather.ScheduleParallel(_units, JobHandle.CombineDependencies(input, clear));
            var sorted = new OrderRequestsJob { Requests = _requests }.Schedule(observed);
            var generated = new CandidateJob
            {
                Grid = snapshot.Grid,
                Requests = _requests,
                Candidates = _candidates,
                Diameter = diameter,
                Settings = _settings
            }.Schedule(_settings.RequestBudget, 8, sorted);
            _bufferUse = new ArbitrateJob
            {
                Requests = _requests,
                Candidates = _candidates,
                Claims = _claims,
                Targets = GetComponentLookup<UnitMoveTarget>(),
                Arrival = GetComponentLookup<UnitArrivalState>(),
                Progress = GetComponentLookup<UnitMovementProgress>(),
                Metrics = _metrics,
                Settings = _settings,
                Diameter = diameter,
                Now = now
            }.Schedule(generated);
            snapshot.RegisterReader(generated);
            Dependency = _bufferUse;
        }

        private ClearJob NewClearJob() => new ClearJob { Claims = _claims, Requests = _requests, Metrics = _metrics };

        private void ReserveBuffers(int count)
        {
            if (!_requests.IsCreated)
            {
                _capacity = math.max(InitialCapacity, count);
                _requests = new NativeList<Request>(_capacity, Allocator.Persistent);
                _claims = new NativeParallelMultiHashMap<int2, Claim>(_capacity * 2, Allocator.Persistent);
                _candidates = new NativeArray<FixedList32Bytes<byte>>(ArrivalManagementSettings.MaxBudget, Allocator.Persistent);
                _metrics = new NativeArray<BatchMetrics>(1, Allocator.Persistent);
            }
            else if (count > _capacity)
            {
                _bufferUse.Complete();
                _capacity = math.max(count, _capacity * 2);
                _requests.Capacity = _capacity;
                _claims.Capacity = _capacity * 2;
            }
        }

        protected override void OnDestroy()
        {
            _bufferUse.Complete();
            if (!_requests.IsCreated)
                return;
            _requests.Dispose();
            _claims.Dispose();
            _candidates.Dispose();
            _metrics.Dispose();
        }

        private struct Claim
        {
            internal Entity Owner;
            internal float2 Position;
        }

        private struct Request : IComparable<Request>
        {
            internal Entity Unit;
            internal ulong Id;
            internal float2 Position;
            internal float2 Original;
            internal float2 Forward;
            internal float Spacing;
            internal uint Failed;
            internal uint Version;
            internal double Since;

            public int CompareTo(Request other)
            {
                int order = Since.CompareTo(other.Since);
                if (order == 0)
                    order = Id.CompareTo(other.Id);
                if (order == 0)
                    order = Unit.Index.CompareTo(other.Unit.Index);
                return order != 0 ? order : Unit.Version.CompareTo(other.Unit.Version);
            }
        }

        [BurstCompile]
        private struct ClearJob : IJob
        {
            internal NativeList<Request> Requests;
            internal NativeParallelMultiHashMap<int2, Claim> Claims;
            internal NativeArray<BatchMetrics> Metrics;
            public void Execute()
            {
                Requests.Clear();
                Claims.Clear();
                Metrics[0] = default;
            }
        }

        [BurstCompile]
        private struct RestoreJob : IJobChunk
        {
            internal ComponentTypeHandle<UnitMoveTarget> Targets;
            internal ComponentTypeHandle<UnitArrivalState> Arrival;
            internal ComponentTypeHandle<UnitMovementProgress> Progress;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 mask)
            {
                if (!chunk.Has(ref Arrival))
                    return;
                var states = chunk.GetNativeArray(ref Arrival);
                var targets = chunk.GetNativeArray(ref Targets);
                bool hasProgress = chunk.Has(ref Progress);
                var progress = hasProgress ? chunk.GetNativeArray(ref Progress) : default;
                var units = new ChunkEntityEnumerator(useEnabledMask, mask, chunk.Count);
                while (units.NextEntityIndex(out int i))
                {
                    var state = states[i];
                    if (!state.HasCommand)
                        continue;
                    if (state.AlternativeIndex != 0)
                    {
                        targets[i] = new UnitMoveTarget { DestinationXZ = state.OriginalSlot };
                        if (hasProgress)
                            progress[i] = ResetProgress(progress[i]);
                    }
                    state.AlternativeIndex = 0;
                    state.Status = ArrivalStatus.Approaching;
                    state.OccupiedSince = state.QueuedSince = state.NextRequest = state.LastProgressTime = 0;
                    state.BestDistance = 0;
                    states[i] = state;
                }
            }
        }

        internal static UnitMovementProgress ResetProgress(UnitMovementProgress progress)
        {
            progress.Reset(progress.Epoch);
            return progress;
        }

        [BurstCompile]
        private struct ObserveJob : IJobChunk
        {
            private static readonly ProfilerMarker Marker = new ProfilerMarker("RTS.Arrival.Observe");
            internal SpatialGrid Grid;
            [ReadOnly] internal EntityTypeHandle Entities;
            [ReadOnly] internal ComponentTypeHandle<LocalTransform> Transforms;
            [ReadOnly] internal ComponentLookup<UnitId> Id;
            [ReadOnly] internal ComponentLookup<UnitStats> Stats;
            internal ComponentTypeHandle<UnitMoveTarget> Targets;
            internal ComponentTypeHandle<UnitArrivalState> Arrival;
            internal ComponentTypeHandle<UnitMovementProgress> Progress;
            internal NativeParallelMultiHashMap<int2, Claim>.ParallelWriter Claims;
            internal NativeList<Request>.ParallelWriter Requests;
            internal ArrivalManagementSettings Settings;
            internal float Diameter;
            internal double Now;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 mask)
            {
                using var scope = Marker.Auto();
                var entities = chunk.GetNativeArray(Entities);
                var transforms = chunk.GetNativeArray(ref Transforms);
                var targets = chunk.GetNativeArray(ref Targets);
                bool tracked = chunk.Has(ref Arrival);
                bool hasProgress = chunk.Has(ref Progress);
                var states = tracked ? chunk.GetNativeArray(ref Arrival) : default;
                var progress = hasProgress ? chunk.GetNativeArray(ref Progress) : default;
                var units = new ChunkEntityEnumerator(useEnabledMask, mask, chunk.Count);
                while (units.NextEntityIndex(out int i))
                {
                    Entity entity = entities[i];
                    var state = tracked ? states[i] : default;
                    AddClaim(entity, state.HasCommand ? state.OriginalSlot : targets[i].DestinationXZ);
                    if (!state.HasCommand)
                        continue;

                    float2 position = transforms[i].Position.xz;
                    var movement = hasProgress ? progress[i] : default;
                    float speed = Stats.TryGetComponent(entity, out var stats) ? stats.MovementSpeed : 0f;
                    if (state.AlternativeIndex != 0)
                    {
                        float distance = math.distance(position, targets[i].DestinationXZ);
                        if (HasProgress(state, distance, speed))
                        {
                            state.BestDistance = distance;
                            state.LastProgressTime = Now;
                        }
                        if (Now - state.LastProgressTime >= 2.0)
                        {
                            state.FailedCandidates |= 1u << (state.AlternativeIndex - 1);
                            state.FailedAttempts++;
                            state.AlternativeIndex = 0;
                            state.Status = ArrivalStatus.Approaching;
                            state.NextRequest = Now + Settings.RetryCooldown;
                            state.OccupiedSince = state.QueuedSince = 0;
                            targets[i] = new UnitMoveTarget { DestinationXZ = state.OriginalSlot };
                            if (hasProgress)
                                progress[i] = ResetProgress(movement);
                            movement = default;
                        }
                        else
                        {
                            AddClaim(entity, targets[i].DestinationXZ);
                            states[i] = state;
                            continue;
                        }
                    }

                    ObserveOriginal(ref state, entity, position, speed, movement);
                    states[i] = state;
                }
            }

            private bool HasProgress(UnitArrivalState state, float distance, float speed)
            {
                float elapsed = math.max(Settings.BatchInterval, (float)(Now - state.LastProgressTime));
                float threshold = math.clamp(speed * elapsed * 0.1f, 0.00001f, 0.04f);
                return state.BestDistance - distance >= threshold;
            }

            private void ObserveOriginal(ref UnitArrivalState state, Entity entity, float2 position, float speed,
                UnitMovementProgress movement)
            {
                float distance = math.distance(position, state.OriginalSlot);
                if (!(distance <= Settings.ObservationRadius) || !(speed > 0f) || !math.isfinite(speed))
                {
                    ClearObservation(ref state, movement);
                    return;
                }
                if (!TryOccupancy(Grid, entity, state.OriginalSlot, Diameter, out bool occupied))
                {
                    ClearObservation(ref state, movement);
                    return;
                }
                if (!occupied)
                {
                    ClearObservation(ref state, movement);
                    return;
                }

                if (state.OccupiedSince == 0)
                {
                    state.OccupiedSince = Now;
                    state.LastProgressTime = Now;
                    state.BestDistance = distance;
                }
                else if (HasProgress(state, distance, speed))
                {
                    state.LastProgressTime = Now;
                    state.BestDistance = distance;
                    state.QueuedSince = 0;
                }

                if (Now - state.OccupiedSince < Settings.OccupancySeconds ||
                    Now - state.LastProgressTime < Settings.OccupancySeconds)
                {
                    state.Status = ArrivalStatus.TemporarilyOccupied;
                    return;
                }
                if (state.FailedAttempts >= 3 || Now < state.NextRequest)
                {
                    state.Status = ArrivalStatus.NoAlternative;
                    return;
                }

                state.Status = ArrivalStatus.PersistentlyOccupied;
                if (state.QueuedSince == 0)
                    state.QueuedSince = Now;
                Requests.AddNoResize(new Request
                {
                    Unit = entity,
                    Id = Id.TryGetComponent(entity, out var id) ? id.Value : 0,
                    Position = position,
                    Original = state.OriginalSlot,
                    Forward = state.FormationForward,
                    Spacing = state.Spacing,
                    Failed = state.FailedCandidates,
                    Version = state.CommandVersion,
                    Since = state.QueuedSince
                });
            }

            private static void ClearObservation(ref UnitArrivalState state, UnitMovementProgress movement)
            {
                state.OccupiedSince = state.QueuedSince = 0;
                state.Status = movement.State == UnitMovementState.Waiting
                    ? ArrivalStatus.RouteBlocked : ArrivalStatus.Approaching;
            }

            private void AddClaim(Entity entity, float2 position)
            {
                if (SpatialGrid.TryGetCell(position, Diameter, out var cell))
                    Claims.Add(cell, new Claim { Owner = entity, Position = position });
            }
        }

        internal static bool TryOccupancy(SpatialGrid grid, Entity unit, float2 center, float diameter, out bool occupied)
        {
            occupied = false;
            if (!grid.TryQuery(center, diameter, unit, out var candidates))
                return false;
            while (candidates.MoveNext(out var candidate))
            {
                if (math.distancesq(center, candidate.PositionXZ) < diameter * diameter)
                {
                    occupied = true;
                    break;
                }
            }
            return true;
        }

        [BurstCompile]
        private struct OrderRequestsJob : IJob
        {
            private static readonly ProfilerMarker Marker = new ProfilerMarker("RTS.Arrival.Order");
            internal NativeList<Request> Requests;
            public void Execute()
            {
                using var scope = Marker.Auto();
                if (Requests.Length > 1)
                    Requests.AsArray().Sort();
            }
        }

        [BurstCompile]
        private struct CandidateJob : IJobParallelFor
        {
            private static readonly ProfilerMarker Marker = new ProfilerMarker("RTS.Arrival.Search");
            [ReadOnly] internal NativeList<Request> Requests;
            internal NativeArray<FixedList32Bytes<byte>> Candidates;
            internal SpatialGrid Grid;
            internal ArrivalManagementSettings Settings;
            internal float Diameter;

            public void Execute(int i)
            {
                if (i >= Requests.Length)
                    return;
                using var scope = Marker.Auto();
                var request = Requests[i];
                FixedList32Bytes<byte> options = default;
                float spacing = math.max(Diameter, request.Spacing);
                for (byte index = 0; index < Settings.CandidateLimit; index++)
                {
                    if ((request.Failed & (1u << index)) != 0 ||
                        math.lengthsq((float2)Offset(index) * spacing) > Settings.SearchRadius * Settings.SearchRadius)
                        continue;
                    float2 candidate = CandidatePosition(request, index, spacing);
                    if (TryOccupancy(Grid, request.Unit, candidate, Diameter, out bool occupied) && !occupied &&
                        StuckProbe.PathIsClear(Grid, request.Unit, request.Position, candidate - request.Position, Diameter))
                        options.Add(index);
                }
                Candidates[i] = options;
            }
        }

        [BurstCompile]
        private struct ArbitrateJob : IJob
        {
            private static readonly ProfilerMarker Marker = new ProfilerMarker("RTS.Arrival.Arbitrate");
            [ReadOnly] internal NativeList<Request> Requests;
            [ReadOnly] internal NativeArray<FixedList32Bytes<byte>> Candidates;
            internal NativeParallelMultiHashMap<int2, Claim> Claims;
            internal ComponentLookup<UnitMoveTarget> Targets;
            internal ComponentLookup<UnitArrivalState> Arrival;
            internal ComponentLookup<UnitMovementProgress> Progress;
            internal NativeArray<BatchMetrics> Metrics;
            internal ArrivalManagementSettings Settings;
            internal float Diameter;
            internal double Now;

            public void Execute()
            {
                using var scope = Marker.Auto();
                var metrics = new BatchMetrics { Queued = Requests.Length };
                int count = math.min(Settings.RequestBudget, Requests.Length);
                for (int i = 0; i < count; i++)
                {
                    var request = Requests[i];
                    if (!Arrival.TryGetComponent(request.Unit, out var state) || state.CommandVersion != request.Version ||
                        !Targets.IsComponentEnabled(request.Unit))
                        continue;

                    metrics.Served++;
                    metrics.MaxQueueDelay = math.max(metrics.MaxQueueDelay, Now - request.Since);
                    var options = Candidates[i];
                    state.Status = ArrivalStatus.NoAlternative;
                    state.QueuedSince = 0;
                    state.NextRequest = Now + Settings.RetryCooldown;
                    for (int j = 0; j < options.Length; j++)
                    {
                        byte index = options[j];
                        float2 candidate = CandidatePosition(request, index, math.max(Diameter, request.Spacing));
                        if (!SpatialGrid.TryGetCell(candidate, Diameter, out var cell) ||
                            Conflicts(request.Unit, candidate, cell))
                        {
                            metrics.Conflicts++;
                            continue;
                        }

                        Claims.Add(cell, new Claim { Owner = request.Unit, Position = candidate });
                        Targets[request.Unit] = new UnitMoveTarget { DestinationXZ = candidate };
                        state.Status = ArrivalStatus.Alternative;
                        state.AlternativeIndex = (byte)(index + 1);
                        state.BestDistance = math.distance(request.Position, candidate);
                        state.LastProgressTime = Now;
                        if (Progress.TryGetComponent(request.Unit, out var progress))
                            Progress[request.Unit] = ResetProgress(progress);
                        metrics.Reserved++;
                        break;
                    }
                    Arrival[request.Unit] = state;
                }
                Metrics[0] = metrics;
            }

            private bool Conflicts(Entity unit, float2 position, int2 cell)
            {
                float diameterSquared = Diameter * Diameter;
                for (int y = -1; y <= 1; y++)
                for (int x = -1; x <= 1; x++)
                {
                    if (!Claims.TryGetFirstValue(cell + new int2(x, y), out var claim, out var iterator))
                        continue;
                    do
                    {
                        if (claim.Owner != unit && math.distancesq(position, claim.Position) < diameterSquared)
                            return true;
                    } while (Claims.TryGetNextValue(out claim, ref iterator));
                }
                return false;
            }
        }

        private static float2 CandidatePosition(Request request, int index, float spacing)
        {
            int2 offset = Offset(index);
            float2 right = new float2(request.Forward.y, -request.Forward.x);
            return request.Original + (right * offset.x + request.Forward * offset.y) * spacing;
        }

        private static int2 Offset(int index)
        {
            switch (index)
            {
                case 0: return new int2(-1, 0);
                case 1: return new int2(1, 0);
                case 2: return new int2(0, -1);
                case 3: return new int2(0, 1);
                case 4: return new int2(-1, -1);
                case 5: return new int2(1, -1);
                case 6: return new int2(-1, 1);
                case 7: return new int2(1, 1);
                case 8: return new int2(-2, 0);
                case 9: return new int2(2, 0);
                case 10: return new int2(0, -2);
                case 11: return new int2(0, 2);
                case 12: return new int2(-2, -1);
                case 13: return new int2(2, -1);
                case 14: return new int2(-2, 1);
                default: return new int2(2, 1);
            }
        }
    }
}
