using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;

namespace ProjectLegion.Gameplay
{
    [WorldSystemFilter(WorldSystemFilterFlags.LocalSimulation)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    internal partial class PrototypeStatisticsSystem : SystemBase
    {
        internal struct Counts
        {
            internal int Total, Selected, Moving, Waiting, Retrying, Idle;
            internal void Add(Counts other)
            {
                Total += other.Total;
                Selected += other.Selected;
                Moving += other.Moving;
                Waiting += other.Waiting;
                Retrying += other.Retrying;
                Idle += other.Idle;
            }
        }

        internal bool Collect { get; set; }
        internal float Interval { get; set; } = 0.25f;
        private EntityQuery _units;
        private NativeArray<Counts> _chunks;
        private NativeArray<Counts> _result;
        private JobHandle _sample;
        private bool _pending, _available;
        private uint _generation, _publishedGeneration;
        private double _next;
        private Counts _published;

        protected override void OnCreate()
        {
            _units = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<PrototypeUnit, UnitSelected, UnitMoveTarget, UnitMovementProgress>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState).Build(this);
        }

        internal bool TryGet(uint generation, out Counts counts)
        {
            counts = _published;
            return _available && generation == _publishedGeneration;
        }

        protected override void OnUpdate()
        {
            uint generation = World.GetExistingSystemManaged<UnitSpawnSystem>()?.Generation ?? 0;
            if (_pending)
            {
                if (!_sample.IsCompleted)
                    return;
                _sample.Complete();
                if (Collect && generation == _generation)
                {
                    _published = _result[0];
                    _publishedGeneration = generation;
                    _available = true;
                }
                _pending = false;
            }
            if (!Collect)
            {
                _available = false;
                _next = 0;
                return;
            }
            double now = SystemAPI.Time.ElapsedTime;
            if (generation == _generation && now < _next)
                return;
            int count = _units.CalculateChunkCountWithoutFiltering();
            if (!_result.IsCreated)
                _result = new NativeArray<Counts>(1, Allocator.Persistent);
            if (!_chunks.IsCreated || count > _chunks.Length)
            {
                int size = count >= 64 ? count * 2 : 64;
                if (_chunks.IsCreated)
                    _chunks.Dispose();
                _chunks = new NativeArray<Counts>(size, Allocator.Persistent);
            }
            var detector = World.GetExistingSystemManaged<UnitStuckDetectionSystem>();
            var countJob = new CountChunks
            {
                Output = _chunks,
                Selected = GetComponentTypeHandle<UnitSelected>(true),
                Targets = GetComponentTypeHandle<UnitMoveTarget>(true),
                Progress = GetComponentTypeHandle<UnitMovementProgress>(true),
                Monitoring = detector is { Monitoring: true },
                Epoch = detector?.Epoch ?? 0
            }.ScheduleParallel(_units, Dependency);
            _sample = new Reduce { Chunks = _chunks, Output = _result, Count = count }.Schedule(countJob);
            Dependency = _sample;
            _pending = true;
            _generation = generation;
            _next = now + Interval;
        }

        protected override void OnDestroy()
        {
            _sample.Complete();
            if (_chunks.IsCreated)
                _chunks.Dispose();
            if (_result.IsCreated)
                _result.Dispose();
        }

        [BurstCompile]
        private struct CountChunks : IJobChunk
        {
            [NativeDisableParallelForRestriction] internal NativeArray<Counts> Output;
            [ReadOnly] internal ComponentTypeHandle<UnitSelected> Selected;
            [ReadOnly] internal ComponentTypeHandle<UnitMoveTarget> Targets;
            [ReadOnly] internal ComponentTypeHandle<UnitMovementProgress> Progress;
            internal bool Monitoring;
            internal uint Epoch;

            public void Execute(in ArchetypeChunk chunk, int index, bool useEnabledMask, in v128 mask)
            {
                var counts = new Counts { Total = chunk.Count };
                var states = chunk.GetNativeArray(ref Progress);
                for (int i = 0; i < chunk.Count; i++)
                {
                    if (chunk.IsComponentEnabled(ref Selected, i))
                        counts.Selected++;
                    if (!chunk.IsComponentEnabled(ref Targets, i))
                        counts.Idle++;
                    else if (!Monitoring || states[i].Epoch != Epoch)
                        counts.Moving++;
                    else if (states[i].State == UnitMovementState.Waiting)
                        counts.Waiting++;
                    else if (states[i].State == UnitMovementState.Retrying)
                        counts.Retrying++;
                    else
                        counts.Moving++;
                }
                Output[index] = counts;
            }
        }

        [BurstCompile]
        private struct Reduce : IJob
        {
            [ReadOnly] internal NativeArray<Counts> Chunks;
            internal NativeArray<Counts> Output;
            internal int Count;
            public void Execute()
            {
                Counts totals = default;
                for (int i = 0; i < Count; i++)
                    totals.Add(Chunks[i]);
                Output[0] = totals;
            }
        }
    }
}
