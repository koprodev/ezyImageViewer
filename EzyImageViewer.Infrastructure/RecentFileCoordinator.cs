using System.Threading.Channels;

namespace EzyImageViewer.Infrastructure;

public sealed class RecentFileHistoryClearException : IOException
{
    public RecentFileHistoryClearException(Exception innerException)
        : base("The recent-file history could not be cleared.", innerException)
    {
    }
}

/// <summary>
/// Serializes recent-file persistence for the process. Register one instance and share it across windows.
/// </summary>
public sealed class RecentFileCoordinator : IAsyncDisposable
{
    private readonly RecentFileStore _store;
    private readonly Action<Exception> _reportError;
    private readonly Channel<WorkItem> _queue;
    private readonly object _stateLock = new();
    private readonly Task _workerTask;
    private bool _admissionEnabled;
    private bool _desiredEnabled;
    private bool _sessionPaused;
    private bool _disposeStarted;
    private long _transitionVersion;

    public RecentFileCoordinator(
        RecentFileStore store,
        bool initiallyEnabled,
        Action<Exception>? reportError = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _reportError = reportError ?? (_ => { });
        _admissionEnabled = initiallyEnabled;
        _desiredEnabled = initiallyEnabled;
        _queue = Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        if (!initiallyEnabled)
            _queue.Writer.TryWrite(new DisableWorkItem(0, null));
        _workerTask = Task.Run(() => ProcessQueueAsync(initiallyEnabled));
    }

    public bool RecordOpened(string path)
    {
        lock (_stateLock)
        {
            if (_disposeStarted || !_admissionEnabled)
                return false;
            return _queue.Writer.TryWrite(new RecordWorkItem(path));
        }
    }

    /// <summary>Stops recording and hides snapshots for this process without changing the
    /// persisted preference or deleting existing history. Safe mode uses this one-way pause.</summary>
    public void PauseForSession()
    {
        lock (_stateLock)
        {
            if (_disposeStarted)
                throw new ObjectDisposedException(nameof(RecentFileCoordinator));
            _sessionPaused = true;
            _admissionEnabled = false;
        }
    }

    public Task SetEnabledAsync(bool enabled)
    {
        var completion = CreateCompletion();
        lock (_stateLock)
        {
            if (_disposeStarted)
                return Task.FromException(new ObjectDisposedException(nameof(RecentFileCoordinator)));

            var version = ++_transitionVersion;
            _desiredEnabled = enabled;
            if (!enabled)
                _admissionEnabled = false;

            WorkItem workItem = enabled
                ? new EnableWorkItem(version, completion)
                : new DisableWorkItem(version, completion);
            if (!_queue.Writer.TryWrite(workItem))
                return Task.FromException(new ObjectDisposedException(nameof(RecentFileCoordinator)));
        }
        return completion.Task;
    }

    public Task<IReadOnlyList<RecentFileEntry>> SnapshotAsync()
    {
        var completion = new TaskCompletionSource<IReadOnlyList<RecentFileEntry>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_stateLock)
        {
            if (_disposeStarted)
            {
                return Task.FromException<IReadOnlyList<RecentFileEntry>>(
                    new ObjectDisposedException(nameof(RecentFileCoordinator)));
            }
            if (_sessionPaused)
                return Task.FromResult<IReadOnlyList<RecentFileEntry>>([]);
            if (!_queue.Writer.TryWrite(new SnapshotWorkItem(completion)))
            {
                return Task.FromException<IReadOnlyList<RecentFileEntry>>(
                    new ObjectDisposedException(nameof(RecentFileCoordinator)));
            }
        }
        return completion.Task;
    }

    /// <summary>Clears history without changing the persisted enabled preference. Admission closes
    /// before the clear is queued and reopens only after a successful delete.</summary>
    public Task ClearAsync()
    {
        var completion = CreateCompletion();
        lock (_stateLock)
        {
            if (_disposeStarted)
                return Task.FromException(new ObjectDisposedException(nameof(RecentFileCoordinator)));
            var version = ++_transitionVersion;
            _admissionEnabled = false;
            if (!_queue.Writer.TryWrite(new ClearWorkItem(version, completion)))
                return Task.FromException(new ObjectDisposedException(nameof(RecentFileCoordinator)));
        }
        return completion.Task;
    }

    public Task DrainAsync()
    {
        var completion = CreateCompletion();
        lock (_stateLock)
        {
            if (_disposeStarted)
                return Task.FromException(new ObjectDisposedException(nameof(RecentFileCoordinator)));
            if (!_queue.Writer.TryWrite(new DrainWorkItem(completion)))
                return Task.FromException(new ObjectDisposedException(nameof(RecentFileCoordinator)));
        }
        return completion.Task;
    }

    public ValueTask DisposeAsync()
    {
        lock (_stateLock)
        {
            if (!_disposeStarted)
            {
                _disposeStarted = true;
                _admissionEnabled = false;
                _queue.Writer.TryComplete();
            }
            return new ValueTask(_workerTask);
        }
    }

    private async Task ProcessQueueAsync(bool storageEnabled)
    {
        var clearRequired = !storageEnabled;
        await foreach (var workItem in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            switch (workItem)
            {
                case RecordWorkItem record:
                    _ = ExecuteSafely(() => _store.Add(record.Path, enabled: true));
                    break;

                case DisableWorkItem disable:
                    storageEnabled = false;
                    var disableFailure = ExecuteSafely(_store.DisableAndClear);
                    clearRequired = disableFailure is not null;
                    CompleteClearTransition(disable.Completion, disableFailure);
                    break;

                case EnableWorkItem enable:
                    bool isCurrentTransition;
                    lock (_stateLock)
                        isCurrentTransition = _desiredEnabled && _transitionVersion == enable.Version;

                    Exception? enableFailure = null;
                    if (isCurrentTransition && clearRequired)
                    {
                        enableFailure = ExecuteSafely(_store.DisableAndClear);
                        clearRequired = enableFailure is not null;
                    }

                    bool shouldEnable;
                    lock (_stateLock)
                    {
                        shouldEnable = !clearRequired
                            && _desiredEnabled
                            && _transitionVersion == enable.Version;
                        if (shouldEnable && !_sessionPaused && !_disposeStarted)
                            _admissionEnabled = true;
                    }
                    if (shouldEnable)
                        storageEnabled = true;
                    CompleteClearTransition(enable.Completion, enableFailure);
                    break;

                case SnapshotWorkItem snapshot:
                    snapshot.Completion.TrySetResult(LoadSafely(storageEnabled));
                    break;

                case ClearWorkItem clear:
                {
                    var clearFailure = ExecuteSafely(_store.DisableAndClear);
                    var cleared = clearFailure is null;
                    clearRequired = clearFailure is not null;
                    storageEnabled = false;
                    lock (_stateLock)
                    {
                        if (cleared && _desiredEnabled
                            && _transitionVersion == clear.Version
                            && !_sessionPaused
                            && !_disposeStarted)
                        {
                            storageEnabled = true;
                            _admissionEnabled = true;
                        }
                    }
                    CompleteClearTransition(clear.Completion, clearFailure);
                    break;
                }

                case DrainWorkItem drain:
                    drain.Completion.TrySetResult(true);
                    break;
            }
        }
    }

    private IReadOnlyList<RecentFileEntry> LoadSafely(bool enabled)
    {
        try
        {
            return _store.Load(enabled);
        }
        catch (Exception ex)
        {
            ReportSafely(ex);
            return [];
        }
    }

    private Exception? ExecuteSafely(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            ReportSafely(ex);
            return ex;
        }
    }

    private static void CompleteClearTransition(
        TaskCompletionSource<bool>? completion,
        Exception? failure)
    {
        if (completion is null)
            return;
        if (failure is null)
            completion.TrySetResult(true);
        else
            completion.TrySetException(new RecentFileHistoryClearException(failure));
    }

    private void ReportSafely(Exception exception)
    {
        try
        {
            _reportError(exception);
        }
        catch
        {
        }
    }

    private static TaskCompletionSource<bool> CreateCompletion() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    private abstract record WorkItem;
    private sealed record RecordWorkItem(string Path) : WorkItem;
    private sealed record DisableWorkItem(
        long Version,
        TaskCompletionSource<bool>? Completion) : WorkItem;
    private sealed record EnableWorkItem(
        long Version,
        TaskCompletionSource<bool> Completion) : WorkItem;
    private sealed record SnapshotWorkItem(
        TaskCompletionSource<IReadOnlyList<RecentFileEntry>> Completion) : WorkItem;
    private sealed record ClearWorkItem(
        long Version,
        TaskCompletionSource<bool> Completion) : WorkItem;
    private sealed record DrainWorkItem(TaskCompletionSource<bool> Completion) : WorkItem;
}
