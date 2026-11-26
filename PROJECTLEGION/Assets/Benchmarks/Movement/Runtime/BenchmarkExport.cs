using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace ProjectLegion.MovementBenchmark
{
    internal readonly struct BenchmarkStatistics
    {
        internal readonly int Count;
        internal readonly double Min, Max, Mean, Median, StdDev, P95, P99;

        internal BenchmarkStatistics(double[] sorted, int count, double mean, double varianceSum)
        {
            Count = count;
            Min = count == 0 ? double.NaN : sorted[0];
            Max = count == 0 ? double.NaN : sorted[count - 1];
            Mean = count == 0 ? double.NaN : mean;
            Median = Percentile(sorted, count, 0.5);
            StdDev = count == 0 ? double.NaN : Math.Sqrt(Math.Max(0, varianceSum / count));
            P95 = Percentile(sorted, count, 0.95);
            P99 = Percentile(sorted, count, 0.99);
        }

        private static double Percentile(double[] sorted, int count, double probability)
        {
            if (count == 0)
                return double.NaN;
            double rank = (count - 1) * probability;
            int lower = (int)rank;
            return sorted[lower] + (sorted[Math.Min(lower + 1, count - 1)] - sorted[lower]) * (rank - lower);
        }

        internal static BenchmarkStatistics Calculate(double[] values)
        {
            int count = 0;
            double mean = 0;
            double varianceSum = 0;
            foreach (double value in values)
            {
                if (double.IsNaN(value) || double.IsInfinity(value))
                    continue;
                values[count++] = value;
                double delta = value - mean;
                mean += delta / count;
                varianceSum += delta * (value - mean);
            }
            Array.Sort(values, 0, count);
            return new BenchmarkStatistics(values, count, mean, varianceSum);
        }
    }

    internal sealed class BenchmarkExport
    {
        private static readonly (string Name, string Unit, Func<BenchmarkSample, double> Read)[] Metrics =
        {
            ("step_wall", "ms", sample => sample.StepMs),
            ("frame_wall", "ms", sample => sample.FrameMs),
            ("sync_dispatch_wall", "ms", sample => sample.SyncRunMs),
            ("schedule_dispatch_wall", "ms", sample => sample.ScheduleMs),
            ("complete_wall", "ms", sample => sample.CompleteMs),
            ("main_thread_profiler", "ms", sample => sample.MainThreadMs),
            ("gc_allocated", "bytes", sample => sample.GcBytes),
            ("total_used_memory", "bytes", sample => sample.UsedBytes),
            ("total_reserved_memory", "bytes", sample => sample.ReservedBytes),
            ("worker_execution", "ms", _ => double.NaN),
            ("thread_cpu", "ms", _ => double.NaN)
        };

        internal BenchmarkExport(string root, IEnumerable<KeyValuePair<string, string>> metadata)
        {
            DirectoryPath = Path.Combine(root, $"legion-{DateTime.UtcNow:yyyyMMddTHHmmssZ}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
            Write("environment.csv", writer =>
            {
                writer.WriteLine("key,value");
                foreach (var item in metadata)
                    writer.WriteLine($"{Escape(item.Key)},{Escape(item.Value)}");
            });
            Write("frames.csv", writer =>
            {
                writer.Write("run,mode,units,repetition,baseline,sample_step,unity_frame");
                foreach (var metric in Metrics)
                    writer.Write($",{metric.Name}_{metric.Unit}");
                writer.WriteLine();
            });
            Write("summary.csv", writer => writer.WriteLine(
                "run,mode,units,repetition,baseline,metric,unit,valid_samples,min,max,mean,median,population_stddev,p95,p99,unit_updates_per_second"));
            Write("runs.csv", writer => writer.WriteLine("run,mode,units,repetition,baseline,status,samples,detail"));
        }

        internal string DirectoryPath { get; }

        internal void Record(int run, BenchmarkWorkload workload, BenchmarkMeasurements measurement,
            BenchmarkLifecycle status, string detail)
        {
            string key = FormattableString.Invariant($"{run},{workload.Mode},{workload.Units},{workload.Repetition},{workload.IsBaseline}");
            if (status == BenchmarkLifecycle.Completed)
            {
                Append("frames.csv", writer =>
                {
                    for (int index = 0; index < measurement.Count; index++)
                    {
                        BenchmarkSample sample = measurement.Samples[index];
                        writer.Write(FormattableString.Invariant($"{key},{index + 1},{sample.Frame}"));
                        foreach (var metric in Metrics)
                            writer.Write($",{Number(metric.Read(sample))}");
                        writer.WriteLine();
                    }
                });

                Append("summary.csv", writer =>
                {
                    var values = new double[measurement.Count];
                    foreach (var metric in Metrics)
                    {
                        for (int index = 0; index < values.Length; index++)
                            values[index] = metric.Read(measurement.Samples[index]);

                        BenchmarkStatistics stats = BenchmarkStatistics.Calculate(values);
                        bool throughputMetric = metric.Name == "step_wall" || metric.Name == "frame_wall";
                        double throughput = throughputMetric && stats.Count == values.Length && stats.Mean > 0 &&
                            !workload.IsBaseline ? workload.Units * 1000.0 / stats.Mean : double.NaN;
                        writer.WriteLine(FormattableString.Invariant(
                            $"{key},{metric.Name},{metric.Unit},{stats.Count},{Number(stats.Min)},{Number(stats.Max)},{Number(stats.Mean)},{Number(stats.Median)},{Number(stats.StdDev)},{Number(stats.P95)},{Number(stats.P99)},{Number(throughput)}"));
                    }
                });
            }

            Append("runs.csv", writer => writer.WriteLine(FormattableString.Invariant(
                $"{key},{status},{measurement.Count},{Escape(detail)}")));
        }

        private void Write(string name, Action<TextWriter> write) => Open(name, false, write);
        private void Append(string name, Action<TextWriter> write) => Open(name, true, write);

        private void Open(string name, bool append, Action<TextWriter> write)
        {
            using var writer = new StreamWriter(Path.Combine(DirectoryPath, name), append, new UTF8Encoding(false));
            write(writer);
        }

        internal static string Number(double value) => double.IsNaN(value) || double.IsInfinity(value)
            ? "N/A"
            : value.ToString("G17", CultureInfo.InvariantCulture);

        internal static string Escape(string value)
        {
            value ??= "";
            return value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) < 0
                ? value
                : "\"" + value.Replace("\"", "\"\"") + "\"";
        }
    }
}
