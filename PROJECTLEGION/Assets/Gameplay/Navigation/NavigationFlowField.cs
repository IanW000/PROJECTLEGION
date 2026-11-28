using System;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;

namespace ProjectLegion.Gameplay
{
    internal enum NavigationFlowState : byte { Invalid, Goal, Blocked, Unreachable, Direction }
    internal enum NavigationFlowBuildStatus { Success, InvalidGoal, Invalidated, NumericFailure }

    internal struct NavigationFlowStats
    {
        internal int Reachable;
        internal double MaxCost;
        internal bool NumericFailure;
    }

    internal readonly struct NavigationFlowView
    {
        private readonly NativeArray<double>.ReadOnly _integration;
        private readonly NativeArray<byte>.ReadOnly _directions;
        internal readonly int2 Size;
        internal readonly long GridGeneration;

        internal NavigationFlowView(NativeArray<double>.ReadOnly integration,
            NativeArray<byte>.ReadOnly directions, int2 size, long generation)
        {
            _integration = integration;
            _directions = directions;
            Size = size;
            GridGeneration = generation;
        }

        internal NavigationFlowState Query(int2 cell, out int2 next, out double cost)
        {
            next = cell;
            cost = double.PositiveInfinity;
            if (!math.all(cell >= 0 & cell < Size))
                return NavigationFlowState.Invalid;
            int index = cell.y * Size.x + cell.x;
            cost = _integration[index];
            byte value = _directions[index];
            if (value < 8)
            {
                next = cell + NavigationGrid.Offset(value);
                return NavigationFlowState.Direction;
            }
            return value switch
            {
                NavigationFlowField.GoalCode => NavigationFlowState.Goal,
                NavigationFlowField.BlockedCode => NavigationFlowState.Blocked,
                _ => NavigationFlowState.Unreachable
            };
        }

        internal bool TryGetNext(int2 cell, out int2 next) =>
            Query(cell, out next, out _) == NavigationFlowState.Direction;
    }

    internal sealed class NavigationFlowField : IDisposable
    {
        internal const byte GoalCode = 8, BlockedCode = 9, UnreachableCode = 10, ErrorCode = 11;
        internal const int BytesPerNode = sizeof(double) + sizeof(byte);
        private readonly NavigationGrid _grid;
        private NativeArray<double> _integration;
        private NativeArray<byte> _directions;
        private JobHandle _readers;
        private JobHandle _retirement;
        private bool _published, _hasReaders;
        internal int2 Goal { get; }
        internal long GridGeneration { get; }
        internal NavigationFlowStats Stats { get; private set; }
        internal long PayloadBytes => _integration.IsCreated ? (long)_integration.Length * BytesPerNode : 0;
        internal NativeArray<double> Integration => _integration;
        internal NativeArray<byte> Directions => _directions;

        internal NavigationFlowField(NavigationGrid grid, int2 goal)
        {
            _grid = grid;
            Goal = goal;
            GridGeneration = grid.Generation;
            int count = checked(grid.Size.x * grid.Size.y);
            _integration = new NativeArray<double>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            try
            {
                _directions = new NativeArray<byte>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void Publish(NavigationFlowStats stats)
        {
            Stats = stats;
            _published = true;
        }

        // Acquire, schedule, and register each reader on the owner thread before returning control.
        // A view must not be retained for subsequent scheduling. Results from in-flight reads
        // must be revalidated against the current snapshot before consumption.
        internal bool TryGetView(NavigationGrid current, out NavigationFlowView view)
        {
            view = default;
            if (!_published || !ReferenceEquals(current, _grid) || !_grid.AcceptsQueries ||
                current.Generation != GridGeneration)
                return false;
            view = new NavigationFlowView(_integration.AsReadOnly(), _directions.AsReadOnly(), current.Size, GridGeneration);
            return true;
        }

        internal void RegisterReader(NavigationGrid current, JobHandle reader)
        {
            if (!TryGetView(current, out _))
                throw new InvalidOperationException("Flow field snapshot is no longer current.");
            if (_hasReaders && _readers.IsCompleted)
            {
                _readers.Complete();
                _hasReaders = false;
            }
            _readers = _hasReaders ? JobHandle.CombineDependencies(_readers, reader) : reader;
            _hasReaders = true;
        }

        internal JobHandle Retire()
        {
            _published = false;
            if (!_integration.IsCreated)
                return _retirement;
            if (_hasReaders && !_readers.IsCompleted)
            {
                var integration = _integration.Dispose(_readers);
                var directions = _directions.Dispose(_readers);
                _retirement = JobHandle.CombineDependencies(integration, directions);
                JobHandle.ScheduleBatchedJobs();
            }
            else
            {
                if (_hasReaders) _readers.Complete();
                _integration.Dispose();
                if (_directions.IsCreated) _directions.Dispose();
            }
            return _retirement;
        }

        public void Dispose() => Retire().Complete();
    }
}
