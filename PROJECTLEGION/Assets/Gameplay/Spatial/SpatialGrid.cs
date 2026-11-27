using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace ProjectLegion.Gameplay
{
    internal struct SpatialGridEntry
    {
        internal Entity Entity;
        internal float2 PositionXZ;
    }

    internal readonly struct SpatialGrid
    {
        internal const int MaxCellCoordinate = 1_000_000_000;
        internal const int MaxQueryCells = 4096;

        private readonly NativeParallelMultiHashMap<int2, SpatialGridEntry>.ReadOnly _entries;
        internal float CellSize { get; }

        internal SpatialGrid(NativeParallelMultiHashMap<int2, SpatialGridEntry>.ReadOnly entries, float cellSize)
        {
            _entries = entries;
            CellSize = cellSize;
        }

        internal static bool TryGetCell(double2 position, float cellSize, out int2 cell)
        {
            cell = default;
            if (!(cellSize > 0f) || !math.isfinite(cellSize))
                return false;

            // Check before conversion, including when coordinates are non-finite.
            double2 coordinate = math.floor(position / cellSize);
            if (!math.all(coordinate >= -MaxCellCoordinate & coordinate <= MaxCellCoordinate))
                return false;

            cell = (int2)coordinate;
            return true;
        }

        internal bool TryQuery(float2 center, float radius, Entity excluded, out Neighbors neighbors)
        {
            neighbors = default;
            if (!(radius >= 0f) || !math.isfinite(radius))
                return false;

            double2 origin = center;
            if (!TryGetCell(origin - radius, CellSize, out int2 min) ||
                !TryGetCell(origin + radius, CellSize, out int2 max))
                return false;

            long cellCount = (max.x - (long)min.x + 1) * (max.y - (long)min.y + 1);
            if (cellCount > MaxQueryCells)
                return false;

            neighbors = new Neighbors(_entries, min, max, center, radius, excluded);
            return true;
        }

        internal struct Neighbors
        {
            private readonly NativeParallelMultiHashMap<int2, SpatialGridEntry>.ReadOnly _entries;
            private readonly int2 _max;
            private readonly int _minX;
            private readonly double2 _center;
            private readonly double _radiusSquared;
            private readonly Entity _excluded;
            private int2 _nextCell;
            private NativeParallelMultiHashMapIterator<int2> _iterator;
            private bool _continueCell;

            internal long CandidateCount { get; private set; }

            internal Neighbors(NativeParallelMultiHashMap<int2, SpatialGridEntry>.ReadOnly entries,
                int2 min, int2 max, float2 center, float radius, Entity excluded)
            {
                _entries = entries;
                _max = max;
                _minX = min.x;
                _center = center;
                _radiusSquared = (double)radius * radius;
                _excluded = excluded;
                _nextCell = min;
                _iterator = default;
                _continueCell = false;
                CandidateCount = 0;
            }

            internal bool MoveNext(out SpatialGridEntry entry)
            {
                while (true)
                {
                    if (_continueCell)
                    {
                        _continueCell = _entries.TryGetNextValue(out entry, ref _iterator);
                    }
                    else
                    {
                        entry = default;
                        if (_nextCell.y > _max.y || !_entries.IsCreated)
                            return false;

                        _continueCell = _entries.TryGetFirstValue(_nextCell, out entry, out _iterator);
                        if (++_nextCell.x > _max.x)
                            _nextCell = new int2(_minX, _nextCell.y + 1);
                    }

                    if (!_continueCell)
                        continue;

                    CandidateCount++;
                    double2 offset = (double2)entry.PositionXZ - _center;
                    if (entry.Entity != _excluded && math.lengthsq(offset) <= _radiusSquared)
                        return true;
                }
            }
        }
    }
}
