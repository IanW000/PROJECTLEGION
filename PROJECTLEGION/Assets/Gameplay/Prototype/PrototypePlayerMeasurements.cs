using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Transforms;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    internal static class PrototypePlayerMeasurements
    {
        private const int WarmFrames = 60;
        private const int Samples = 160;
        private static readonly int[] Counts = { 1000, 5000, 10000 };
        private static readonly string[] Loads = { "Idle", "All Selected", "Formation Movement", "Congestion" };

        internal static string OutputPath()
        {
            string[] args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, "-prototype-profile");
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
        }

        internal static IEnumerator Run(PrototypeHud hud, string output)
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            Application.runInBackground = true;
            Screen.SetResolution(1920, 1080, FullScreenMode.Windowed);
            yield return null;
            var world = World.DefaultGameObjectInjectionWorld;
            var spawner = world.GetExistingSystemManaged<UnitSpawnSystem>();
            var commands = world.GetExistingSystemManaged<UnitMoveCommandSystem>();
            int timeout = 600;
            while (!spawner.CanReset && timeout-- > 0)
                yield return null;
            if (!spawner.CanReset)
            {
                UnityEngine.Debug.LogError("Prototype measurements: the unit SubScene did not load.");
                Application.Quit(1);
                yield break;
            }

            var manager = world.EntityManager;
            using var units = new EntityQueryBuilder(Allocator.Temp)
                .WithAll<PrototypeUnit, UnitSelected, LocalTransform>()
                .WithOptions(EntityQueryOptions.IgnoreComponentEnabledState).Build(manager);
            using var active = manager.CreateEntityQuery(typeof(PrototypeUnit), typeof(UnitMoveTarget));
            using var main = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Main Thread");
            using var render = ProfilerRecorder.StartNew(ProfilerCategory.Internal, "Render Thread");
            using var gc = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            using var memory = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Total Used Memory");
            using var reserved = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "Total Reserved Memory");
            var timings = new FrameTiming[1];
            var wall = new double[Samples];
            var mainTimes = new double[Samples];
            var renderTimes = new double[Samples];
            var gpu = new double[Samples];
            var gcBytes = new double[Samples];
            var memoryBytes = new double[Samples];
            var reservedBytes = new double[Samples];
            string directory = Path.GetDirectoryName(Path.GetFullPath(output));
            Directory.CreateDirectory(directory);
            using var writer = new StreamWriter(output);
            writer.WriteLine($"# Unity={Application.unityVersion}; development={UnityEngine.Debug.isDebugBuild}; " +
                $"graphics={SystemInfo.graphicsDeviceType}; gpu={SystemInfo.graphicsDeviceName}; cpu={SystemInfo.processorType}; " +
                $"ram_mb={SystemInfo.systemMemorySize}; quality={QualitySettings.names[QualitySettings.GetQualityLevel()]}; " +
                $"resolution={Screen.width}x{Screen.height}; vsync=0; target=-1; warm_frames={WarmFrames}; samples={Samples}; " +
                $"frame_timing_available={FrameTimingManager.IsFeatureEnabled()}");
            writer.WriteLine("units,load,hud,mean_ms,p95_ms,max_ms,fps,main_mean_ms,render_mean_ms,gpu_mean_ms,gc_mean_bytes,gc_max_bytes,used_peak_bytes,reserved_peak_bytes,active_at_start,active_at_end");
            foreach (int count in Counts)
            for (int load = 0; load < Loads.Length; load++)
            for (int hudMode = 0; hudMode < 2; hudMode++)
            {
                hud.SetShown(hudMode == 1);
                uint generation = spawner.Generation;
                spawner.RequestReset(count);
                while (spawner.Generation == generation)
                    yield return null;
                if (units.CalculateEntityCount() != count)
                    throw new InvalidOperationException("Reset did not produce the requested unit count.");

                for (int i = 0; i < WarmFrames; i++)
                    yield return null;
                if (load != 0)
                    manager.SetComponentEnabled<UnitSelected>(units, true);
                if (load == 2)
                {
                    commands.IssueMove(new float2(30f, 19f));
                    yield return null;
                }
                if (load == 3)
                {
                    SetCohort(manager, units, false);
                    commands.IssueMove(new float2(12f, 19f), 0.75f);
                    yield return null;
                    SetCohort(manager, units, true);
                    commands.IssueMove(new float2(-12f, 19f), 0.75f);
                    yield return null;
                }

                int activeAtStart = active.CalculateEntityCount();
                ulong lastTiming = 0;
                for (int i = 0; i < Samples; i++)
                {
                    if (FrameTimingManager.IsFeatureEnabled())
                        FrameTimingManager.CaptureFrameTimings();
                    long start = Stopwatch.GetTimestamp();
                    yield return null;
                    wall[i] = (Stopwatch.GetTimestamp() - start) * (1000d / Stopwatch.Frequency);
                    mainTimes[i] = Value(main, 1e-6);
                    renderTimes[i] = Value(render, 1e-6);
                    gcBytes[i] = Value(gc, 1);
                    memoryBytes[i] = Value(memory, 1);
                    reservedBytes[i] = Value(reserved, 1);
                    gpu[i] = double.NaN;
                    if (FrameTimingManager.IsFeatureEnabled() && FrameTimingManager.GetLatestTimings(1, timings) == 1 &&
                        timings[0].frameStartTimestamp != lastTiming)
                    {
                        lastTiming = timings[0].frameStartTimestamp;
                        if (timings[0].gpuFrameTime > 0)
                            gpu[i] = timings[0].gpuFrameTime;
                        if (double.IsNaN(mainTimes[i]) && timings[0].cpuMainThreadFrameTime > 0)
                            mainTimes[i] = timings[0].cpuMainThreadFrameTime;
                        if (double.IsNaN(renderTimes[i]) && timings[0].cpuRenderThreadFrameTime > 0)
                            renderTimes[i] = timings[0].cpuRenderThreadFrameTime;
                    }
                }

                double average = Mean(wall);
                Array.Sort(wall);
                Write(writer, count.ToString(), Loads[load], hudMode.ToString(), Number(average),
                    Number(wall[(int)Math.Ceiling(Samples * 0.95) - 1]), Number(wall[Samples - 1]), Number(1000 / average),
                    Number(Mean(mainTimes)), Number(Mean(renderTimes)), Number(Mean(gpu)), Number(Mean(gcBytes)),
                    Number(Max(gcBytes)), Number(Max(memoryBytes)), Number(Max(reservedBytes)),
                    activeAtStart.ToString(), active.CalculateEntityCount().ToString());
                writer.Flush();
            }

            writer.WriteLine("# Repeated reset after warm capacity: count,wall_ms,used_bytes,reserved_bytes");
            hud.SetShown(false);
            for (int cycle = 0; cycle < 6; cycle++)
            {
                int count = cycle % 2 == 0 ? 100 : 10000;
                uint generation = spawner.Generation;
                long start = Stopwatch.GetTimestamp();
                spawner.RequestReset(count);
                while (spawner.Generation == generation)
                    yield return null;
                double reset = (Stopwatch.GetTimestamp() - start) * (1000d / Stopwatch.Frequency);
                if (units.CalculateEntityCount() != count || active.CalculateEntityCount() != 0)
                    throw new InvalidOperationException("Repeated reset retained stale units or targets.");
                for (int i = 0; i < 30; i++)
                    yield return null;
                Write(writer, count.ToString(), Number(reset), Number(Value(memory, 1)), Number(Value(reserved, 1)));
                writer.Flush();
            }
            Application.Quit(0);
        }

        private static void SetCohort(EntityManager manager, EntityQuery units, bool right)
        {
            using var entities = units.ToEntityArray(Allocator.Temp);
            foreach (Entity entity in entities)
            {
                float x = manager.GetComponentData<LocalTransform>(entity).Position.x;
                manager.SetComponentEnabled<UnitSelected>(entity, right ? x >= 0 : x < 0);
            }
        }

        private static double Value(ProfilerRecorder recorder, double scale) =>
            recorder.Valid && recorder.Count > 0 && recorder.LastValue >= 0 ? recorder.LastValue * scale : double.NaN;
        private static double Mean(double[] values)
        {
            double total = 0;
            int count = 0;
            foreach (double value in values)
                if (!double.IsNaN(value))
                {
                    total += value;
                    count++;
                }
            return count > 0 ? total / count : double.NaN;
        }
        private static double Max(double[] values)
        {
            double max = double.NaN;
            foreach (double value in values)
                if (!double.IsNaN(value) && (double.IsNaN(max) || value > max))
                    max = value;
            return max;
        }
        private static string Number(double value) =>
            double.IsNaN(value) ? "N/A" : value.ToString("F3", CultureInfo.InvariantCulture);
        private static void Write(StreamWriter writer, params string[] fields) =>
            writer.WriteLine(string.Join(",", fields));
    }
}
