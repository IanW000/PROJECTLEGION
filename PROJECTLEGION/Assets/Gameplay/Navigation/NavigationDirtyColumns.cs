using System;
using Unity.Mathematics;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    internal sealed class NavigationDirtyColumns
    {
        internal readonly bool[] Sample, Repair;
        private readonly int2 _size;
        private readonly float2 _origin;
        private readonly float _cell, _margin;
        internal int SampleCount { get; private set; }
        internal int RepairCount { get; private set; }
        internal long PayloadBytes => (long)Sample.Length + Repair.Length;

        internal NavigationDirtyColumns(int2 size, float2 origin, float cell, float radius)
        {
            _size = size; _origin = origin; _cell = cell;
            _margin = radius + .005f;
            Sample = new bool[checked(size.x * size.y)];
            Repair = new bool[Sample.Length];
        }

        internal void Add(Bounds bounds)
        {
            // Column centers have a half-cell offset. Include tangent probes and an extra full cell
            // beyond cross-cell probe reach for both orthogonal paths used by diagonal validation.
            float2 min = (new float2(bounds.min.x, bounds.min.z) - _origin - _margin) / _cell - .5f;
            float2 max = (new float2(bounds.max.x, bounds.max.z) - _origin + _margin) / _cell - .5f;
            Mark(Sample, min, max, 0);
            Mark(Repair, min, max, 2);
        }

        private void Mark(bool[] target, float2 min, float2 max, int halo)
        {
            min = math.floor(min) - halo;
            max = math.ceil(max) + halo;
            if (math.any(max < 0 | min >= _size)) return;
            int2 a = (int2)math.clamp(min, 0, (float2)(_size - 1));
            int2 b = (int2)math.clamp(max, 0, (float2)(_size - 1));
            for (int z = a.y; z <= b.y; z++)
            for (int x = a.x; x <= b.x; x++)
            {
                int column = z * _size.x + x;
                if (target[column]) continue;
                target[column] = true;
                if (target == Sample) SampleCount++;
                else RepairCount++;
            }
        }
    }
}
