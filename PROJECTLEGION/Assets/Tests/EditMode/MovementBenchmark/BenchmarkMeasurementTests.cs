using System;
using System.Globalization;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace ProjectLegion.MovementBenchmark.Tests
{
    public class BenchmarkMeasurementTests
    {
        [Test]
        public void CalculatesReferenceStatisticsAndSkipsMissingValues()
        {
            double[] values = { 4, double.NaN, 2, 1, 3 };
            BenchmarkStatistics result = BenchmarkStatistics.Calculate(values);
            Assert.That(result.Count, Is.EqualTo(4));
            Assert.That(result.Min, Is.EqualTo(1));
            Assert.That(result.Max, Is.EqualTo(4));
            Assert.That(result.Mean, Is.EqualTo(2.5));
            Assert.That(result.Median, Is.EqualTo(2.5));
            Assert.That(result.StdDev, Is.EqualTo(Math.Sqrt(1.25)).Within(1e-12));
            Assert.That(result.P95, Is.EqualTo(3.85).Within(1e-12));
            Assert.That(result.P99, Is.EqualTo(3.97).Within(1e-12));
            Assert.That(BenchmarkStatistics.Calculate(new[] { double.NaN }).Mean, Is.NaN);
        }

        [Test]
        public void AlignsFrameIntervalsAndMarksMissingCounters()
        {
            using var data = new BenchmarkMeasurements(2);
            data.Open(15, 100, 104);
            data.Close(111, 1, 2, BenchmarkMode.EcsParallel);
            Assert.That(data.Samples[0].FrameMs, Is.NaN);
            data.ObserveFrame(16, 120);
            Assert.That(data.Samples[0].FrameMs, Is.EqualTo(BenchmarkMeasurements.Milliseconds(20)));
            Assert.That(data.Samples[0].StepMs, Is.EqualTo(BenchmarkMeasurements.Milliseconds(7)));
            Assert.That(data.Samples[0].SyncRunMs, Is.NaN);
            Assert.That(data.Samples[0].ScheduleMs, Is.EqualTo(1));
            Assert.That(data.Samples[0].CompleteMs, Is.EqualTo(2));
            Assert.That(data.Samples[0].MainThreadMs, Is.NaN);
            Assert.That(data.Samples[0].GcBytes, Is.NaN);
            Assert.That(data.Samples[0].UsedBytes, Is.NaN);
            Assert.That(data.Samples[0].ReservedBytes, Is.NaN);

            data.Open(16, 120, 123);
            data.Close(126, 0, 0, BenchmarkMode.MonoBehaviour);
            data.ObserveFrame(18, 150);
            Assert.That(data.Samples[1].FrameMs, Is.NaN);
        }

        [Test]
        public void SuiteContainsThreeSeededPermutationsAndFiveSeparateBaselines()
        {
            var plan = BenchmarkWorkload.Plan(true, BenchmarkMode.MonoBehaviour, 0, 3, 8);
            var repeat = BenchmarkWorkload.Plan(true, BenchmarkMode.MonoBehaviour, 0, 3, 8);
            var varied = BenchmarkWorkload.Plan(true, BenchmarkMode.MonoBehaviour, 0, 3, 9);
            Assert.That(plan.Count, Is.EqualTo(95));
            Assert.That(plan.Take(5).All(item => item.IsBaseline && item.Repetition == 0), Is.True);
            Assert.That(plan, Is.EqualTo(repeat));
            Assert.That(plan, Is.Not.EqualTo(varied));
            for (int repetition = 1; repetition <= 3; repetition++)
            {
                var combinations = plan.Where(item => item.Repetition == repetition)
                    .Select(item => (item.Mode, item.Units)).ToArray();
                Assert.That(combinations.Distinct().Count(), Is.EqualTo(30));
                Assert.That(combinations.Select(item => item.Units).Distinct(),
                    Is.EquivalentTo(new[] { 100, 500, 1000, 2500, 5000, 10000 }));
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => new BenchmarkConfig(unitCount: 0));
        }

        [Test]
        public void ExportUsesInvariantNumbersEscapesFieldsAndPreservesRunRecords()
        {
            string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-CA");
                var report = new BenchmarkExport(root, new System.Collections.Generic.Dictionary<string, string>
                {
                    ["cpu"] = "a,b"
                });
                var second = new BenchmarkExport(root, Array.Empty<System.Collections.Generic.KeyValuePair<string, string>>());
                Assert.That(second.DirectoryPath, Is.Not.EqualTo(report.DirectoryPath));
                using var data = new BenchmarkMeasurements(1);
                data.Open(2, 0, 0);
                data.Close(System.Diagnostics.Stopwatch.Frequency, 1.25, 0, BenchmarkMode.EcsSequential);
                data.ObserveFrame(3, System.Diagnostics.Stopwatch.Frequency);
                var workload = new BenchmarkWorkload(BenchmarkMode.EcsSequential, 100, 1);
                report.Record(1, workload, data, BenchmarkLifecycle.Completed, "complete");
                report.Record(2, workload, data, BenchmarkLifecycle.Failed, "bad,\"quote\"");

                string[] frames = File.ReadAllLines(Path.Combine(report.DirectoryPath, "frames.csv"));
                Assert.That(frames, Has.Length.EqualTo(2));
                Assert.That(frames[1], Does.Contain(",1.25,N/A,N/A,N/A,"));
                Assert.That(File.ReadAllText(Path.Combine(report.DirectoryPath, "environment.csv")), Does.Contain("cpu,\"a,b\""));
                Assert.That(File.ReadAllText(Path.Combine(report.DirectoryPath, "runs.csv")), Does.Contain("\"bad,\"\"quote\"\"\""));
                Assert.That(File.ReadAllText(Path.Combine(report.DirectoryPath, "summary.csv")),
                    Does.Contain(",step_wall,ms,1,1000,1000,1000,1000,0,1000,1000,100"));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
                if (Directory.Exists(root))
                    Directory.Delete(root, true);
            }
        }
    }
}
