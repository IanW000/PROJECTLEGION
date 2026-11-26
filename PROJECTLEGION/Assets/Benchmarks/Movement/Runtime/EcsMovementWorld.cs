using System;
using Unity.Collections;
using Unity.Entities;

namespace ProjectLegion.MovementBenchmark
{
    internal sealed class EcsMovementWorld : IDisposable
    {
        internal const string WorldName = "Movement Benchmark";
        private readonly World _world;
        private readonly EcsMovementSystem _system;

        // The array maps the original unit index to its Entity. The World owns the component storage.
        private NativeArray<Entity> _units;

        internal EcsMovementWorld(BenchmarkConfig config, ReadOnlySpan<UnitState> initial, bool recordExecution = false)
        {
            _world = new World(WorldName, WorldFlags.Game);
            try
            {
                _system = _world.GetOrCreateSystemManaged<EcsMovementSystem>();
                _system.Configure(config, recordExecution);

                EntityManager manager = _world.EntityManager;
                EntityArchetype archetype = recordExecution
                    ? manager.CreateArchetype(typeof(MovementState), typeof(MovementExecutionProbe))
                    : manager.CreateArchetype(typeof(MovementState));

                _units = new NativeArray<Entity>(initial.Length, Allocator.Persistent,
                    NativeArrayOptions.UninitializedMemory);
                if (initial.Length > 0)
                    manager.CreateEntity(archetype, _units);

                for (int index = 0; index < initial.Length; index++)
                    manager.SetComponentData(_units[index], new MovementState { Value = initial[index] });
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal int ChunkCount => _system.ChunkCount;

        internal void StartStep()
        {
            _system.Update();
        }

        internal void CompleteStep()
        {
            _system.CompleteStep();
        }

        internal void CopyStates(Span<UnitState> destination)
        {
            CompleteStep();
            if (destination.Length != _units.Length)
                throw new ArgumentException("The destination must contain one entry per unit.", nameof(destination));

            EntityManager manager = _world.EntityManager;
            for (int index = 0; index < _units.Length; index++)
                destination[index] = manager.GetComponentData<MovementState>(_units[index]).Value;
        }

        internal void VerifyExecution(int expectedBackend)
        {
            CompleteStep();
            EntityManager manager = _world.EntityManager;
            for (int index = 0; index < _units.Length; index++)
            {
                int actual = manager.GetComponentData<MovementExecutionProbe>(_units[index]).Backend;
                if (actual != expectedBackend)
                    throw new InvalidOperationException($"Unit {index} executed on backend {actual}, expected {expectedBackend}.");
            }
        }

        public void Dispose()
        {
            try
            {
                if (_world.IsCreated)
                {
                    try
                    {
                        _system?.CompleteStep();
                    }
                    finally
                    {
                        _world.Dispose();
                    }
                }
            }
            finally
            {
                if (_units.IsCreated)
                    _units.Dispose();
            }
        }
    }
}
