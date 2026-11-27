using System;
using System.Collections.Generic;
using System.Diagnostics;
using NUnit.Framework;
using Unity.Burst;
using Unity.Collections;
using Unity.Core;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;

namespace ProjectLegion.Gameplay.Tests
{
    public sealed class RtsSpatialGridTests
    {
        [TestCase(0f, 0f, 0, 0)]
        [TestCase(-0.001f, 0.999f, -1, 0)]
        [TestCase(-1f, 1f, -1, 1)]
        [TestCase(-1.001f, -0.999f, -2, -1)]
        public void CellCoordinatesRoundDown(float x, float z, int expectedX, int expectedZ)
        {
            Assert.That(SpatialGrid.TryGetCell(new double2(x, z), 1f, out int2 cell), Is.True);
            Assert.That(cell, Is.EqualTo(new int2(expectedX, expectedZ)));
        }

        [Test]
        public void QueriesCrossCellsFilterDistanceAndExcludeOnlyTheRequestedEntity()
        {
            using var world = new World("Spatial query test");
            var system = world.GetOrCreateSystemManaged<UnitSpatialGridSystem>();
            system.SetCellSize(1f);
            Entity self = CreateUnit(world, new float2(0.75f));
            Entity samePosition = CreateUnit(world, new float2(0.75f));
            Entity sameCell = CreateUnit(world, new float2(0.875f, 0.75f));
            Entity onRadius = CreateUnit(world, new float2(1.25f, 0.75f));
            CreateUnit(world, new float2(1.25f, 1.25f));
            CreateUnit(world, new float2(0.75f), typeof(Prefab));
            CreateUnit(world, new float2(0.75f), typeof(Disabled));
            system.Update();
            SpatialGrid grid = CompleteSnapshot(system);

            Assert.That(ReadNeighbors(grid, new float2(0.75f), 0.5f, self),
                Is.EquivalentTo(new[] { samePosition, sameCell, onRadius }));
            Assert.That(ReadNeighbors(grid, new float2(0.75f), 0f, self),
                Is.EquivalentTo(new[] { samePosition }));
        }

        [Test]
        public void InvalidCoordinatesAndOversizedQueriesAreRejected()
        {
            using var world = new World("Spatial bounds test");
            var system = world.GetOrCreateSystemManaged<UnitSpatialGridSystem>();
            Assert.Throws<ArgumentOutOfRangeException>(() => system.SetCellSize(0f));
            Assert.Throws<ArgumentOutOfRangeException>(() => system.SetCellSize(-1f));
            Assert.Throws<ArgumentOutOfRangeException>(() => system.SetCellSize(float.PositiveInfinity));
            Assert.Throws<ArgumentOutOfRangeException>(() => system.SetCellSize(float.NaN));
            system.SetCellSize(1f);
            CreateUnit(world, new float2(float.NaN, 0f));
            CreateUnit(world, new float2(0f, float.PositiveInfinity));
            CreateUnit(world, new float2(float.MaxValue, 0f));
            Entity atLimit = CreateUnit(world, new float2(SpatialGrid.MaxCellCoordinate, 0f));
            system.Update();
            SpatialGrid grid = CompleteSnapshot(system);

            Assert.That(ReadNeighbors(grid, float2.zero, 0f, Entity.Null), Is.Empty);
            Assert.That(ReadNeighbors(grid, new float2(SpatialGrid.MaxCellCoordinate, 0f), 0f, Entity.Null),
                Is.EquivalentTo(new[] { atLimit }));
            Assert.That(grid.TryQuery(new float2(float.NaN), 1f, Entity.Null, out _), Is.False);
            Assert.That(grid.TryQuery(new float2(float.MaxValue), 1f, Entity.Null, out _), Is.False);
            foreach (float radius in new[] { -1f, float.NaN, float.PositiveInfinity, float.MaxValue, 32f })
                Assert.That(grid.TryQuery(float2.zero, radius, Entity.Null, out _), Is.False);
            Assert.That(grid.TryQuery(new float2(0.5f), 31.5f, Entity.Null, out _), Is.True);
        }

        [Test]
        public void SimulationGroupSnapshotsBeforeMovementAndRefreshesOnSpawnAndDestruction()
        {
            using var world = new World("Spatial update order test");
            var group = world.GetOrCreateSystemManaged<SimulationSystemGroup>();
            var system = world.GetOrCreateSystemManaged<UnitSpatialGridSystem>();
            var movement = world.GetOrCreateSystemManaged<UnitMovementSystem>();
            var spawn = world.GetOrCreateSystemManaged<UnitSpawnSystem>();
            var commands = world.GetOrCreateSystemManaged<UnitMoveCommandSystem>();
            var transforms = world.GetOrCreateSystemManaged<TransformSystemGroup>();
            var manager = world.EntityManager;
            system.SetCellSize(1f);
            group.AddSystemToUpdateList(transforms);
            group.AddSystemToUpdateList(system);
            group.AddSystemToUpdateList(movement);
            group.AddSystemToUpdateList(spawn);
            group.AddSystemToUpdateList(commands);
            group.SortSystems();

            using (var order = group.GetAllSystems())
            {
                int commandsIndex = FindSystemIndex(order, commands.SystemHandle);
                int movementIndex = FindSystemIndex(order, movement.SystemHandle);
                int spawnIndex = FindSystemIndex(order, spawn.SystemHandle);
                int gridIndex = FindSystemIndex(order, system.SystemHandle);
                int transformsIndex = FindSystemIndex(order, transforms.SystemHandle);
                Assert.That(commandsIndex, Is.LessThan(movementIndex));
                Assert.That(gridIndex, Is.LessThan(movementIndex));
                Assert.That(spawnIndex, Is.LessThan(gridIndex));
                Assert.That(movementIndex, Is.LessThan(transformsIndex));
            }

            world.SetTime(new TimeData(1, 1));

            group.Update();
            Assert.That(ReadNeighbors(CompleteSnapshot(system), float2.zero, 1f, Entity.Null), Is.Empty);

            Entity moving = CreateUnit(world, float2.zero, typeof(UnitStats), typeof(UnitMoveTarget));
            manager.SetComponentData(moving, new UnitStats { MovementSpeed = 2f });
            manager.SetComponentData(moving, new UnitMoveTarget { DestinationXZ = new float2(2f, 0f) });
            Entity prefab = CreateUnit(world, float2.zero, typeof(Prefab), typeof(UnitId), typeof(UnitMoveTarget));
            manager.SetComponentEnabled<UnitMoveTarget>(prefab, false);
            Entity request = manager.CreateEntity(typeof(UnitSpawnRequest));
            manager.SetComponentData(request, new UnitSpawnRequest
            {
                Prefab = prefab,
                Count = 1,
                Spacing = 1f,
                Origin = new float3(-2f, 0f, 0f)
            });

            group.Update();
            SpatialGrid grid = CompleteSnapshot(system);
            Assert.That(ReadNeighbors(grid, float2.zero, 0f, Entity.Null), Is.EquivalentTo(new[] { moving }));
            Assert.That(ReadNeighbors(grid, new float2(2f, 0f), 0f, Entity.Null), Is.Empty);
            Assert.That(manager.GetComponentData<LocalTransform>(moving).Position.x, Is.EqualTo(2f));
            var spawned = ReadNeighbors(grid, new float2(-2f, 0f), 0f, Entity.Null);
            Assert.That(spawned.Count, Is.EqualTo(1));
            Assert.That(manager.IsComponentEnabled<UnitMoveTarget>(spawned[0]), Is.False);

            manager.DestroyEntity(moving);
            manager.DestroyEntity(spawned[0]);
            group.Update();
            Assert.That(ReadNeighbors(CompleteSnapshot(system), float2.zero, 3f, Entity.Null), Is.Empty);
        }

        [Test]
        public void ReadersAreProtectedDuringRebuildGrowthAndWorldDisposal()
        {
            using var result = new NativeArray<QueryCounts>(1, Allocator.TempJob);
            using var centers = new NativeArray<float2>(1, Allocator.TempJob);
            using var otherWorld = new World("Independent grid");
            var other = otherWorld.GetOrCreateSystemManaged<UnitSpatialGridSystem>();
            using (var world = new World("Spatial reader test"))
            {
                var system = world.GetOrCreateSystemManaged<UnitSpatialGridSystem>();
                CreateUnit(world, float2.zero);
                system.Update();
                var first = system.GetSnapshot();
                system.SetCellSize(2f);
                Assert.That(system.GetSnapshot().Grid.CellSize, Is.EqualTo(UnitSpatialGridSystem.DefaultCellSize));
                ScheduleReader(first, result, centers);
                system.Update();
                system.GetSnapshot().Build.Complete();
                Assert.That(result[0].Matches, Is.EqualTo(1));
                Assert.That(system.GetSnapshot().Grid.CellSize, Is.EqualTo(2f));
                Assert.Throws<InvalidOperationException>(() => first.RegisterReader(default));

                using var added = new NativeArray<Entity>(2048, Allocator.Temp);
                world.EntityManager.CreateEntity(world.EntityManager.CreateArchetype(typeof(UnitTag),
                    typeof(LocalTransform)), added);
                ScheduleReader(system.GetSnapshot(), result, centers);
                system.Update();
                Assert.That(result[0].Matches, Is.EqualTo(1));
                ScheduleReader(system.GetSnapshot(), result, centers);
                other.Update();
                Assert.That(ReadNeighbors(CompleteSnapshot(other), float2.zero, 0f, Entity.Null), Is.Empty);
            }

            Assert.That(result[0].Matches, Is.EqualTo(2049));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void TenThousandUnitsMatchBruteForceAndReportTimings(bool dense)
        {
            const int count = 10_000;
            const int sampleFrames = 32;
            const int queryCount = 128;
            using var world = new World("10,000 spatial units");
            var system = world.GetOrCreateSystemManaged<UnitSpatialGridSystem>();
            var manager = world.EntityManager;
            using var units = new NativeArray<Entity>(count, Allocator.TempJob);
            using var results = new NativeArray<QueryCounts>(queryCount, Allocator.TempJob);
            var queryCenters = new float2[queryCount];
            var positions = new float2[count];
            var random = new Unity.Mathematics.Random(18731);
            float spread = dense ? 2f : 75f;

            system.Update();
            CompleteSnapshot(system);
            manager.CreateEntity(manager.CreateArchetype(typeof(UnitTag), typeof(LocalTransform)), units);
            for (int i = 0; i < count; i++)
            {
                positions[i] = random.NextFloat2(new float2(-spread), new float2(spread));
                manager.SetComponentData(units[i], LocalTransform.FromPosition(new float3(positions[i].x, 0f, positions[i].y)));
            }

            var watch = Stopwatch.StartNew();
            system.Update();
            SpatialGrid grid = CompleteSnapshot(system);
            double growthMs = watch.Elapsed.TotalMilliseconds;

            for (int sample = 0; sample < queryCount; sample++)
            {
                int source = sample * 73;
                float2 center = queryCenters[sample] = positions[source];
                float radius = sample % 4 * 0.6f;
                var expected = new HashSet<Entity>();
                for (int i = 0; i < count; i++)
                {
                    double2 offset = (double2)positions[i] - (double2)center;
                    if (i != source && math.lengthsq(offset) <= (double)radius * radius)
                        expected.Add(units[i]);
                }

                var actual = ReadNeighbors(grid, center, radius, units[source]);
                var unique = new HashSet<Entity>(actual);
                Assert.That(unique.Count, Is.EqualTo(actual.Count), $"Duplicate in sample {sample}");
                Assert.That(unique.SetEquals(expected), Is.True, $"Result differs in sample {sample}");
            }

            // Warm up the actual 10,000-entry build and the Burst query job before steady samples.
            using var queries = new NativeArray<float2>(queryCenters, Allocator.TempJob);
            for (int i = 0; i < 4; i++)
            {
                system.Update();
                TimeQueryBatch(system.GetSnapshot(), queries, results);
            }

            double buildMs = 0;
            double queryMs = 0;
            long completionTicks = 0;
            long buildBytes = 0;
            for (int i = 0; i < sampleFrames; i++)
            {
                long allocated = GC.GetAllocatedBytesForCurrentThread();
                watch.Restart();
                system.Update();
                var snapshot = system.GetSnapshot();
                long completionStart = Stopwatch.GetTimestamp();
                snapshot.Build.Complete();
                completionTicks += Stopwatch.GetTimestamp() - completionStart;
                buildMs += watch.Elapsed.TotalMilliseconds;
                buildBytes += GC.GetAllocatedBytesForCurrentThread() - allocated;
                queryMs += TimeQueryBatch(snapshot, queries, results);
            }

            long candidates = 0;
            long maxCandidates = 0;
            foreach (QueryCounts counts in results)
            {
                candidates += counts.Candidates;
                maxCandidates = Math.Max(maxCandidates, counts.Candidates);
            }
            UnityEngine.Debug.Log($"Spatial grid {(dense ? "dense" : "spread")}: first growth+build {growthMs:F3} ms, " +
                $"steady build {buildMs / sampleFrames:F3} ms ({(double)buildBytes / sampleFrames:F1} main-thread managed bytes), " +
                $"build Complete {1000.0 * completionTicks / Stopwatch.Frequency / sampleFrames:F3} ms, " +
                $"steady {queryCount} queries " +
                $"{queryMs / sampleFrames:F3} ms, candidates/query mean {(double)candidates / queryCount:F1}, " +
                $"max {maxCandidates} ({sampleFrames} samples, schedule-to-completion).");
        }

        private static double TimeQueryBatch(UnitSpatialGridSystem.Snapshot snapshot, NativeArray<float2> centers,
            NativeArray<QueryCounts> results)
        {
            var watch = Stopwatch.StartNew();
            var handle = new QueryJob { Centers = centers, Radius = 1.35f, Results = results, Grid = snapshot.Grid }
                .Schedule(centers.Length, 16, snapshot.Build);
            snapshot.RegisterReader(handle);
            handle.Complete();
            return watch.Elapsed.TotalMilliseconds;
        }

        private static void ScheduleReader(UnitSpatialGridSystem.Snapshot snapshot, NativeArray<QueryCounts> results,
            NativeArray<float2> centers)
        {
            var handle = new QueryJob { Centers = centers, Results = results, Grid = snapshot.Grid }
                .Schedule(1, 1, snapshot.Build);
            snapshot.RegisterReader(handle);
        }

        private static SpatialGrid CompleteSnapshot(UnitSpatialGridSystem system)
        {
            var snapshot = system.GetSnapshot();
            snapshot.Build.Complete();
            return snapshot.Grid;
        }

        private static int FindSystemIndex(NativeList<SystemHandle> order, SystemHandle system)
        {
            for (int i = 0; i < order.Length; i++)
            {
                if (order[i] == system)
                    return i;
            }

            throw new AssertionException("Expected system is missing from the simulation update order.");
        }

        private static List<Entity> ReadNeighbors(SpatialGrid grid, float2 center, float radius, Entity excluded)
        {
            Assert.That(grid.TryQuery(center, radius, excluded, out var neighbors), Is.True);
            var results = new List<Entity>();
            while (neighbors.MoveNext(out var entry))
                results.Add(entry.Entity);
            return results;
        }

        private static Entity CreateUnit(World world, float2 position, params ComponentType[] extra)
        {
            var components = new ComponentType[extra.Length + 2];
            components[0] = typeof(UnitTag);
            components[1] = typeof(LocalTransform);
            extra.CopyTo(components, 2);
            Entity entity = world.EntityManager.CreateEntity(components);
            world.EntityManager.SetComponentData(entity, LocalTransform.FromPosition(new float3(position.x, 0f, position.y)));
            return entity;
        }

        private struct QueryCounts
        {
            internal int Matches;
            internal long Candidates;
        }

        [BurstCompile(CompileSynchronously = true)]
        private struct QueryJob : IJobParallelFor
        {
            [ReadOnly] internal NativeArray<float2> Centers;
            internal float Radius;
            internal SpatialGrid Grid;
            [WriteOnly] internal NativeArray<QueryCounts> Results;

            public void Execute(int index)
            {
                Grid.TryQuery(Centers[index], Radius, Entity.Null, out var neighbors);
                int matches = 0;
                while (neighbors.MoveNext(out _))
                    matches++;
                Results[index] = new QueryCounts { Matches = matches, Candidates = neighbors.CandidateCount };
            }
        }
    }
}
