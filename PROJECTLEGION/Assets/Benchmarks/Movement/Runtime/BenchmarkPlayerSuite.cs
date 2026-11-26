using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using Unity.Burst;
using Unity.Jobs.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Profiling;

namespace ProjectLegion.MovementBenchmark
{
    [DefaultExecutionOrder(200)]
    internal sealed class BenchmarkPlayerSuite : MonoBehaviour
    {
        private BenchmarkRunner _runner;
        private List<BenchmarkWorkload> _plan;
        private BenchmarkExport _export;
        private BenchmarkMeasurements _current;
        private FileSystemWatcher _cancelWatcher;
        private int _index;
        private int _cancelRequested;
        private bool _smoke;
        private bool _finishing;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Launch()
        {
#if UNITY_STANDALONE_WIN && !UNITY_EDITOR
            string[] args = Environment.GetCommandLineArgs();
            int flag = Array.IndexOf(args, "-legion-benchmark");
            if (flag < 0)
                return;

            var root = new GameObject("Automated Movement Benchmark");
            var suite = root.AddComponent<BenchmarkPlayerSuite>();
            try
            {
                suite.Configure(args, flag);
            }
            catch (Exception exception)
            {
                suite.Finish(1, exception.Message);
            }
#endif
        }

        private static string Option(string[] args, string option, string fallback = null)
        {
            int index = Array.IndexOf(args, option);
            if (index < 0)
                return fallback;
            if (index + 1 >= args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
                throw new ArgumentException($"Value required for {option}.");
            return args[index + 1];
        }

        private static int IntegerOption(string[] args, string key, int fallback)
        {
            string value = Option(args, key);
            return value == null ? fallback : int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        private void Configure(string[] args, int flag)
        {
            const string command = "-legion-benchmark";
            string kind = Option(args, command);
            if (kind != "suite" && kind != "single")
                throw new ArgumentException($"{args[flag]} requires 'single' or 'suite'.");

            string letter = Option(args, "-legion-mode", "A");
            if (letter.Length != 1 || letter[0] < 'A' || letter[0] > 'E')
                throw new ArgumentException("Mode must be A, B, C, D or E.");

            var mode = (BenchmarkMode)(letter[0] - 'A');
            int units = IntegerOption(args, "-legion-units", 1000);
            int repetitions = IntegerOption(args, "-legion-repetitions", kind == "suite" ? BenchmarkWorkload.Repetitions : 1);
            int seed = IntegerOption(args, "-legion-order-seed", 71237);
            _smoke = Array.IndexOf(args, "-legion-smoke") >= 0;
            if (_smoke && kind == "suite")
                throw new ArgumentException("Smoke mode requires a single benchmark combination.");
            _plan = BenchmarkWorkload.Plan(kind == "suite", mode, units, repetitions, seed);

            string git = Option(args, "-legion-git", "N/A");
            if (git != "N/A" && (git.Length != 40 || !System.Text.RegularExpressions.Regex.IsMatch(git, @"\A[0-9a-fA-F]{40}\z")))
                throw new ArgumentException("Pass the full Git SHA or omit -legion-git.");

            string output = Option(args, "-legion-output", Application.persistentDataPath);
            if (kind == "suite")
                for (int index = 1; index <= (int)BenchmarkMode.EcsParallelBurst; index++)
                    VerifyBackend((BenchmarkMode)index);
            else
                VerifyBackend(mode);

            _export = new BenchmarkExport(output, Metadata(kind, seed, repetitions, git, _plan[0].Config(_smoke)));
            _runner = gameObject.AddComponent<BenchmarkRunner>();
            _cancelWatcher = new FileSystemWatcher(_export.DirectoryPath, "cancel.request");
            _cancelWatcher.Created += RequestCancellation;
            _cancelWatcher.Renamed += RequestCancellation;
            _cancelWatcher.EnableRaisingEvents = true;
            Debug.Log($"Movement benchmark output: {_export.DirectoryPath}");
        }

        private void RequestCancellation(object sender, FileSystemEventArgs args)
        {
            Interlocked.Exchange(ref _cancelRequested, 1);
        }

        private static void VerifyBackend(BenchmarkMode mode)
        {
            if (mode == BenchmarkMode.MonoBehaviour)
                return;
            using var world = new EcsMovementWorld(new BenchmarkConfig(mode), new UnitState[1], recordExecution: true);
            world.StartStep();
            bool burst = mode == BenchmarkMode.EcsSequentialBurst || mode == BenchmarkMode.EcsParallelBurst;
            world.VerifyExecution(burst ? MovementChunkData.BurstExecution : MovementChunkData.ManagedExecution);
        }

        private IEnumerable<KeyValuePair<string, string>> Metadata(string kind, int seed, int repetitions,
            string git, BenchmarkConfig config)
        {
            var values = new Dictionary<string, string>
            {
                ["utc_start"] = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ["unity_version"] = Application.unityVersion,
                ["platform"] = Application.platform.ToString(),
                ["development_build"] = Debug.isDebugBuild.ToString(),
                ["application_version"] = Application.version,
#if ENABLE_IL2CPP
                ["scripting_backend"] = "IL2CPP",
#elif ENABLE_MONO
                ["scripting_backend"] = "Mono",
#else
                ["scripting_backend"] = "N/A",
#endif
                ["burst_enabled"] = BurstCompiler.IsEnabled.ToString(),
                ["burst_execution_probe"] = "passed for selected ECS modes",
                ["git_commit_user_supplied"] = git,
                ["os"] = SystemInfo.operatingSystem,
                ["cpu"] = SystemInfo.processorType,
                ["logical_processors"] = SystemInfo.processorCount.ToString(CultureInfo.InvariantCulture),
                ["ram_mb"] = SystemInfo.systemMemorySize.ToString(CultureInfo.InvariantCulture),
                ["graphics_device"] = SystemInfo.graphicsDeviceType.ToString(),
                ["gpu"] = SystemInfo.graphicsDeviceName,
                ["resolution"] = FormattableString.Invariant($"{Screen.width}x{Screen.height}"),
                ["quality"] = QualitySettings.names[QualitySettings.GetQualityLevel()],
                ["job_workers"] = JobsUtility.JobWorkerCount.ToString(CultureInfo.InvariantCulture),
                ["profiler_enabled"] = Profiler.enabled.ToString(),
                ["vsync_during_run"] = "0",
                ["target_frame_rate_during_run"] = "-1",
                ["stopwatch_frequency_hz"] = StopwatchFrequency(),
                ["warmup_steps"] = config.WarmupSteps.ToString(CultureInfo.InvariantCulture),
                ["sample_steps"] = config.SampleSteps.ToString(CultureInfo.InvariantCulture),
                ["step_seconds"] = BenchmarkExport.Number(config.StepSeconds),
                ["state_seed"] = config.Seed.ToString(CultureInfo.InvariantCulture),
                ["bounds_xz"] = FormattableString.Invariant($"{config.MinXZ.x},{config.MinXZ.y},{config.MaxXZ.x},{config.MaxXZ.y}"),
                ["min_speed"] = BenchmarkExport.Number(config.MinSpeed),
                ["max_speed"] = BenchmarkExport.Number(config.MaxSpeed),
                ["order_seed"] = seed.ToString(CultureInfo.InvariantCulture),
                ["repetitions"] = repetitions.ToString(CultureInfo.InvariantCulture),
                ["planned_runs_including_baselines"] = _plan.Count.ToString(CultureInfo.InvariantCulture),
                ["request"] = kind,
                ["smoke"] = _smoke.ToString()
            };
            return values;
        }

        private static string StopwatchFrequency() =>
            System.Diagnostics.Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture);

        private void Update()
        {
            if (_finishing || _runner == null)
                return;

            bool cancelled = Volatile.Read(ref _cancelRequested) != 0;
            try
            {
                if (_current != null)
                {
                    if (_runner.IsRunning)
                    {
                        if (cancelled)
                            _runner.Cancel();
                        return;
                    }

                    BenchmarkLifecycle status = _runner.Lifecycle;
                    _export.Record(_index + 1, _plan[_index], _current, status, _runner.Status);
                    _current = null;
                    _index++;
                    Debug.Log($"Movement benchmark {_index}/{_plan.Count}: {status}");
                    if (status != BenchmarkLifecycle.Completed)
                    {
                        Finish(status == BenchmarkLifecycle.Cancelled ? 2 : 1, _runner.Status);
                        return;
                    }
                    // Allow deferred destruction before starting the next run.
                    return;
                }

                if (cancelled || _index == _plan.Count)
                {
                    Finish(cancelled ? 2 : 0, cancelled ? "Cancelled between runs." : "Completed.");
                    return;
                }

                BenchmarkWorkload workload = _plan[_index];
                var measurement = new BenchmarkMeasurements(_smoke ? 6 : BenchmarkWorkload.Samples);
                if (!_runner.TryStartMeasured(workload, _smoke, measurement))
                    throw new InvalidOperationException(_runner.Status);
                _current = measurement;
            }
            catch (Exception exception)
            {
                Finish(1, exception.Message);
            }
        }

        private void Finish(int code, string status)
        {
            _finishing = true;
            _cancelWatcher?.Dispose();
            _cancelWatcher = null;
            _runner?.Cancel();
            if (code == 1)
                Debug.LogError($"Movement benchmark failed: {status}");
            else
                Debug.Log($"Movement benchmark: {status}");
            Application.Quit(code);
        }

        private void OnDestroy()
        {
            _cancelWatcher?.Dispose();
        }
    }
}
