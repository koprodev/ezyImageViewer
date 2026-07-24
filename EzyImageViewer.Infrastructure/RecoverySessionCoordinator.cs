namespace EzyImageViewer.Infrastructure;

/// <summary>한 앱 세션의 창별 복구 스냅숏을 디바운스하며 조율.</summary>
public sealed class RecoverySessionCoordinator : IAsyncDisposable
{
    public static readonly TimeSpan DefaultDebounceDelay = TimeSpan.FromSeconds(2);

    private readonly RecoveryStore _store;
    private readonly TimeSpan _debounceDelay;
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private readonly Action<Exception> _reportError;
    private readonly Action<RecoveryRecord> _reportSaved;
    private readonly Action _reportAvailable;
    private readonly object _sync = new();
    private readonly Dictionary<Guid, WindowState> _windows = [];

    private Guid _sessionId;
    private bool _started;
    private bool _completionRequested;
    private bool _disposed;
    private Task? _completionTask;
    private Task? _disposeTask;

    public RecoverySessionCoordinator(
        RecoveryStore store,
        TimeSpan? debounceDelay = null,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null,
        Action<Exception>? reportError = null,
        Action<RecoveryRecord>? reportSaved = null,
        Action? reportAvailable = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _debounceDelay = debounceDelay ?? DefaultDebounceDelay;
        if (_debounceDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(debounceDelay));
        _delayAsync = delayAsync ?? Task.Delay;
        _reportError = reportError ?? (_ => { });
        _reportSaved = reportSaved ?? (_ => { });
        _reportAvailable = reportAvailable ?? (() => { });
    }

    /// <summary>호출자가 이전 복구 후보를 열거한 뒤 새 세션 시작.</summary>
    public void Start(Guid sessionId)
    {
        ValidateId(sessionId, nameof(sessionId));
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_started)
                throw new InvalidOperationException("The recovery session has already started.");

            _store.BeginSession(sessionId);
            _sessionId = sessionId;
            _started = true;
        }
    }

    /// <summary>창의 최신 복구 스냅숏 저장 예약.</summary>
    public Task Schedule(
        Guid windowId,
        Func<CancellationToken, Task<RecoveryRecord>> snapshotFactory)
    {
        ValidateId(windowId, nameof(windowId));
        ArgumentNullException.ThrowIfNull(snapshotFactory);

        WorkItem work;
        WorkItem[] superseded;
        lock (_sync)
        {
            EnsureSchedulingAllowed();
            if (!_windows.TryGetValue(windowId, out var window))
            {
                window = new WindowState();
                _windows.Add(windowId, window);
            }
            if (window.Closed)
                throw new InvalidOperationException("Recovery is closed for this window.");
            if (window.Clearing)
                throw new InvalidOperationException("The recovery checkpoint is being cleared for this window.");

            superseded = CaptureForCancellation(window.WorkItems);
            var revision = checked(++window.Revision);
            work = new WorkItem(revision);
            window.WorkItems.Add(work);
            work.Execution = RunScheduledAsync(
                windowId,
                window,
                work,
                snapshotFactory,
                work.StartSignal.Task);
        }

        CancelAndRelease(superseded);
        work.StartSignal.TrySetResult(true);
        return work.Execution;
    }

    /// <summary>창 하나를 멈추고 작업을 비운 뒤 늦은 저장이 되살리지 못하게 차단.</summary>
    public Task StopWindowAsync(Guid windowId)
    {
        ValidateId(windowId, nameof(windowId));

        StopRequest? request;
        Task stopTask;
        lock (_sync)
        {
            EnsureStarted();
            ThrowIfDisposed();
            if (!_windows.TryGetValue(windowId, out var window))
            {
                if (_completionRequested)
                    throw new InvalidOperationException("Recovery session completion has started.");
                window = new WindowState();
                _windows.Add(windowId, window);
            }

            request = PrepareStopLocked(windowId, window);
            stopTask = window.StopTask!;
        }

        ExecuteStopRequest(request);
        return stopTask;
    }

    /// <summary>창은 후속 편집에 열어 둔 채 체크포인트 하나를 비우고 삭제.</summary>
    public Task ClearWindowAsync(Guid windowId)
    {
        ValidateId(windowId, nameof(windowId));

        StopRequest request;
        Task clearTask;
        lock (_sync)
        {
            EnsureSchedulingAllowed();
            if (!_windows.TryGetValue(windowId, out var window))
            {
                window = new WindowState();
                _windows.Add(windowId, window);
            }
            if (window.Closed)
                throw new InvalidOperationException("Recovery is closed for this window.");
            if (window.ClearTask is not null)
                return window.ClearTask;

            window.Clearing = true;
            _ = checked(++window.Revision);
            var work = CaptureForCancellation(window.WorkItems);
            var drainTasks = window.WorkItems.Select(item => item.Execution).ToArray();
            var startSignal = CreateSignal();
            window.ClearTask = ClearAndReopenWindowAsync(
                windowId,
                window,
                drainTasks,
                startSignal.Task);
            request = new StopRequest(work, startSignal);
            clearTask = window.ClearTask;
        }

        ExecuteStopRequest(request);
        return clearTask;
    }

    /// <summary>모든 창 작업을 비운 뒤 세션을 정상 완료.</summary>
    public Task CompleteAsync()
    {
        List<StopRequest> requests;
        TaskCompletionSource<bool> startSignal;
        Task completionTask;
        lock (_sync)
        {
            EnsureStarted();
            ThrowIfDisposed();
            if (_completionTask is not null)
                return _completionTask;

            _completionRequested = true;
            requests = [];
            foreach (var pair in _windows)
            {
                var request = PrepareStopLocked(pair.Key, pair.Value);
                if (request is not null)
                    requests.Add(request);
            }

            var stopTasks = _windows.Values
                .Select(window => window.StopTask!)
                .ToArray();
            startSignal = CreateSignal();
            _completionTask = CompleteCoreAsync(stopTasks, startSignal.Task);
            completionTask = _completionTask;
        }

        foreach (var request in requests)
            ExecuteStopRequest(request);
        startSignal.TrySetResult(true);
        return completionTask;
    }

    /// <summary>충돌 표식과 저장 스냅숏은 남긴 채 백그라운드 작업을 비정상 중지.</summary>
    public ValueTask DisposeAsync()
    {
        WorkItem[] workToCancel;
        TaskCompletionSource<bool>? startSignal = null;
        Task disposeTask;
        lock (_sync)
        {
            if (_disposeTask is not null)
                return new ValueTask(_disposeTask);

            _disposed = true;
            if (_completionTask is not null)
            {
                _disposeTask = _completionTask;
                return new ValueTask(_disposeTask);
            }

            var activeWork = new List<WorkItem>();
            var drainTasks = new List<Task>();
            foreach (var window in _windows.Values)
            {
                window.Closed = true;
                _ = checked(++window.Revision);
                var captured = CaptureForCancellation(window.WorkItems);
                activeWork.AddRange(captured);
                drainTasks.AddRange(window.WorkItems.Select(item => item.Execution));
                if (window.ClearTask is not null)
                    drainTasks.Add(window.ClearTask);
                if (window.StopTask is not null)
                    drainTasks.Add(window.StopTask);
            }

            workToCancel = [.. activeWork];
            startSignal = CreateSignal();
            _disposeTask = DisposeCoreAsync(drainTasks.ToArray(), startSignal.Task);
            disposeTask = _disposeTask;
        }

        CancelAndRelease(workToCancel);
        startSignal.TrySetResult(true);
        return new ValueTask(disposeTask);
    }

    private async Task RunScheduledAsync(
        Guid windowId,
        WindowState window,
        WorkItem work,
        Func<CancellationToken, Task<RecoveryRecord>> snapshotFactory,
        Task startTask)
    {
        try
        {
            await startTask.ConfigureAwait(false);
            await _delayAsync(_debounceDelay, work.Cancellation.Token).ConfigureAwait(false);
            work.Cancellation.Token.ThrowIfCancellationRequested();

            var record = await snapshotFactory(work.Cancellation.Token).ConfigureAwait(false);
            work.Cancellation.Token.ThrowIfCancellationRequested();
            if (record is null)
                throw new InvalidDataException("The recovery snapshot factory returned no record.");
            if (record.SessionId != _sessionId || record.WindowId != windowId)
                throw new InvalidDataException("The recovery snapshot identity does not match its session and window.");

            await window.SaveGate.WaitAsync(work.Cancellation.Token).ConfigureAwait(false);
            try
            {
                lock (_sync)
                {
                    if (window.Closed
                        || work.Revision != window.Revision
                        || _completionRequested
                        || _disposed)
                        return;
                }
                _store.Save(record);
                ReportSaved(record);
                ReportAvailable();
            }
            finally
            {
                window.SaveGate.Release();
            }
        }
        catch (OperationCanceledException) when (work.Cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Report(ex);
        }
        finally
        {
            lock (_sync)
                window.WorkItems.Remove(work);
            work.Release();
        }
    }

    private StopRequest? PrepareStopLocked(Guid windowId, WindowState window)
    {
        if (window.StopTask is not null)
            return null;

        window.Closed = true;
        _ = checked(++window.Revision);
        var work = CaptureForCancellation(window.WorkItems);
        var drainTasks = window.WorkItems.Select(item => item.Execution).ToList();
        if (window.ClearTask is not null)
            drainTasks.Add(window.ClearTask);
        var startSignal = CreateSignal();
        window.StopTask = DrainAndClearWindowAsync(windowId, drainTasks.ToArray(), startSignal.Task);
        return new StopRequest(work, startSignal);
    }

    private async Task ClearAndReopenWindowAsync(
        Guid windowId,
        WindowState window,
        Task[] drainTasks,
        Task startTask)
    {
        await startTask.ConfigureAwait(false);
        await Task.WhenAll(drainTasks).ConfigureAwait(false);
        try
        {
            _store.ClearWindow(_sessionId, windowId);
            ReportAvailable();
        }
        catch (Exception ex)
        {
            Report(ex);
            throw;
        }
        finally
        {
            lock (_sync)
            {
                window.ClearTask = null;
                if (!window.Closed && !_completionRequested && !_disposed)
                    window.Clearing = false;
            }
        }
    }

    private async Task DrainAndClearWindowAsync(
        Guid windowId,
        Task[] drainTasks,
        Task startTask)
    {
        await startTask.ConfigureAwait(false);
        await Task.WhenAll(drainTasks).ConfigureAwait(false);
        try
        {
            _store.ClearWindow(_sessionId, windowId);
            ReportAvailable();
        }
        catch (Exception ex)
        {
            Report(ex);
            throw;
        }
    }

    private async Task CompleteCoreAsync(Task[] stopTasks, Task startTask)
    {
        await startTask.ConfigureAwait(false);
        await Task.WhenAll(stopTasks).ConfigureAwait(false);
        try
        {
            _store.CompleteSession(_sessionId);
        }
        catch (Exception ex)
        {
            Report(ex);
            throw;
        }
    }

    private static async Task DisposeCoreAsync(Task[] drainTasks, Task startTask)
    {
        await startTask.ConfigureAwait(false);
        await Task.WhenAll(drainTasks).ConfigureAwait(false);
    }

    private void ExecuteStopRequest(StopRequest? request)
    {
        if (request is null)
            return;
        CancelAndRelease(request.WorkItems);
        request.StartSignal.TrySetResult(true);
    }

    private void CancelAndRelease(IEnumerable<WorkItem> items)
    {
        foreach (var item in items)
        {
            try
            {
                item.Cancellation.Cancel();
            }
            catch (Exception ex)
            {
                Report(ex);
            }
            finally
            {
                item.Release();
            }
        }
    }

    private static WorkItem[] CaptureForCancellation(IEnumerable<WorkItem> items)
    {
        var captured = items.ToArray();
        foreach (var item in captured)
            item.Acquire();
        return captured;
    }

    private void Report(Exception exception)
    {
        try
        {
            _reportError(exception);
        }
        catch
        {
        }
    }

    private void ReportSaved(RecoveryRecord record)
    {
        try
        {
            _reportSaved(record);
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private void ReportAvailable()
    {
        try
        {
            _reportAvailable();
        }
        catch (Exception ex)
        {
            Report(ex);
        }
    }

    private void EnsureSchedulingAllowed()
    {
        EnsureStarted();
        ThrowIfDisposed();
        if (_completionRequested)
            throw new InvalidOperationException("Recovery session completion has started.");
    }

    private void EnsureStarted()
    {
        if (!_started)
            throw new InvalidOperationException("The recovery session has not started.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void ValidateId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Recovery identifiers cannot be empty.", parameterName);
    }

    private static TaskCompletionSource<bool> CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class WindowState
    {
        public long Revision { get; set; }
        public bool Closed { get; set; }
        public bool Clearing { get; set; }
        public HashSet<WorkItem> WorkItems { get; } = [];
        public SemaphoreSlim SaveGate { get; } = new(1, 1);
        public Task? ClearTask { get; set; }
        public Task? StopTask { get; set; }
    }

    private sealed class WorkItem(long revision)
    {
        private int _references = 1;

        public long Revision { get; } = revision;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<bool> StartSignal { get; } = CreateSignal();
        public Task Execution { get; set; } = Task.CompletedTask;

        public void Acquire() => Interlocked.Increment(ref _references);

        public void Release()
        {
            if (Interlocked.Decrement(ref _references) == 0)
                Cancellation.Dispose();
        }
    }

    private sealed record StopRequest(
        WorkItem[] WorkItems,
        TaskCompletionSource<bool> StartSignal);
}
