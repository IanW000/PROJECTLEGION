using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace ProjectLegion.Gameplay
{
    internal static class FormationSlotAssignment
    {
        internal struct Unit
        {
            public Entity Entity;
            public ulong Id;
            public float2 Position;
            public float2 Projection;
        }

        internal static RectangularFormation Sort(NativeArray<Unit> units, float2 destination, float spacing)
        {
            // A canonical summation order keeps the centroid independent of chunk iteration order.
            units.Sort(new IdOrder());
            double2 sum = default;
            foreach (Unit unit in units)
                sum += (double2)unit.Position;

            float2 centroid = (float2)(sum / units.Length);
            var formation = new RectangularFormation(units.Length, destination, destination - centroid, spacing);
            for (int i = 0; i < units.Length; i++)
            {
                Unit unit = units[i];
                float2 offset = unit.Position - centroid;
                unit.Projection = new float2(math.dot(offset, formation.Right), math.dot(offset, formation.Forward));
                units[i] = unit;
            }

            units.Sort(new FrontToBackOrder());
            for (int start = 0; start < units.Length; start += formation.Columns)
                units.GetSubArray(start, formation.RowLength(start)).Sort(new LeftToRightOrder());
            return formation;
        }

        private struct IdOrder : IComparer<Unit>
        {
            public int Compare(Unit left, Unit right) => left.Id.CompareTo(right.Id);
        }

        private struct FrontToBackOrder : IComparer<Unit>
        {
            public int Compare(Unit left, Unit right)
            {
                int comparison = right.Projection.y.CompareTo(left.Projection.y);
                return comparison != 0 ? comparison : left.Id.CompareTo(right.Id);
            }
        }

        private struct LeftToRightOrder : IComparer<Unit>
        {
            public int Compare(Unit left, Unit right)
            {
                int comparison = left.Projection.x.CompareTo(right.Projection.x);
                return comparison != 0 ? comparison : left.Id.CompareTo(right.Id);
            }
        }
    }
}
