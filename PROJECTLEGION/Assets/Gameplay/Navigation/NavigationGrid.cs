using System;
using Unity.Collections;
using Unity.Mathematics;

namespace ProjectLegion.Gameplay
{
    internal readonly struct NavigationNode
    {
        internal readonly float Height;
        internal readonly float TraversalCost;
        internal readonly bool HasSurface;
        internal readonly bool Walkable;

        internal NavigationNode(float height, float cost, byte flags)
        {
            Height = height;
            TraversalCost = cost;
            HasSurface = (flags & 1) != 0;
            Walkable = (flags & 2) != 0;
        }
    }

    /// <summary>Owns a static, single-surface navigation graph. Dispose once after all queries.</summary>
    internal sealed class NavigationGrid : IDisposable
    {
        internal const float DefaultTraversalCost = 1f;
        internal const float MaxTraversalCost = 64f;
        internal static float ValidateCost(float cost) => math.isfinite(cost)
            ? math.clamp(cost, DefaultTraversalCost, MaxTraversalCost) : DefaultTraversalCost;

        private NativeArray<float> _heights, _costs;
        private NativeArray<byte> _flags, _connections;
        internal int2 Size { get; }
        internal float2 Origin { get; }
        internal float CellSize { get; }
        internal long PayloadBytes => _heights.IsCreated ? (long)_heights.Length * 10 : 0;

        internal NavigationGrid(int2 size, float2 origin, float cellSize)
        {
            Size = size;
            Origin = origin;
            CellSize = cellSize;
            int length = size.x * size.y;
            try
            {
                _heights = new NativeArray<float>(length, Allocator.Persistent);
                _costs = new NativeArray<float>(length, Allocator.Persistent);
                _flags = new NativeArray<byte>(length, Allocator.Persistent);
                _connections = new NativeArray<byte>(length, Allocator.Persistent);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal bool Contains(int2 cell) => math.all(cell >= 0 & cell < Size);
        private int Index(int2 cell) => cell.y * Size.x + cell.x;
        internal float2 Center(int2 cell) => Origin + ((float2)cell + 0.5f) * CellSize;

        internal bool TryWorldToCell(float2 position, out int2 cell)
        {
            cell = default;
            double2 fractional = ((double2)position - (double2)Origin) / CellSize;
            if (!math.all(fractional >= 0 & fractional < (double2)Size))
                return false;
            cell = (int2)fractional;
            return true;
        }

        internal bool TryGetNode(int2 cell, out NavigationNode node)
        {
            node = default;
            if (!Contains(cell))
                return false;
            int index = Index(cell);
            node = new NavigationNode(_heights[index], _costs[index], _flags[index]);
            return true;
        }

        internal void SetNode(int2 cell, float height, float cost, byte flags)
        {
            int index = Index(cell);
            _heights[index] = height;
            _costs[index] = cost;
            _flags[index] = flags;
        }

        // Cardinal directions are 0–3, diagonals 4–7. Opposite directions differ by 2.
        internal static int2 Offset(int direction)
        {
            switch (direction)
            {
                case 0: return new int2(1, 0);
                case 1: return new int2(0, 1);
                case 2: return new int2(-1, 0);
                case 3: return new int2(0, -1);
                case 4: return new int2(1, 1);
                case 5: return new int2(-1, 1);
                case 6: return new int2(-1, -1);
                default: return new int2(1, -1);
            }
        }

        internal bool IsConnected(int2 cell, int direction) =>
            _connections.IsCreated && (uint)direction < 8 && Contains(cell) &&
            (_connections[Index(cell)] & (1 << direction)) != 0;

        internal void Connect(int2 from, int2 to, int direction)
        {
            int a = Index(from);
            int b = Index(to);
            _connections[a] |= (byte)(1 << direction);
            _connections[b] |= (byte)(1 << (direction ^ 2));
        }

        internal bool TryGetNeighbor(int2 from, int direction, out int2 to, out double cost)
        {
            to = default;
            cost = 0;
            if (!IsConnected(from, direction))
                return false;

            to = from + Offset(direction);
            int a = Index(from);
            int b = Index(to);
            double run = CellSize;
            double rise = (double)_heights[b] - _heights[a];
            double length = math.sqrt(run * run * (direction < 4 ? 1 : 2) + rise * rise);
            cost = length * ((_costs[a] + _costs[b]) * 0.5);
            return true;
        }

        public void Dispose()
        {
            if (_heights.IsCreated) _heights.Dispose();
            if (_costs.IsCreated) _costs.Dispose();
            if (_flags.IsCreated) _flags.Dispose();
            if (_connections.IsCreated) _connections.Dispose();
        }
    }
}
