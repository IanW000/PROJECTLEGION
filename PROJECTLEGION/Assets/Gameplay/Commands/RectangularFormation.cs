using Unity.Mathematics;

namespace ProjectLegion.Gameplay
{
    internal readonly struct RectangularFormation
    {
        internal const float DefaultSpacing = 0.9f;
        internal const float MinimumSpacing = 0.01f;
        private const float MinimumDirectionLengthSquared = 0.000001f;

        internal readonly float2 Forward;
        internal readonly float2 Right;
        internal readonly int Columns;

        private readonly int _count;
        private readonly float2 _center;
        private readonly float _spacing;
        private readonly float _meanRow;

        internal RectangularFormation(int count, float2 center, float2 direction, float spacing)
        {
            _count = count;
            _center = center;
            _spacing = math.isfinite(spacing) ? math.max(spacing, MinimumSpacing) : DefaultSpacing;
            Forward = math.lengthsq(direction) > MinimumDirectionLengthSquared
                ? math.normalize(direction) : new float2(0f, 1f);
            Right = new float2(Forward.y, -Forward.x);
            Columns = (int)math.ceil(math.sqrt(count));

            // Weight each row by its occupied slots, including the short final row.
            int fullRows = count / Columns;
            int remaining = count % Columns;
            _meanRow = (float)(((double)Columns * fullRows * (fullRows - 1) * 0.5
                + (double)remaining * fullRows) / count);
        }

        internal int RowLength(int start) => math.min(Columns, _count - start);

        internal float2 GetSlot(int index)
        {
            int row = index / Columns;
            int start = row * Columns;
            float x = index - start - (RowLength(start) - 1) * 0.5f;
            return _center + (Right * x + Forward * (_meanRow - row)) * _spacing;
        }
    }
}
