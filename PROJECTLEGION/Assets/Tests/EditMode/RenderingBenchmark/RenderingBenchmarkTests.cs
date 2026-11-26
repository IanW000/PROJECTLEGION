using System;
using System.Collections.Generic;
using System.Globalization;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using System.Reflection;

namespace ProjectLegion.RenderingBenchmark.Tests
{
    public sealed class RenderingBenchmarkTests
    {
        [Test]
        public void ConfigurationAndSuite()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RenderBenchmarkConfig(RenderMode.G1, 101, 0, 1, 1));
            Assert.Throws<ArgumentException>(() => new RenderBenchmarkConfig(RenderMode.G2, 100, 0, 0, 1));
            var plan = RenderBenchmarkRunner.Plan("suite", 100, 3, false);
            Assert.That(plan.Count, Is.EqualTo(44));
            Assert.That(plan[0].Units, Is.Zero);
            Assert.That(plan[1].Units, Is.Zero);
            Assert.That(plan[2].Warmup, Is.EqualTo(300));
            Assert.That(plan[2].Samples, Is.EqualTo(1000));
            var smoke = RenderBenchmarkRunner.Plan("suite", 100, 3, true);
            Assert.That(smoke.Count, Is.EqualTo(6));
        }

        [Test]
        public void LayoutFitsAllCellsInTheFixedFrustumWithoutOverlap()
        {
            var seen = new HashSet<Vector3>();
            float radius = UnitLayout.MeshSize * 0.5f;
            Assert.That(UnitLayout.MeshSize, Is.LessThan(1));
            Assert.That(UnitLayout.MeshSize / (2 * UnitLayout.CameraSize) * 1080, Is.GreaterThan(3));
            for (int id = 0; id < UnitLayout.Capacity; id++)
            {
                Vector3 position = UnitLayout.Position(id);
                if (position != UnitLayout.Position(id) || !seen.Add(position) ||
                    Mathf.Abs(position.x) + radius > UnitLayout.CameraSize * UnitLayout.Aspect ||
                    Mathf.Abs(position.y) + radius > UnitLayout.CameraSize)
                    Assert.Fail($"Invalid coverage, determinism or unique cell at unit {id}.");
            }
        }

        [Test]
        public void SamplesRequireAdjacentFrameAndFlushTheLastFrame()
        {
            using var sampler = new RenderFrameSampler(0, 2, false);
            sampler.BeginSample(10, 100);
            sampler.Advance(12, 300);
            Assert.That(sampler.Frames[0, 0], Is.NaN);
            sampler.BeginSample(12, 300);
            sampler.Advance(13, 500);
            sampler.Advance(14, 900);
            Assert.That(sampler.Frames[1, 0], Is.EqualTo(200 * (1000.0 / RenderClock.Frequency)));
            Assert.That(sampler.Frames[1, 1], Is.NaN);
        }

        [Test]
        public void DelayedTimingsAreDeduplicatedAndDrainKeepsOnlyTheSamplingWindow()
        {
            var window = new TimingWindow(3);
            window.SetWindow(100, 140);
            window.Accept(new FrameTiming { frameStartTimestamp = 99 });
            window.Accept(new FrameTiming { frameStartTimestamp = 112 });
            window.Accept(new FrameTiming { frameStartTimestamp = 112 });
            Assert.That(window.Missing, Is.EqualTo(2));
            window.Accept(new FrameTiming { frameStartTimestamp = 142 });
            window.Accept(new FrameTiming { frameStartTimestamp = 139 });
            Assert.That(window.Count, Is.EqualTo(2));
            Assert.That(window.Missing, Is.EqualTo(1));
            Assert.That(RenderBenchmarkExport.TimingValue(default, 2, 0), Is.NaN);
            window.Accept(new FrameTiming { frameStartTimestamp = 131 });
            window.Accept(new FrameTiming { frameStartTimestamp = 135 });
            Assert.That(window.Overflow, Is.EqualTo(1));
        }

        [Test]
        public void StatisticsAndCsvFollowInvariantM4Convention()
        {
            var stats = Statistics.Calculate(new[] { 4.0, double.NaN, 1, 3, 2 });
            Assert.That(stats.Count, Is.EqualTo(4));
            Assert.That(stats.Mean, Is.EqualTo(2.5));
            Assert.That(stats.Median, Is.EqualTo(2.5));
            Assert.That(stats.P95, Is.EqualTo(3.85).Within(1e-12));
            Assert.That(stats.P99, Is.EqualTo(3.97).Within(1e-12));
            CultureInfo previous = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-CA");
                Assert.That(RenderBenchmarkExport.Number(1.25), Is.EqualTo("1.25"));
                Assert.That(RenderBenchmarkExport.Number(double.NaN), Is.EqualTo("N/A"));
                Assert.That(RenderBenchmarkExport.Escape("a,\"b"), Is.EqualTo("\"a,\"\"b\""));
            }
            finally
            {
                CultureInfo.CurrentCulture = previous;
            }
        }

        [Test]
        public void LitMaterialHasSrpBatcherCompatibleSubshader()
        {
            var material = AssetDatabase.LoadAssetAtPath<Material>("Assets/Benchmarks/Rendering/Settings/BenchmarkUnits.mat");
            Assert.That(material, Is.Not.Null);
            Assert.That(material.enableInstancing, Is.False);
            const BindingFlags nativeEditorMethod = BindingFlags.Static | BindingFlags.NonPublic;
            var active = typeof(ShaderUtil).GetMethod("GetShaderActiveSubshaderIndex", nativeEditorMethod);
            var check = typeof(ShaderUtil).GetMethod("GetSRPBatcherCompatibilityCode", nativeEditorMethod);
            var reason = typeof(ShaderUtil).GetMethod("GetSRPBatcherCompatibilityIssueReason", nativeEditorMethod);
            Assert.That(active, Is.Not.Null);
            Assert.That(check, Is.Not.Null);
            Assert.That(material.SetPass(0), Is.True);
            int subshader = (int)active.Invoke(null, new object[] { material.shader });
            int result = (int)check.Invoke(null, new object[] { material.shader, subshader });
            Assert.That(result, Is.Zero,
                result == 0 ? "" : (string)reason.Invoke(null, new object[] { material.shader, subshader, result }));
        }
    }
}
