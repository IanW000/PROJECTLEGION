using System;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace ProjectLegion.RenderingBenchmark
{
    internal sealed class RenderBenchmarkExport
    {
        internal string DirectoryPath { get; }
        private static readonly string[] TimingMetrics = { "main_thread_ms", "render_thread_ms", "gpu_ms", "present_wait_ms" };

        internal RenderBenchmarkExport(string root)
        {
            DirectoryPath = Path.Combine(root, "render-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(DirectoryPath);
            using var environment = Open("environment.csv");
            environment.WriteLine("key,value");
            WriteMetadata(environment, "unity", Application.unityVersion);
            WriteMetadata(environment, "development", Debug.isDebugBuild.ToString());
            WriteMetadata(environment, "graphics_api", SystemInfo.graphicsDeviceType.ToString());
            WriteMetadata(environment, "gpu", SystemInfo.graphicsDeviceName);
            WriteMetadata(environment, "graphics_memory_mb", Number(SystemInfo.graphicsMemorySize));
            WriteMetadata(environment, "processor", SystemInfo.processorType);
            WriteMetadata(environment, "resolution", "1920x1080");
            WriteMetadata(environment, "render_surface",
                Application.platform == RuntimePlatform.WindowsPlayer ? "player_backbuffer" : "offscreen_camera_target");
            WriteMetadata(environment, "player_window", FormattableString.Invariant($"{Screen.width}x{Screen.height}"));
            WriteMetadata(environment, "render_scale", "1");
            WriteMetadata(environment, "vsync", "0");
            WriteMetadata(environment, "target_fps", "-1");
            WriteMetadata(environment, "profiler_enabled", UnityEngine.Profiling.Profiler.enabled.ToString());

            using var frames = Open("frames.csv");
            frames.WriteLine("run,mode,units,repetition,unity_frame,frame_wall_ms,draw_calls,batches,setpass,triangles,vertices,gfx_used_bytes,gfx_reserved_bytes,gc_bytes");
            using var timing = Open("timings.csv");
            timing.WriteLine("run,mode,units,repetition,qpc_frame_start,main_thread_ms,render_thread_ms,gpu_ms,present_wait_ms");
            using var summary = Open("summary.csv");
            summary.WriteLine("run,mode,units,repetition,metric,valid,mean,median,p95,p99");
            using var runs = Open("runs.csv");
            runs.WriteLine("run,mode,units,repetition,status,warmup_frames,sample_frames,frame_timing_enabled,cpu_frequency_hz,gpu_frequency_hz,timing_records,timing_missing,timing_ambiguous,gpu_valid,srp_batcher_probe,raw_timing_reads,raw_first_ts,raw_last_ts,window_lower,window_upper,detail");
        }

        internal void Record(int index, RenderBenchmarkConfig config, RenderFrameSampler sampler, string status, string detail)
        {
            string id = FormattableString.Invariant($"{index},{config.Mode},{config.Units},{config.Repetition}");
            int timingCount = sampler?.Timings.Count ?? 0;
            int gpuValid = 0;
            if (status == "Completed")
            {
                double[,] values = sampler.Frames;
                using var frames = Open("frames.csv");
                for (int row = 0; row < sampler.Count; row++)
                {
                    frames.Write(FormattableString.Invariant($"{id},{sampler.UnityFrames[row]}"));
                    for (int col = 0; col < values.GetLength(1); col++)
                        frames.Write("," + Number(values[row, col]));
                    frames.WriteLine();
                }

                using var timing = Open("timings.csv");
                if (sampler.Timings.Overflow == 0)
                {
                    for (int row = 0; row < timingCount; row++)
                    {
                        FrameTiming item = sampler.Timings.Records[row];
                        timing.Write(FormattableString.Invariant($"{id},{item.frameStartTimestamp}"));
                        for (int col = 0; col < TimingMetrics.Length; col++)
                            timing.Write("," + Number(TimingValue(item, col, sampler.GpuTimerFrequency)));
                        timing.WriteLine();
                        if (!double.IsNaN(TimingValue(item, 2, sampler.GpuTimerFrequency)))
                            gpuValid++;
                    }
                }

                using var summary = Open("summary.csv");
                var scratch = new double[sampler.Count];
                for (int col = 0; col < values.GetLength(1); col++)
                {
                    for (int row = 0; row < scratch.Length; row++)
                        scratch[row] = values[row, col];
                    WriteStats(summary, id, col == 0 ? "frame_wall_ms" : RenderFrameSampler.CounterNames[col - 1], scratch);
                }
                for (int col = 0; col < TimingMetrics.Length; col++)
                {
                    for (int row = 0; row < scratch.Length; row++)
                        scratch[row] = row < timingCount && sampler.Timings.Overflow == 0
                            ? TimingValue(sampler.Timings.Records[row], col, sampler.GpuTimerFrequency) : double.NaN;
                    WriteStats(summary, id, TimingMetrics[col], scratch);
                }
            }

            using var runs = Open("runs.csv");
            TimingWindow window = sampler?.Timings;
            runs.WriteLine(FormattableString.Invariant(
                $"{id},{status},{config.Warmup},{sampler?.Count ?? 0},{sampler?.TimingEnabled ?? false},{sampler?.CpuTimerFrequency ?? 0},{sampler?.GpuTimerFrequency ?? 0},{timingCount},{Math.Max(0, config.Samples - timingCount)},{window?.Overflow ?? 0},{gpuValid},{Escape(sampler?.BatchPathProbe ?? "N/A")},{window?.Returned ?? 0},{window?.FirstReturned ?? 0},{window?.LastReturned ?? 0},{window?.Lower ?? 0},{window?.Upper ?? 0},{Escape(detail)}"));
        }

        internal static double TimingValue(FrameTiming timing, int metric, ulong gpuFrequency)
        {
            double value = metric switch
            {
                0 => timing.cpuMainThreadFrameTime,
                1 => timing.cpuRenderThreadFrameTime,
                2 => gpuFrequency == 0 ? double.NaN : timing.gpuFrameTime,
                _ => timing.cpuMainThreadPresentWaitTime
            };
            return !double.IsInfinity(value) && (value > 0 || (metric == 3 && value == 0 && timing.cpuFrameTime > 0))
                ? value : double.NaN;
        }

        private static void WriteStats(StreamWriter writer, string id, string metric, double[] values)
        {
            var result = Statistics.Calculate(values);
            writer.WriteLine(FormattableString.Invariant($"{id},{Escape(metric)},{result.Count},{Number(result.Mean)},{Number(result.Median)},{Number(result.P95)},{Number(result.P99)}"));
        }

        private StreamWriter Open(string file) => new StreamWriter(Path.Combine(DirectoryPath, file), true);
        private static void WriteMetadata(StreamWriter writer, string key, string value) => writer.WriteLine($"{key},{Escape(value)}");
        internal static string Number(double value) => double.IsNaN(value) || double.IsInfinity(value) ? "N/A" : value.ToString("G17", CultureInfo.InvariantCulture);
        internal static string Escape(string value) => "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
    }

    internal readonly struct Statistics
    {
        internal readonly int Count;
        internal readonly double Mean, Median, P95, P99;

        private Statistics(double[] values, int count, double sum)
        {
            Count = count;
            Mean = count == 0 ? double.NaN : sum / count;
            Median = Percentile(values, count, .5);
            P95 = Percentile(values, count, .95);
            P99 = Percentile(values, count, .99);
        }

        internal static Statistics Calculate(double[] values)
        {
            int count = 0;
            double sum = 0;
            foreach (double value in values)
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                    continue;
                values[count++] = value;
                sum += value;
            }
            Array.Sort(values, 0, count);
            return new Statistics(values, count, sum);
        }

        private static double Percentile(double[] values, int count, double fraction)
        {
            if (count == 0)
                return double.NaN;
            double rank = (count - 1) * fraction;
            int lower = (int)rank;
            return values[lower] + (values[Math.Min(lower + 1, count - 1)] - values[lower]) * (rank - lower);
        }
    }
}
