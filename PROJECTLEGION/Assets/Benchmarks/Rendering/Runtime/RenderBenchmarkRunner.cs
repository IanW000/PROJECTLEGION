using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Debug = UnityEngine.Debug;
using Profiler = UnityEngine.Profiling.Profiler;

namespace ProjectLegion.RenderingBenchmark
{
    public sealed class RenderBenchmarkRunner : MonoBehaviour
    {
        private const int DrainFrames = 32;
        [SerializeField] private UniversalRenderPipelineAsset _g1Pipeline;
        [SerializeField] private UniversalRenderPipelineAsset _g2Pipeline;
        [SerializeField] private Material _material;
        [SerializeField] private RenderMode _inspectionMode;
        private MeshRendererBackend _backend;
        private RenderFrameSampler _sampler;
        private RenderPipelineAsset _previousPipeline;
        private bool _settingsOwned, _previousBatcher, _previousBackground, _cancel;
        private int _previousVSync, _previousTargetRate;

        private void Start()
        {
            string[] args = Environment.GetCommandLineArgs();
            IEnumerator suite;
            try
            {
                string requested = Option(args, "-render-mode", null);
                if (requested == null)
                    return;
                if (Application.platform != RuntimePlatform.WindowsPlayer || !Debug.isDebugBuild ||
                    SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D11 || Profiler.enabled)
                    throw new InvalidOperationException("Use a Windows x64 Development Player with D3D11 and the profiler disconnected.");
                if (Array.IndexOf(args, "-render-inspect") >= 0)
                {
                    Begin(new RenderBenchmarkConfig(ParseMode(requested), ReadInt(args, "-render-units", 100), 0, 1, 0));
                    return;
                }

                bool smoke = Array.IndexOf(args, "-render-smoke") >= 0;
                int repeats = ReadInt(args, "-render-repetitions", RenderBenchmarkConfig.DefaultRepetitions);
                if (repeats < 1 || repeats > 20)
                    throw new ArgumentOutOfRangeException(nameof(repeats));
                string output = Option(args, "-render-output", Application.persistentDataPath);
                suite = Run(Plan(requested, ReadInt(args, "-render-units", 100), repeats, smoke), output, true);
            }
            catch (Exception error)
            {
                End();
                Debug.LogError(error);
                Application.Quit(1);
                return;
            }
            StartCoroutine(suite);
        }

        [ContextMenu("Start Rendering Path Inspection")]
        private void Inspect()
        {
            if (Application.isPlaying && !_settingsOwned)
                Begin(new RenderBenchmarkConfig(_inspectionMode, 100, 0, 1, 0));
        }

        [ContextMenu("Cancel Rendering Benchmark")]
        public void Cancel()
        {
            _cancel = true;
        }

        private void Update()
        {
            if (_settingsOwned && Input.GetKeyDown(KeyCode.Escape))
            {
                if (_sampler == null)
                    End();
                else
                    Cancel();
            }
        }

        internal static List<RenderBenchmarkConfig> Plan(string mode, int units, int repeats, bool smoke)
        {
            if (repeats < 1 || repeats > 20)
                throw new ArgumentOutOfRangeException(nameof(repeats));
            if (smoke && units != 0 && units != 100 && units != 1000)
                throw new ArgumentException("Smoke supports zero, 100 or 1000 units.");

            int warmup = smoke ? 20 : RenderBenchmarkConfig.DefaultWarmup;
            int samples = smoke ? 30 : RenderBenchmarkConfig.DefaultSamples;
            var cases = new List<RenderBenchmarkConfig>();
            bool suite = mode == "suite";
            RenderMode single = suite ? RenderMode.G1 : ParseMode(mode);
            cases.Add(new RenderBenchmarkConfig(single, 0, warmup, samples, 0));
            if (suite)
                cases.Add(new RenderBenchmarkConfig(RenderMode.G2, 0, warmup, samples, 0));
            int[] counts = smoke ? new[] { 100, 1000 } : RenderBenchmarkConfig.UnitCounts;
            for (int repetition = 1; repetition <= (smoke ? 1 : repeats); repetition++)
            {
                if (suite)
                    foreach (int count in counts)
                    {
                        RenderMode first = repetition % 2 == 1 ? RenderMode.G1 : RenderMode.G2;
                        cases.Add(new RenderBenchmarkConfig(first, count, warmup, samples, repetition));
                        cases.Add(new RenderBenchmarkConfig(first == RenderMode.G1 ? RenderMode.G2 : RenderMode.G1,
                            count, warmup, samples, repetition));
                    }
                else if (units != 0)
                    cases.Add(new RenderBenchmarkConfig(single, units, warmup, samples, repetition));
            }
            return cases;
        }

        internal IEnumerator Run(List<RenderBenchmarkConfig> plan, string output, bool quitPlayer)
        {
            _cancel = false;
            RenderBenchmarkExport export;
            try
            {
                export = new RenderBenchmarkExport(output);
                Debug.Log($"Rendering benchmark output: {export.DirectoryPath}");
            }
            catch (Exception error)
            {
                Debug.LogError(error);
                if (quitPlayer)
                    Application.Quit(1);
                yield break;
            }

            for (int index = 0; index < plan.Count; index++)
            {
                if (_cancel)
                    break;
                RenderBenchmarkConfig config = plan[index];
                string failure = null;
                try
                {
                    Begin(config);
                    _sampler = new RenderFrameSampler(config.Warmup, config.Samples);
                }
                catch (Exception error)
                {
                    failure = error.Message;
                }

                long previous = RenderClock.Timestamp();
                if (failure == null)
                {
                    for (int i = 0; i < config.Warmup && !_cancel; i++)
                    {
                        yield return null;
                        previous = RenderClock.Timestamp();
                        _sampler.Advance(Time.frameCount, previous);
                    }

                    // Begin the sample on the frame following the last warm-up frame.
                    yield return null;
                    long lower = previous;
                    long upper = 0;
                    for (int i = 0; i < config.Samples && !_cancel; i++)
                    {
                        upper = RenderClock.Timestamp();
                        _sampler.Advance(Time.frameCount, upper);
                        _sampler.Timings.SetWindow(lower, upper);
                        _sampler.BeginSample(Time.frameCount, upper);
                        _sampler.PollTimings();
                        yield return null;
                    }
                    for (int i = 0; i < DrainFrames && !_cancel; i++)
                    {
                        _sampler.Advance(Time.frameCount, RenderClock.Timestamp());
                        _sampler.PollTimings();
                        yield return null;
                    }

                    if (!_cancel)
                        failure = Validate(config);
                    if (failure == null && !_cancel && config.Units != 0)
                    {
                        // Keep diagnostic markers outside the measurement and timing-drain windows.
                        using var path = new RenderPathProbe();
                        for (int i = 0; i < RenderPathProbe.Frames && !_cancel; i++)
                            yield return null;
                        _sampler.BatchPathProbe = path.Observation();
                    }
                }

                string status = failure != null ? "Failed" : _cancel ? "Cancelled" : "Completed";
                try
                {
                    export.Record(index + 1, config, _sampler, status, failure ?? status);
                }
                catch (Exception error)
                {
                    failure = error.Message;
                    status = "Failed";
                }
                finally
                {
                    End();
                }

                Debug.Log($"Rendering benchmark {index + 1}/{plan.Count}: {config.Mode} {config.Units} {status}");
                if (failure != null)
                {
                    Debug.LogError(failure);
                    if (quitPlayer)
                        Application.Quit(1);
                    yield break;
                }
                yield return null;
            }
            if (quitPlayer)
                Application.Quit(_cancel ? 2 : 0);
        }

        private string Validate(RenderBenchmarkConfig config)
        {
            bool expectedBatcher = config.Mode == RenderMode.G2;
            if (Application.platform == RuntimePlatform.WindowsPlayer && Profiler.enabled)
                return "The profiler was enabled during the run.";
            if (Application.platform == RuntimePlatform.WindowsPlayer && !HasFixedResolution())
                return "The Player backbuffer resolution changed during the run.";
            if (GraphicsSettings.useScriptableRenderPipelineBatching != expectedBatcher ||
                GraphicsSettings.currentRenderPipeline != (expectedBatcher ? _g2Pipeline : _g1Pipeline))
                return "The requested URP/SRP Batcher state did not become active.";
            if (_sampler.Count != config.Samples)
                return "Unexpected sample count.";
            for (int row = 0; row < _sampler.Count; row++)
                if (!(_sampler.Frames[row, 0] > 0))
                    return "Missing frame wall time or non-consecutive sample boundary.";
            return null;
        }

        internal void Begin(RenderBenchmarkConfig config)
        {
            if (_settingsOwned)
                throw new InvalidOperationException("A render run is active.");
            if (_g1Pipeline == null || _g2Pipeline == null || _g1Pipeline.useSRPBatcher || !_g2Pipeline.useSRPBatcher)
                throw new InvalidOperationException("Assign the two benchmark pipelines with opposite SRP Batcher states.");
            bool usePlayerBackbuffer = Application.platform == RuntimePlatform.WindowsPlayer;
            if (usePlayerBackbuffer && !HasFixedResolution())
                throw new InvalidOperationException("The Player requires a 1920x1080 backbuffer. Use the benchmark launch parameters.");

            _previousPipeline = QualitySettings.renderPipeline;
            _previousBatcher = GraphicsSettings.useScriptableRenderPipelineBatching;
            _previousBackground = Application.runInBackground;
            _previousVSync = QualitySettings.vSyncCount;
            _previousTargetRate = Application.targetFrameRate;
            _settingsOwned = true;
            try
            {
                QualitySettings.vSyncCount = 0;
                Application.targetFrameRate = -1;
                Application.runInBackground = true;
                QualitySettings.renderPipeline = config.Mode == RenderMode.G2 ? _g2Pipeline : _g1Pipeline;
                _backend = new MeshRendererBackend(_material, config.Units, usePlayerBackbuffer);
            }
            catch
            {
                End();
                throw;
            }
        }

        internal void End()
        {
            _sampler?.Dispose();
            _sampler = null;
            _backend?.Dispose();
            _backend = null;
            if (_settingsOwned)
            {
                QualitySettings.renderPipeline = _previousPipeline;
                GraphicsSettings.useScriptableRenderPipelineBatching = _previousBatcher;
                QualitySettings.vSyncCount = _previousVSync;
                Application.targetFrameRate = _previousTargetRate;
                Application.runInBackground = _previousBackground;
                _settingsOwned = false;
            }
        }

        private void OnDisable()
        {
            StopAllCoroutines();
            End();
        }

        private static RenderMode ParseMode(string name) => name switch
        {
            "G1" => RenderMode.G1,
            "G2" => RenderMode.G2,
            _ => throw new ArgumentException("Use -render-mode G1, G2 or suite.")
        };

        private static bool HasFixedResolution() =>
            Screen.width == RenderBenchmarkConfig.Width && Screen.height == RenderBenchmarkConfig.Height;

        private static int ReadInt(string[] args, string key, int fallback) =>
            int.Parse(Option(args, key, fallback.ToString(CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

        private static string Option(string[] args, string key, string fallback)
        {
            int index = Array.IndexOf(args, key);
            if (index < 0)
                return fallback;
            if (index + 1 == args.Length || args[index + 1].StartsWith("-", StringComparison.Ordinal))
                throw new ArgumentException($"Missing value for {key}.");
            return args[index + 1];
        }
    }
}
