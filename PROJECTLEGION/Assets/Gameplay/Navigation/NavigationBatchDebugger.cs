using System;
using System.Diagnostics;
using Unity.Mathematics;
using UnityEngine;

namespace ProjectLegion.Gameplay
{
    [ExecuteAlways, DisallowMultipleComponent]
    public sealed class NavigationBatchDebugger : MonoBehaviour
    {
        [SerializeField] private NavigationGridAuthoring _authoring;
        [SerializeField, Range(1, 10000)] private int _requests = 1000;
        [SerializeField, Range(1, 16)] private int _workspaces = 4;
        [SerializeField, Min(1)] private int _workspaceMiB = 48;
        [SerializeField, Min(1)] private int _queueCapacity = 1024;
        [SerializeField, Min(1)] private int _resultBlocks = 4096;
        [SerializeField, Min(1)] private int _submissionBudget = 128;
        [SerializeField, Min(1)] private int _dispatchBudget = 16;
        [SerializeField, Min(1)] private int _collectionBudget = 16;
        [SerializeField, Min(1)] private int _copyNodeBudget = 4096;
        [SerializeField] private int _seed = 8301;
        [SerializeField] private bool _showPathPreview;

        private const int ResultSlots = 64;
        private const int LatencyWindow = 4096;
        private readonly long[] _latencies = new long[LatencyWindow];
        private readonly long[] _sortScratch = new long[LatencyWindow];
        private readonly int[] _preview = new int[129];
        private NavigationPathScheduler _scheduler;
        private NavigationGridAuthoring _budgetOwner;
        private NavigationGrid _batchGrid;
        private Unity.Mathematics.Random _random;
        private long _begin, _lastRefresh, _latencySum, _latencyMax, _queueSum, _dispatchSum, _collectSum;
        private int _target, _submitted, _completed, _failed, _updates;
        private int _previewCount;
        private bool _running, _clearing;
        public string Summary { get; private set; } = "Build a navigation grid to start a batch.";

        public void StartBatch()
        {
            if (_scheduler != null && _scheduler.Active != 0)
            {
                Summary = "Cancel or let the current batch drain before restarting.";
                return;
            }
            if (_authoring == null || _authoring.gameObject != gameObject || _authoring.Grid == null)
            {
                Summary = "Build a navigation grid to start a batch.";
                return;
            }

            DisposeScheduler();
            _budgetOwner = _authoring;
            int count = Mathf.Clamp(_workspaces, 1, 16);
            int queue = Mathf.Clamp(_queueCapacity, 1, 10000);
            int blocks = Mathf.Clamp(_resultBlocks, 1, 65536);
            long fixedBytes = NavigationPathScheduler.FixedPayloadBytes(count, queue, ResultSlots, blocks);
            _batchGrid = _authoring.Grid;
            long workspaceBudget = (long)Mathf.Max(1, _workspaceMiB) * 1024 * 1024;
            long available = _authoring.MemoryBudgetBytes - _authoring.LiveGridBytes - _authoring.FlowPayloadBytes - fixedBytes;
            long perWorkspace = NavigationPathScheduler.WorkspaceSize(_batchGrid.Size.x * _batchGrid.Size.y);
            if (Math.Min(workspaceBudget, available) < perWorkspace)
            {
                Summary = "The navigation memory budget cannot fit the batch buffers and one workspace.";
                return;
            }

            _scheduler = new NavigationPathScheduler(count, workspaceBudget, queue, ResultSlots, blocks);
            _scheduler.SetGrid(_batchGrid);
            _budgetOwner.BatchPayloadBytes = _scheduler.PayloadBytes;
            _budgetOwner.ObservePayload(_budgetOwner.LiveGridBytes + _scheduler.PayloadBytes);
            _target = Mathf.Clamp(_requests, 1, 10000);
            _random = new Unity.Mathematics.Random(_seed == 0 ? 1u : unchecked((uint)_seed));
            _submitted = _completed = _failed = _updates = 0;
            _previewCount = 0;
            _latencySum = _latencyMax = _queueSum = _dispatchSum = _collectSum = 0;
            _begin = Stopwatch.GetTimestamp();
            _lastRefresh = 0;
            _clearing = false;
            _running = true;
            Summary = "Submitting path requests.";
        }

        public void CancelBatch()
        {
            _running = false;
            _previewCount = 0;
            _scheduler?.CancelAll();
            Refresh();
        }

        public void ClearResults()
        {
            CancelBatch();
            _clearing = true;
            if (_scheduler?.Active > 0)
                Summary = "Cancelled. Waiting for submitted jobs to return their workspaces.";
            else
                ClearAfterDrain();
        }

        private void ClearAfterDrain()
        {
            DisposeScheduler();
            _clearing = false;
            Summary = "Results cleared.";
        }

        private void Update()
        {
            if (Application.IsPlaying(gameObject))
                Pump();
        }

#if UNITY_EDITOR
        private void EditorPump()
        {
            if (!Application.IsPlaying(gameObject))
                Pump();
        }
#endif

        private void Pump()
        {
            if (_scheduler == null)
                return;
            if (_running && (_budgetOwner == null || _budgetOwner.Grid != _batchGrid))
            {
                _running = false;
                _scheduler.SetGrid(null);
                Refresh();
            }
            if (!_running && _scheduler.Active == 0)
            {
                if (_clearing)
                    ClearAfterDrain();
                return;
            }

            if (_running)
            {
                int limit = Math.Min(_target - _submitted, Mathf.Max(1, _submissionBudget));
                for (int i = 0; i < limit; i++)
                {
                    int2 start = PickCell(), goal = PickCell();
                    if (_scheduler.Submit(start, goal, out _) != NavigationSubmit.Accepted)
                        break;
                    _submitted++;
                }
            }

            long fixedBytes = _scheduler.PayloadBytes - _scheduler.WorkspaceBytes;
            long gridBytes = _budgetOwner != null ? _budgetOwner.LiveGridBytes : 0;
            long flowBytes = _budgetOwner != null ? _budgetOwner.FlowPayloadBytes : 0;
            long available = _budgetOwner != null ? _budgetOwner.MemoryBudgetBytes - gridBytes - flowBytes - fixedBytes : 0;
            _scheduler.Tick(Mathf.Max(1, _dispatchBudget), Mathf.Max(1, _collectionBudget),
                Mathf.Max(1, _copyNodeBudget), available);
            if (_budgetOwner != null)
            {
                _budgetOwner.BatchPayloadBytes = _scheduler.PayloadBytes;
                _budgetOwner.ObservePayload(gridBytes + flowBytes + _scheduler.PayloadBytes);
            }

            _dispatchSum += _scheduler.SchedulingTicks;
            _collectSum += _scheduler.CollectionTicks;
            _updates++;
            bool updatePreview = _showPathPreview && Stopwatch.GetTimestamp() - _lastRefresh >= Stopwatch.Frequency / 4;
            while (_scheduler.TryTake(out var result))
            {
                int index = _completed + _failed;
                _latencies[index % LatencyWindow] = result.LatencyTicks;
                _latencySum += result.LatencyTicks;
                _latencyMax = Math.Max(_latencyMax, result.LatencyTicks);
                _queueSum += result.QueuedTicks;
                if (result.Status == NavigationCompletionStatus.SearchFinished &&
                    result.Path.Status == NavigationPathStatus.Success)
                {
                    _completed++;
                    if (updatePreview)
                    {
                        _previewCount = _scheduler.CopyPathPrefix(result.Handle, _preview);
                        updatePreview = false;
                    }
                }
                else
                    _failed++;
                _scheduler.Release(result.Handle);
            }

            bool done = _completed + _failed == _target || (!_running && _scheduler.Active == 0);
            if (done)
                _running = false;
            if (done || Stopwatch.GetTimestamp() - _lastRefresh >= Stopwatch.Frequency / 4)
                Refresh();
            if (done && _clearing)
                ClearAfterDrain();
        }

        private int2 PickCell()
        {
            int2 cell = default;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                cell = _random.NextInt2(int2.zero, _batchGrid.Size);
                if (_batchGrid.TryGetNode(cell, out var node) && node.Walkable)
                    break;
            }
            return cell;
        }

        private void Refresh()
        {
            if (_scheduler == null)
                return;
            _lastRefresh = Stopwatch.GetTimestamp();
            int total = _completed + _failed;
            int count = Math.Min(total, LatencyWindow);
            Array.Copy(_latencies, _sortScratch, count);
            Array.Sort(_sortScratch, 0, count);
            double p95 = count > 0 ? Ms(_sortScratch[(int)Math.Ceiling(count * 0.95) - 1]) : 0;
            double elapsed = Math.Max(1, _lastRefresh - _begin) / (double)Stopwatch.Frequency;
            long peak = _budgetOwner != null ? _budgetOwner.PeakPayloadBytes : _scheduler.PeakPayloadBytes;
            Summary = $"Submitted {_submitted}/{_target} | Queued {_scheduler.Queued} | Active {_scheduler.Active}/{_scheduler.Concurrency}\n" +
                $"Completed {_completed} | Failed {_failed} | Cancelled {_scheduler.Cancelled} | {total / elapsed:F1} results/s\n" +
                $"Latency avg/P95/max {Ms(_latencySum) / Math.Max(1, total):F2}/{p95:F2}/{Ms(_latencyMax):F2} ms " +
                $"(P95 uses last {count}) | Queue avg {Ms(_queueSum) / Math.Max(1, total):F2} ms\n" +
                $"Dispatch / collection avg {Ms(_dispatchSum) / Math.Max(1, _updates):F3}/" +
                $"{Ms(_collectSum) / Math.Max(1, _updates):F3} ms per update\n" +
                $"Workspace {_scheduler.WorkspaceBytes:N0} B | Batch {_scheduler.PayloadBytes:N0} B | " +
                $"Navigation peak payload {peak:N0} B | Result blocks used {_scheduler.ResultBlocksUsed}\n" +
                $"Logical cancel max {Ms(_scheduler.MaxLogicalCancelTicks):F3} ms | " +
                $"Active release max {Ms(_scheduler.MaxActiveReleaseTicks):F3} ms";
        }

        private static double Ms(long ticks) => ticks * 1000d / Stopwatch.Frequency;

        private void OnDrawGizmosSelected()
        {
            if (!_showPathPreview || _previewCount < 2 || _budgetOwner == null || _budgetOwner.Grid != _batchGrid)
                return;
            Gizmos.color = new Color(0.65f, 0.4f, 1f);
            Vector3 previous = PreviewPosition(_preview[0]);
            for (int i = 1; i < _previewCount; i++)
            {
                Vector3 current = PreviewPosition(_preview[i]);
                Gizmos.DrawLine(previous, current);
                previous = current;
            }
        }

        private Vector3 PreviewPosition(int index)
        {
            var cell = new int2(index % _batchGrid.Size.x, index / _batchGrid.Size.x);
            float2 xz = _batchGrid.Center(cell);
            _batchGrid.TryGetNode(cell, out var node);
            return new Vector3(xz.x, node.Height + 0.16f, xz.y);
        }

        private void DisposeScheduler()
        {
            if (_scheduler == null)
                return;
            _scheduler.Dispose();
            if (_scheduler.FinalWaitTicks > Stopwatch.Frequency / 1000)
                UnityEngine.Debug.Log($"Navigation batch final job wait: {Ms(_scheduler.FinalWaitTicks):F3} ms.", this);
            _scheduler = null;
            if (_budgetOwner != null)
                _budgetOwner.BatchPayloadBytes = 0;
        }

        private void OnEnable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update += EditorPump;
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.update -= EditorPump;
#endif
            _running = false;
            DisposeScheduler();
        }
    }
}
