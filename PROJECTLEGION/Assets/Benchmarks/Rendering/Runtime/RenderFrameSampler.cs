using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;
using UnityEngine;

namespace ProjectLegion.RenderingBenchmark
{
    internal static class RenderClock
    {
#if UNITY_STANDALONE_WIN || UNITY_EDITOR_WIN
        // Unity's native FrameTiming timestamps use the Windows QPC epoch.
        // Mono Stopwatch can expose the same frequency with a different origin.
        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceCounter(out long value);

        [DllImport("kernel32.dll")]
        private static extern bool QueryPerformanceFrequency(out long value);

        internal static readonly long Frequency =
            QueryPerformanceFrequency(out long value) ? value : Stopwatch.Frequency;

        internal static long Timestamp() =>
            QueryPerformanceCounter(out long value) ? value : throw new InvalidOperationException("Windows QPC is unavailable.");

        internal const bool HasNativeQpc = true;
#else
        internal static readonly long Frequency = Stopwatch.Frequency;
        internal static long Timestamp() => Stopwatch.GetTimestamp();
        internal const bool HasNativeQpc = false;
#endif
    }

    internal sealed class RenderFrameSampler : IDisposable
    {
        internal static readonly string[] CounterNames =
        {
            "Draw Calls Count", "Batches Count", "SetPass Calls Count", "Triangles Count",
            "Vertices Count", "Gfx Used Memory", "Gfx Reserved Memory", "GC Allocated In Frame"
        };

        private readonly ProfilerRecorder[] _recorders = new ProfilerRecorder[CounterNames.Length];
        private readonly int[] _previous = new int[CounterNames.Length];
        private readonly FrameTiming[] _latest = new FrameTiming[32];
        private int _pending = -1, _unityFrame;
        private long _tick;

        internal readonly double[,] Frames;
        internal readonly int[] UnityFrames;
        internal readonly TimingWindow Timings;
        internal readonly bool TimingEnabled;
        internal readonly ulong CpuTimerFrequency, GpuTimerFrequency;
        internal int Count { get; private set; }
        internal string BatchPathProbe { get; set; } = "N/A";

        internal RenderFrameSampler(int warmup, int samples, bool collect = true)
        {
            Frames = new double[samples, CounterNames.Length + 1];
            UnityFrames = new int[samples];
            Timings = new TimingWindow(samples);
            if (!collect)
                return;

            TimingEnabled = FrameTimingManager.IsFeatureEnabled();
            if (TimingEnabled)
            {
                CpuTimerFrequency = FrameTimingManager.GetCpuTimerFrequency();
                GpuTimerFrequency = FrameTimingManager.GetGpuTimerFrequency();
            }

            try
            {
                var handles = new List<ProfilerRecorderHandle>();
                ProfilerRecorderHandle.GetAvailable(handles);
                for (int i = 0; i < _recorders.Length; i++)
                {
                    bool memory = i >= 5;
                    foreach (var handle in handles)
                    {
                        var info = ProfilerRecorderHandle.GetDescription(handle);
                        if (info.Category != (memory ? ProfilerCategory.Memory : ProfilerCategory.Render) ||
                            info.Name != CounterNames[i] ||
                            info.UnitType != (memory ? ProfilerMarkerDataUnit.Bytes : ProfilerMarkerDataUnit.Count))
                            continue;
                        _recorders[i] = new ProfilerRecorder(handle, warmup + samples + 128,
                            ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.StartImmediately);
                        break;
                    }
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void Advance(int frame, long tick)
        {
            bool adjacent = _pending >= 0 && frame == _unityFrame + 1;
            if (adjacent)
                Frames[_pending, 0] = (tick - _tick) * (1000.0 / RenderClock.Frequency);

            for (int i = 0; i < _recorders.Length; i++)
            {
                ref ProfilerRecorder recorder = ref _recorders[i];
                if (!recorder.Valid)
                    continue;
                int count = recorder.Count;
                if (adjacent && count == _previous[i] + 1 && recorder.LastValue >= 0)
                    Frames[_pending, i + 1] = recorder.LastValue;
                _previous[i] = count;
            }
            _pending = -1;
        }

        internal void BeginSample(int frame, long tick)
        {
            _pending = Count++;
            UnityFrames[_pending] = _unityFrame = frame;
            _tick = tick;
            for (int i = 0; i < Frames.GetLength(1); i++)
                Frames[_pending, i] = double.NaN;
            if (TimingEnabled)
                FrameTimingManager.CaptureFrameTimings();
        }

        internal void PollTimings()
        {
            if (!TimingEnabled)
                return;

            uint count = FrameTimingManager.GetLatestTimings((uint)_latest.Length, _latest);
            for (int index = 0; index < count; index++)
                Timings.Accept(_latest[index], RenderClock.HasNativeQpc && CpuTimerFrequency == (ulong)RenderClock.Frequency);
        }

        public void Dispose()
        {
            for (int i = 0; i < _recorders.Length; i++)
                if (_recorders[i].Valid)
                    _recorders[i].Dispose();
        }
    }

    internal sealed class RenderPathProbe : IDisposable
    {
        internal const int Frames = 4;
        private ProfilerRecorder _recorder;
        private string _marker;

        internal RenderPathProbe()
        {
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            foreach (var handle in handles)
            {
                var info = ProfilerRecorderHandle.GetDescription(handle);
                if (info.Name != "RenderLoop.DrawSRPBatcher" && info.Name != "SRPBatcher.Draw" &&
                    info.Name != "RenderLoopNewBatcher.Draw")
                    continue;
                _marker = info.Name;
                _recorder = new ProfilerRecorder(handle, Frames + 1,
                    ProfilerRecorderOptions.SumAllSamplesInFrame | ProfilerRecorderOptions.StartImmediately);
                break;
            }
        }

        internal string Observation()
        {
            if (!_recorder.Valid)
                return "N/A";
            for (int i = 0; i < _recorder.Count; i++)
                if (_recorder.GetSample(i).Count > 0 && _recorder.GetSample(i).Value > 0)
                    return _marker + " observed";
            return _marker + " not observed";
        }

        public void Dispose()
        {
            if (_recorder.Valid)
                _recorder.Dispose();
        }
    }

    // The Windows QPC window admits primary timing records into a separate stream.
    // FrameTiming exposes timestamps, not Unity frame IDs.
    internal sealed class TimingWindow
    {
        private readonly HashSet<ulong> _seen;
        internal readonly FrameTiming[] Records;
        internal ulong Lower { get; private set; }
        internal ulong Upper { get; private set; }
        internal ulong FirstReturned { get; private set; }
        internal ulong LastReturned { get; private set; }
        internal int Returned { get; private set; }
        internal int Count { get; private set; }
        internal int Overflow { get; private set; }
        internal int Missing => Records.Length - Count;

        internal TimingWindow(int capacity)
        {
            Records = new FrameTiming[capacity];
            _seen = new HashSet<ulong>(capacity);
        }

        internal void SetWindow(long lower, long upper)
        {
            Lower = (ulong)lower;
            Upper = (ulong)upper;
        }

        internal void Accept(FrameTiming timing, bool matchingFrequency = true)
        {
            ulong timestamp = timing.frameStartTimestamp;
            Returned++;
            if (timestamp != 0 && (FirstReturned == 0 || timestamp < FirstReturned))
                FirstReturned = timestamp;
            LastReturned = Math.Max(LastReturned, timestamp);
            if (!matchingFrequency || timestamp == 0 || timestamp <= Lower || timestamp > Upper)
                return;
            if (Count == Records.Length)
            {
                if (!_seen.Contains(timestamp))
                    Overflow = 1; // Exact admission is ambiguous once the expected capacity is exceeded.
                return;
            }
            if (_seen.Add(timestamp))
                Records[Count++] = timing;
        }
    }
}
