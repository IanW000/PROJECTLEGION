using System;
using UnityEngine;

namespace ProjectLegion.RenderingBenchmark
{
    public enum RenderMode { G1, G2 }

    internal readonly struct RenderBenchmarkConfig
    {
        internal const int Width = 1920;
        internal const int Height = 1080;
        internal const int DefaultWarmup = 300;
        internal const int DefaultSamples = 1000;
        internal const int DefaultRepetitions = 3;
        internal static readonly int[] UnitCounts = { 100, 1000, 5000, 10000, 25000, 50000, 100000 };

        internal readonly RenderMode Mode;
        internal readonly int Units, Warmup, Samples, Repetition;

        internal RenderBenchmarkConfig(RenderMode mode, int units, int warmup, int samples, int repetition)
        {
            if (mode != RenderMode.G1 && mode != RenderMode.G2)
                throw new ArgumentOutOfRangeException(nameof(mode));
            if (units != 0 && Array.IndexOf(UnitCounts, units) < 0)
                throw new ArgumentOutOfRangeException(nameof(units));
            if (warmup < 0 || samples <= 0 || samples > 10000 || warmup > 10000 || repetition < 0)
                throw new ArgumentException("Invalid frame or repetition count.");

            Mode = mode;
            Units = units;
            Warmup = warmup;
            Samples = samples;
            Repetition = repetition;
        }
    }

    internal static class UnitLayout
    {
        private const int Columns = 400;
        private const int Rows = 250;
        private const int CellStride = 8191; // Coprime with the cells, so every stable ID owns a unique cell.
        internal const int Capacity = Columns * Rows;
        internal const float MeshSize = 0.85f;
        internal const float CameraSize = 128f;
        internal const float Aspect = (float)RenderBenchmarkConfig.Width / RenderBenchmarkConfig.Height;

        internal static Vector3 Position(int unitId)
        {
            if ((uint)unitId >= Capacity)
                throw new ArgumentOutOfRangeException(nameof(unitId));
            int cell = unitId * CellStride % Capacity;
            return new Vector3(cell % Columns - (Columns - 1) * 0.5f,
                cell / Columns - (Rows - 1) * 0.5f, 0);
        }
    }
}
