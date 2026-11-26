using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Profiling;
using Unity.Profiling.LowLevel;
using Unity.Profiling.LowLevel.Unsafe;

namespace ProjectLegion.MovementBenchmark
{
    internal struct BenchmarkSample
    {
        internal int Frame;
        internal double StepMs;
        internal double FrameMs;
        internal double SyncRunMs;
        internal double ScheduleMs;
        internal double CompleteMs;
        internal double MainThreadMs;
        internal double GcBytes;
        internal double UsedBytes;
        internal double ReservedBytes;
    }

    internal sealed class BenchmarkMeasurements : IDisposable
    {
        private sealed class Counter : IDisposable
        {
            private ProfilerRecorder _recorder;
            private int _previousCount;

            internal Counter(List<ProfilerRecorderHandle> handles, ProfilerCategory category, string name,
                ProfilerMarkerDataUnit unit, int capacity)
            {
                foreach (var handle in handles)
                {
                    var description = ProfilerRecorderHandle.GetDescription(handle);
                    if (description.Category != category || description.UnitType != unit || description.Name != name)
                        continue;

                    _recorder = new ProfilerRecorder(handle, capacity, ProfilerRecorderOptions.SumAllSamplesInFrame);
                    if (_recorder.Valid)
                        _recorder.Start();
                    break;
                }
            }

            internal double Read(double scale)
            {
                if (!_recorder.Valid)
                    return double.NaN;

                int count = _recorder.Count;
                bool fresh = count == _previousCount + 1;
                _previousCount = count;
                return fresh && _recorder.LastValue >= 0 ? _recorder.LastValue * scale : double.NaN;
            }

            public void Dispose()
            {
                if (_recorder.Valid)
                    _recorder.Dispose();
            }
        }

        private Counter _main;
        private Counter _gc;
        private Counter _used;
        private Counter _reserved;
        private int _pending = -1;
        private long _opening;
        private long _frameStart;
        private int _frame;

        internal BenchmarkMeasurements(int samples)
        {
            Samples = new BenchmarkSample[samples];
        }

        internal BenchmarkSample[] Samples { get; }
        internal int Count { get; private set; }

        internal void StartCounters(int warmupSteps)
        {
            // No wraparound. A count delta of one identifies a fresh previous-frame sample.
            int capacity = checked(warmupSteps + Samples.Length + 4);
            var handles = new List<ProfilerRecorderHandle>();
            ProfilerRecorderHandle.GetAvailable(handles);
            _main = new Counter(handles, ProfilerCategory.Internal, "Main Thread", ProfilerMarkerDataUnit.TimeNanoseconds, capacity);
            _gc = new Counter(handles, ProfilerCategory.Memory, "GC Allocated In Frame", ProfilerMarkerDataUnit.Bytes, capacity);
            _used = new Counter(handles, ProfilerCategory.Memory, "Total Used Memory", ProfilerMarkerDataUnit.Bytes, capacity);
            _reserved = new Counter(handles, ProfilerCategory.Memory, "Total Reserved Memory", ProfilerMarkerDataUnit.Bytes, capacity);
        }

        internal void ObserveFrame(int frame, long timestamp)
        {
            double main = _main?.Read(0.000001) ?? double.NaN;
            double gc = _gc?.Read(1) ?? double.NaN;
            double used = _used?.Read(1) ?? double.NaN;
            double reserved = _reserved?.Read(1) ?? double.NaN;
            if (_pending < 0)
                return;

            ref BenchmarkSample sample = ref Samples[_pending];
            if (frame == sample.Frame + 1)
            {
                sample.FrameMs = Milliseconds(timestamp - _frameStart);
                sample.MainThreadMs = main;
                sample.GcBytes = gc;
                sample.UsedBytes = used;
                sample.ReservedBytes = reserved;
            }
            _pending = -1;
        }

        internal void Open(int frame, long frameStart, long timestamp)
        {
            _frame = frame;
            _frameStart = frameStart;
            _opening = timestamp;
        }

        internal void Close(long timestamp, double dispatchMs, double completeMs, BenchmarkMode mode)
        {
            bool sequential = mode == BenchmarkMode.EcsSequential || mode == BenchmarkMode.EcsSequentialBurst;
            bool parallel = mode == BenchmarkMode.EcsParallel || mode == BenchmarkMode.EcsParallelBurst;
            _pending = Count++;
            Samples[_pending] = new BenchmarkSample
            {
                Frame = _frame,
                StepMs = Milliseconds(timestamp - _opening),
                FrameMs = double.NaN,
                SyncRunMs = sequential ? dispatchMs : double.NaN,
                ScheduleMs = parallel ? dispatchMs : double.NaN,
                CompleteMs = parallel ? completeMs : double.NaN,
                MainThreadMs = double.NaN,
                GcBytes = double.NaN,
                UsedBytes = double.NaN,
                ReservedBytes = double.NaN
            };
        }

        internal static double Milliseconds(long ticks) => ticks * (1000.0 / Stopwatch.Frequency);

        public void Dispose()
        {
            _main?.Dispose();
            _gc?.Dispose();
            _used?.Dispose();
            _reserved?.Dispose();
        }
    }
}
