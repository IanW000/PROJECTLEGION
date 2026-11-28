using System;
using System.Diagnostics;
using System.Threading;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;

namespace ProjectLegion.Gameplay
{
    internal enum NavigationSubmit { Accepted, NoGrid, Full, Stale, NoWorkspaceBudget }
    internal enum NavigationCompletionStatus { SearchFinished, PathExceedsResultCapacity }

    internal readonly struct NavigationRequestToken : IEquatable<NavigationRequestToken>
    {
        internal readonly long Owner, Id, Revision, GridGeneration;
        internal readonly int Slot;

        internal NavigationRequestToken(long owner, long id, long revision, long gridGeneration, int slot)
        {
            Owner = owner;
            Id = id;
            Revision = revision;
            GridGeneration = gridGeneration;
            Slot = slot;
        }

        public bool Equals(NavigationRequestToken other) => Owner == other.Owner && Id == other.Id &&
            Revision == other.Revision && GridGeneration == other.GridGeneration && Slot == other.Slot;
    }

    internal readonly struct NavigationResultHandle
    {
        internal readonly NavigationRequestToken Request;
        internal NavigationResultHandle(NavigationRequestToken request) => Request = request;
    }

    internal struct NavigationScheduledResult
    {
        internal NavigationResultHandle Handle;
        internal NavigationPathResult Path;
        internal NavigationCompletionStatus Status;
        internal long QueuedTicks, LatencyTicks;
        internal int Tag;
    }

    // The owner thread manages all requests and results. Jobs only access their private workspace and graph view.
    internal sealed class NavigationPathScheduler : IDisposable
    {
        private enum State : byte { Free, Queued, Running, Ready, Delivered }
        private struct Request
        {
            internal NavigationRequestToken Token;
            internal int2 Start, Goal;
            internal long Submitted, Dispatched, Published;
            internal int Prev, Next, Worker, Chain, Tag;
            internal State State;
            internal NavigationPathResult Result;
            internal NavigationCompletionStatus Status;
        }

        private sealed class Worker
        {
            internal NavigationPathfinder Search;
            internal LayeredSearchWorkspace Layered;
            internal JobHandle Job;
            internal int Slot = -1, Chain = -1, Block, Copied;
            internal bool Busy, Finished;
            internal long Cancelled;
        }

        private struct SlotList
        {
            internal int Head, Tail;
            internal static SlotList Empty => new SlotList { Head = -1, Tail = -1 };
        }

        private static long _nextOwner;
        private readonly long _owner = ReserveOwner();
        private readonly int _queueCapacity;
        private readonly long _workspaceBudget;
        private readonly Worker[] _workers;
        private readonly NavigationPathResultPool _results;
        private NativeArray<Request> _requests;
        private SlotList _queue = SlotList.Empty, _ready = SlotList.Empty;
        private int _free, _nextCollector, _busy;
        private Worker _reservationWaiter;
        private long _lastId, _generation;
        private NavigationGrid _grid;
        private LayeredNavigationSnapshot _layered;
        private bool _layeredWorkers;
        private bool _disposed;
        private int _workspaceNodes;

        internal int Queued { get; private set; }
        internal int Active => _busy;
        internal int Concurrency { get; private set; }
        internal int Completed { get; private set; }
        internal int Failed { get; private set; }
        internal int Cancelled { get; private set; }
        internal int ResultBlocksUsed => _results.BlockCount - _results.FreeBlocks;
        internal long WorkspaceBytes => (long)Concurrency * (_layeredWorkers ? LayeredSearchWorkspace.Budget(_workspaceNodes) : WorkspaceSize(_workspaceNodes));
        internal long PayloadBytes => WorkspaceBytes + _results.PayloadBytes +
            (long)_requests.Length * UnsafeUtility.SizeOf<Request>();
        internal long PeakPayloadBytes { get; private set; }
        internal long MaxLogicalCancelTicks { get; private set; }
        internal long MaxActiveReleaseTicks { get; private set; }
        internal long SumActiveReleaseTicks { get; private set; }
        internal int ActiveReleaseSamples { get; private set; }
        internal long SchedulingTicks { get; private set; }
        internal long CollectionTicks { get; private set; }
        internal long FinalWaitTicks { get; private set; }

        internal NavigationPathScheduler(int workspaceCount, long workspaceBytes, int queueCapacity,
            int resultSlots, int resultBlocks)
        {
            if (workspaceCount < 1 || workspaceCount > 16 || workspaceBytes < 1 ||
                queueCapacity < 1 || resultSlots < 1 || resultBlocks < 1)
                throw new ArgumentOutOfRangeException();
            _queueCapacity = queueCapacity;
            _workspaceBudget = workspaceBytes;
            _workers = new Worker[workspaceCount];
            for (int i = 0; i < _workers.Length; i++)
                _workers[i] = new Worker();
            _results = new NavigationPathResultPool(resultBlocks);
            try
            {
                int slots = checked(queueCapacity + workspaceCount + resultSlots);
                _requests = new NativeArray<Request>(slots, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                for (int i = 0; i < slots; i++)
                    _requests[i] = new Request { Next = i + 1 < slots ? i + 1 : -1 };
                PeakPayloadBytes = PayloadBytes;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal static long WorkspaceSize(int nodes) => (long)nodes * NavigationPathfinder.BytesPerNode +
            UnsafeUtility.SizeOf<NavigationPathResult>();

        internal static long FixedPayloadBytes(int workspaceCount, int queueCapacity, int resultSlots, int blocks) =>
            ((long)queueCapacity + workspaceCount + resultSlots) * UnsafeUtility.SizeOf<Request>() +
            (long)blocks * (NavigationPathResultPool.BlockSize + 1) * sizeof(int);

        private static long ReserveOwner()
        {
            while (true)
            {
                long current = Interlocked.Read(ref _nextOwner);
                long next = checked(current + 1);
                if (Interlocked.CompareExchange(ref _nextOwner, next, current) == current)
                    return next;
            }
        }

        internal void SetGrid(NavigationGrid grid)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(NavigationPathScheduler));
            if (grid != null && !grid.AcceptsQueries)
                grid = null;
            if (_layered == null && ReferenceEquals(_grid, grid))
                return;

            long generation = checked(_generation + 1);
            CancelAll();
            _grid = grid;
            _layered = null;
            _generation = generation;
        }

        internal void SetLayeredSnapshot(LayeredNavigationSnapshot snapshot)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NavigationPathScheduler));
            if (snapshot != null && !snapshot.AcceptsQueries) snapshot = null;
            if (_grid == null && ReferenceEquals(_layered, snapshot)) return;
            long generation = checked(_generation + 1);
            CancelAll();
            _grid = null;
            _layered = snapshot;
            _generation = generation;
        }

        internal NavigationSubmit SubmitLayered(LayerNodeHandle start, LayerNodeHandle goal,
            out NavigationRequestToken token, int tag = 0)
        {
            token = default;
            if (_layered == null || !_layered.Matches(start) || !_layered.Matches(goal)) return NavigationSubmit.Stale;
            return SubmitCore(new int2(start.Id, 0), new int2(goal.Id, 0), out token, tag);
        }

        internal NavigationSubmit ReplaceLayered(NavigationRequestToken token, LayerNodeHandle start,
            LayerNodeHandle goal, out NavigationRequestToken replacement, int tag = 0)
        {
            replacement = default;
            if (_layered == null || !_layered.Matches(start) || !_layered.Matches(goal)) return NavigationSubmit.Stale;
            return ReplaceCore(token, new int2(start.Id, 0), new int2(goal.Id, 0), out replacement, tag);
        }

        internal NavigationSubmit Submit(int2 start, int2 goal, out NavigationRequestToken token, int tag = 0)
        {
            token = default;
            return _layered == null ? SubmitCore(start, goal, out token, tag) : NavigationSubmit.Stale;
        }

        private NavigationSubmit SubmitCore(int2 start, int2 goal, out NavigationRequestToken token, int tag)
        {
            token = default;
            var availability = Availability();
            if (availability != NavigationSubmit.Accepted)
                return availability;
            if (Queued >= _queueCapacity || _free < 0)
                return NavigationSubmit.Full;

            long id = checked(_lastId + 1);
            int slot = _free;
            _free = _requests[slot].Next;
            _lastId = id;
            token = new NavigationRequestToken(_owner, id, 1, _generation, slot);
            Enqueue(token, start, goal, tag);
            return NavigationSubmit.Accepted;
        }

        internal NavigationSubmit Replace(NavigationRequestToken token, int2 start, int2 goal,
            out NavigationRequestToken replacement, int tag = 0)
        {
            replacement = default;
            return _layered == null ? ReplaceCore(token, start, goal, out replacement, tag) : NavigationSubmit.Stale;
        }

        private NavigationSubmit ReplaceCore(NavigationRequestToken token, int2 start, int2 goal,
            out NavigationRequestToken replacement, int tag)
        {
            replacement = default;
            if (!Matches(token, out var request))
                return NavigationSubmit.Stale;
            if (Queued >= _queueCapacity && request.State != State.Queued)
                return NavigationSubmit.Full;

            long revision = checked(token.Revision + 1);
            long begin = Stopwatch.GetTimestamp();
            UnlinkAndRelease(request, begin);
            replacement = new NavigationRequestToken(_owner, token.Id, revision, _generation, token.Slot);
            Enqueue(replacement, start, goal, tag);
            MaxLogicalCancelTicks = Math.Max(MaxLogicalCancelTicks, Stopwatch.GetTimestamp() - begin);
            return NavigationSubmit.Accepted;
        }

        internal bool Cancel(NavigationRequestToken token)
        {
            long begin = Stopwatch.GetTimestamp();
            if (!Matches(token, out var request))
                return false;
            Retire(token.Slot, request, begin);
            MaxLogicalCancelTicks = Math.Max(MaxLogicalCancelTicks, Stopwatch.GetTimestamp() - begin);
            return true;
        }

        internal void CancelAll()
        {
            if (_disposed)
                return;
            long begin = Stopwatch.GetTimestamp();
            int previous = Cancelled;
            for (int i = 0; i < _requests.Length; i++)
            {
                var request = _requests[i];
                if (request.State != State.Free)
                    Retire(i, request, begin);
            }
            if (Cancelled != previous)
                MaxLogicalCancelTicks = Math.Max(MaxLogicalCancelTicks, Stopwatch.GetTimestamp() - begin);
        }

        internal bool TryTake(out NavigationScheduledResult result)
        {
            result = default;
            if (!HasGrid || _ready.Head < 0)
                return false;
            int slot = _ready.Head;
            var request = _requests[slot];
            Remove(ref _ready, request);
            request.State = State.Delivered;
            _requests[slot] = request;
            result = new NavigationScheduledResult
            {
                Handle = new NavigationResultHandle(request.Token),
                Status = request.Status,
                Path = request.Result,
                QueuedTicks = request.Dispatched - request.Submitted,
                LatencyTicks = request.Published - request.Submitted,
                Tag = request.Tag
            };
            return true;
        }

        internal bool CopyPath(NavigationResultHandle handle, NativeArray<int> destination)
        {
            if (_layered != null || !Matches(handle.Request, out var request) || request.State != State.Delivered ||
                request.Chain < 0 || destination.Length < request.Result.PathCount)
                return false;

            int node = 0;
            for (int block = request.Chain; block >= 0; block = _results.Next(block))
            for (int offset = 0; offset < NavigationPathResultPool.BlockSize && node < request.Result.PathCount; offset++)
                destination[node++] = _results.Read(block, offset);
            return true;
        }

        internal bool CopyLayeredPath(NavigationResultHandle handle, NativeArray<LayerNodeHandle> nodes,
            NativeArray<LayerEdgeHandle> edges)
        {
            if (_layered == null || !Matches(handle.Request, out var request) || request.State != State.Delivered ||
                request.Chain < 0 || nodes.Length < request.Result.PathCount || edges.Length < request.Result.PathCount - 1)
                return false;
            long generation = _layered.Generation;
            int count = request.Result.PathCount;
            int cursor = 0;
            for (int block = request.Chain; block >= 0; block = _results.Next(block))
            for (int offset = 0; offset < NavigationPathResultPool.BlockSize && cursor < 2L * count - 1; offset++, cursor++)
            {
                int value = _results.Read(block, offset);
                if (cursor < count) nodes[cursor] = new LayerNodeHandle(generation, value);
                else edges[cursor - count] = new LayerEdgeHandle(generation, value);
            }
            return true;
        }

        internal bool IsCurrent(NavigationResultHandle handle) =>
            Matches(handle.Request, out var request) && request.State == State.Delivered;

        internal int CopyPathPrefix(NavigationResultHandle handle, int[] destination)
        {
            if (_layered != null || !Matches(handle.Request, out var request) || request.State != State.Delivered ||
                request.Chain < 0)
                return 0;

            int limit = Math.Min(destination.Length, request.Result.PathCount);
            int node = 0;
            for (int block = request.Chain; block >= 0 && node < limit; block = _results.Next(block))
            for (int offset = 0; offset < NavigationPathResultPool.BlockSize && node < limit; offset++)
                destination[node++] = _results.Read(block, offset);
            return node;
        }

        internal bool Release(NavigationResultHandle handle)
        {
            if (!Matches(handle.Request, out var request) || request.State != State.Delivered)
                return false;
            _results.Release(request.Chain);
            Free(handle.Request.Slot);
            return true;
        }

        internal void Tick(int dispatchBudget, int collectBudget, int copyBudget, long availableWorkspaceBytes = long.MaxValue)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(NavigationPathScheduler));
            if ((_grid != null && !_grid.AcceptsQueries) || (_layered != null && !_layered.AcceptsQueries))
                SetGrid(null);

            long begin = Stopwatch.GetTimestamp();
            Collect(Math.Max(0, collectBudget), Math.Max(0, copyBudget));
            CollectionTicks = Stopwatch.GetTimestamp() - begin;

            begin = Stopwatch.GetTimestamp();
            PrepareWorkspaces(availableWorkspaceBytes);
            int scheduled = 0;
            for (int i = 0; i < Concurrency && _queue.Head >= 0 && scheduled < dispatchBudget &&
                _reservationWaiter == null && _workspaceNodes == GridNodes && _layeredWorkers == (_layered != null); i++)
            {
                var worker = _workers[i];
                if (worker.Busy)
                    continue;
                int slot = _queue.Head;
                var request = _requests[slot];
                worker.Job = _layered != null ? worker.Layered.Schedule(_layered, request.Start.x, request.Goal.x)
                    : worker.Search.Schedule(_grid, request.Start, request.Goal);
                Remove(ref _queue, request);
                Queued--;
                worker.Busy = true;
                worker.Finished = false;
                worker.Slot = slot;
                worker.Chain = -1;
                worker.Copied = 0;
                _busy++;
                request.State = State.Running;
                request.Worker = i;
                request.Dispatched = Stopwatch.GetTimestamp();
                _requests[slot] = request;
                scheduled++;
            }
            if (scheduled > 0)
                JobHandle.ScheduleBatchedJobs();
            SchedulingTicks = Stopwatch.GetTimestamp() - begin;
        }

        private bool HasGrid => !_disposed && (_grid?.AcceptsQueries == true || _layered?.AcceptsQueries == true);
        private int GridNodes => _layered?.NodeCount ?? (_grid.Size.x * _grid.Size.y);

        private long PerWorker(int nodes) => _layered != null ? LayeredSearchWorkspace.Budget(nodes) : WorkspaceSize(nodes);

        private NavigationSubmit Availability()
        {
            if (!HasGrid)
                return NavigationSubmit.NoGrid;
            return PerWorker(GridNodes) <= _workspaceBudget ? NavigationSubmit.Accepted :
                NavigationSubmit.NoWorkspaceBudget;
        }

        private void PrepareWorkspaces(long availableBytes)
        {
            if (_busy > 0 || Queued == 0 || !HasGrid)
                return;
            int nodes = GridNodes;
            long perWorker = PerWorker(nodes);
            int desired = (int)Math.Min(_workers.Length, Math.Max(0, Math.Min(availableBytes, _workspaceBudget)) / perWorker);
            if (nodes == _workspaceNodes && desired == Concurrency && _layeredWorkers == (_layered != null))
                return;
            DisposeWorkspaces();
            _workspaceNodes = nodes;
            _layeredWorkers = _layered != null;
            while (Concurrency < desired)
            {
                if (_layeredWorkers) _workers[Concurrency].Layered = new LayeredSearchWorkspace(nodes);
                else _workers[Concurrency].Search = new NavigationPathfinder(nodes);
                Concurrency++;
                PeakPayloadBytes = Math.Max(PeakPayloadBytes, PayloadBytes);
            }
        }

        private void Collect(int resultBudget, int copyBudget)
        {
            for (int visit = 0; visit < _workers.Length; visit++)
            {
                int index = (_nextCollector + visit) % _workers.Length;
                var worker = _workers[index];
                if (!worker.Busy)
                    continue;
                if (!worker.Finished)
                {
                    if (!worker.Job.IsCompleted)
                        continue;
                    worker.Job.Complete();
                    worker.Finished = true;
                }
                if (worker.Slot < 0)
                {
                    ResetWorker(worker);
                    continue;
                }
                if (resultBudget == 0)
                    continue;

                var request = _requests[worker.Slot];
                var path = worker.Layered != null ? worker.Layered.Finish() : worker.Search.ReadResult();
                if (path.Status == NavigationPathStatus.Success)
                {
                    long items = worker.Layered != null ? 2L * path.PathCount - 1 : path.PathCount;
                    int blocks = items > int.MaxValue ? int.MaxValue : NavigationPathResultPool.BlocksFor((int)items);
                    if (blocks > _results.BlockCount)
                    {
                        request.Status = NavigationCompletionStatus.PathExceedsResultCapacity;
                        path.PathCount = 0;
                    }
                    else if (!Copy(worker, blocks, path.PathCount, (int)items, ref copyBudget))
                        continue;
                }

                request.Result = path;
                request.Chain = worker.Chain;
                request.State = State.Ready;
                request.Published = Stopwatch.GetTimestamp();
                Add(ref _ready, worker.Slot, ref request);
                if (path.Status == NavigationPathStatus.Success && request.Status == NavigationCompletionStatus.SearchFinished)
                    Completed++;
                else
                    Failed++;
                ResetWorker(worker);
                resultBudget--;
            }
            _nextCollector = (_nextCollector + 1) % _workers.Length;
        }

        private bool Copy(Worker worker, int blocks, int nodes, int count, ref int budget)
        {
            if (budget == 0)
                return false;
            if (worker.Chain < 0)
            {
                if (_reservationWaiter != null && _reservationWaiter != worker)
                    return false;
                if (!_results.TryReserve(blocks, out int first))
                {
                    _reservationWaiter = worker;
                    return false;
                }
                _reservationWaiter = null;
                worker.Chain = worker.Block = first;
            }

            while (worker.Copied < count && budget > 0)
            {
                int offset = worker.Copied % NavigationPathResultPool.BlockSize;
                int value = worker.Layered == null ? worker.Search.PathNode(worker.Copied) :
                    worker.Copied < nodes ? worker.Layered.Node(worker.Copied) : worker.Layered.Edge(worker.Copied - nodes);
                _results.Write(worker.Block, offset, value);
                worker.Copied++;
                budget--;
                if (offset == NavigationPathResultPool.BlockSize - 1)
                    worker.Block = _results.Next(worker.Block);
            }
            return worker.Copied == count;
        }

        private void ResetWorker(Worker worker)
        {
            if (_reservationWaiter == worker)
                _reservationWaiter = null;
            if (worker.Cancelled != 0)
            {
                long released = Stopwatch.GetTimestamp() - worker.Cancelled;
                MaxActiveReleaseTicks = Math.Max(MaxActiveReleaseTicks, released);
                SumActiveReleaseTicks += released;
                ActiveReleaseSamples++;
            }
            worker.Layered?.DiscardCompleted();
            worker.Cancelled = 0;
            worker.Busy = false;
            worker.Slot = -1;
            worker.Chain = -1;
            _busy--;
        }

        private void Enqueue(NavigationRequestToken token, int2 start, int2 goal, int tag)
        {
            var request = new Request
            {
                Token = token, Start = start, Goal = goal, Tag = tag, State = State.Queued,
                Submitted = Stopwatch.GetTimestamp(), Worker = -1, Chain = -1
            };
            Add(ref _queue, token.Slot, ref request);
            Queued++;
        }

        private bool Matches(NavigationRequestToken token, out Request request)
        {
            request = default;
            if (!HasGrid || token.Owner != _owner || token.GridGeneration != _generation ||
                (uint)token.Slot >= (uint)_requests.Length)
                return false;
            request = _requests[token.Slot];
            return request.State != State.Free && request.Token.Equals(token);
        }

        private void Retire(int slot, Request request, long begin)
        {
            UnlinkAndRelease(request, begin);
            Free(slot);
        }

        private void UnlinkAndRelease(Request request, long begin)
        {
            if (request.State == State.Queued)
            {
                Remove(ref _queue, request);
                Queued--;
            }
            if (request.State == State.Ready)
                Remove(ref _ready, request);
            if (request.State == State.Running)
            {
                var worker = _workers[request.Worker];
                if (_reservationWaiter == worker)
                    _reservationWaiter = null;
                worker.Slot = -1;
                worker.Cancelled = begin;
                _results.Release(worker.Chain);
                worker.Chain = -1;
                if (worker.Finished)
                    ResetWorker(worker);
            }
            _results.Release(request.Chain);
            Cancelled++;
        }

        private void Add(ref SlotList list, int slot, ref Request request)
        {
            request.Prev = list.Tail;
            request.Next = -1;
            if (list.Tail >= 0)
            {
                var tail = _requests[list.Tail];
                tail.Next = slot;
                _requests[list.Tail] = tail;
            }
            else
                list.Head = slot;
            list.Tail = slot;
            _requests[slot] = request;
        }

        private void Remove(ref SlotList list, Request request)
        {
            if (request.Prev >= 0)
            {
                var previous = _requests[request.Prev];
                previous.Next = request.Next;
                _requests[request.Prev] = previous;
            }
            else
                list.Head = request.Next;

            if (request.Next >= 0)
            {
                var next = _requests[request.Next];
                next.Prev = request.Prev;
                _requests[request.Next] = next;
            }
            else
                list.Tail = request.Prev;
        }

        private void Free(int slot)
        {
            _requests[slot] = new Request { Next = _free };
            _free = slot;
        }

        private void DisposeWorkspaces()
        {
            for (int i = 0; i < Concurrency; i++)
            {
                _workers[i].Search?.Dispose();
                _workers[i].Layered?.Dispose();
                _workers[i].Layered = null;
                _workers[i].Search = null;
            }
            Concurrency = 0;
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            long begin = Stopwatch.GetTimestamp();
            foreach (var worker in _workers)
                if (worker.Busy && !worker.Finished)
                    worker.Job.Complete();
            FinalWaitTicks = Stopwatch.GetTimestamp() - begin;
            DisposeWorkspaces();
            if (_requests.IsCreated) _requests.Dispose();
            _results.Dispose();
        }
    }
}

