namespace EzyImageViewer.Core.Documents;

public enum SessionState
{
    Idle,
    Loading,
    Ready,
    Failed,
    Disposed,
}

/// <summary>
/// Owns the current document of one window. Concurrency contract:
/// latest-wins (a new load cancels the active one); cancel/supersede never publishes Failed;
/// stale results are disposed without publishing; publish + document swap + old-document dispose
/// happen atomically under one gate; Dispose bumps the generation first, permanently blocking
/// any in-flight publish. A failed reload keeps the existing Ready document (LastError is set).
/// </summary>
public sealed class DocumentSession : IDisposable
{
    private readonly object _gate = new();
    private long _generation;
    private CancellationTokenSource? _activeCts;
    private ImageDocument? _current;
    private SessionState _state = SessionState.Idle;
    private Exception? _lastError;

    /// <summary>
    /// Raised outside the gate after any observable state/document change. Subscribers are isolated:
    /// one that throws neither aborts the load nor stops the remaining subscribers — the exception
    /// surfaces on <see cref="SubscriberFaulted"/> instead.
    /// </summary>
    public event Action? Changed;

    /// <summary>Diagnostics for exceptions escaping <see cref="Changed"/> subscribers.</summary>
    public event Action<Exception>? SubscriberFaulted;

    public SessionState State
    {
        get { lock (_gate) return _state; }
    }

    public ImageDocument? Current
    {
        get { lock (_gate) return _current; }
    }

    public Exception? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    /// <summary>Kicks off a load and returns immediately (router handlers must not block on completion).</summary>
    public Task StartLoadAsync(Func<CancellationToken, Task<ImageDocument>> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);

        long generation;
        CancellationToken token;
        lock (_gate)
        {
            if (_state == SessionState.Disposed)
                return Task.CompletedTask;
            generation = ++_generation;
            _activeCts?.Cancel();
            _activeCts?.Dispose();
            _activeCts = new CancellationTokenSource();
            // Captured under the gate: a successor may dispose the CTS before this load's
            // worker body runs, and a disposed CTS must never be touched afterwards.
            token = _activeCts.Token;
            _state = SessionState.Loading;
        }
        RaiseChanged();

        return Task.Run(async () =>
        {
            ImageDocument? document = null;
            Exception? error = null;
            var canceled = false;
            // The catch scope covers the loader only: a Complete inside it would re-enter on its own
            // notification exception and publish a successful load as failed.
            try
            {
                document = await loader(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                canceled = true;
            }
            catch (Exception ex)
            {
                error = ex;
            }
            Complete(generation, document, error, canceled);
        });
    }

    private void Complete(long generation, ImageDocument? document, Exception? error, bool canceled)
    {
        ImageDocument? toDispose = null;
        var publish = false;
        lock (_gate)
        {
            if (_state == SessionState.Disposed || generation != _generation)
            {
                toDispose = document; // stale or post-dispose result: never published
            }
            else if (canceled)
            {
                toDispose = document; // a canceled load may still have produced a frame
                _state = _current is null ? SessionState.Idle : SessionState.Ready;
                publish = true;
            }
            else if (error is not null)
            {
                _lastError = error;
                _state = _current is null ? SessionState.Failed : SessionState.Ready;
                publish = true;
            }
            else if (document is not null)
            {
                toDispose = _current;
                _current = document;
                _lastError = null;
                _state = SessionState.Ready;
                publish = true;
            }
        }
        toDispose?.Dispose();
        if (publish)
            RaiseChanged();
    }

    public void Dispose()
    {
        ImageDocument? document;
        lock (_gate)
        {
            if (_state == SessionState.Disposed)
                return;
            _generation++;
            _activeCts?.Cancel();
            _activeCts?.Dispose();
            _activeCts = null;
            document = _current;
            _current = null;
            _state = SessionState.Disposed;
        }
        document?.Dispose();
    }

    private void RaiseChanged()
    {
        if (Changed is not { } handlers)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try
            {
                ((Action)handler)();
            }
            catch (Exception ex)
            {
                ReportSubscriberFault(ex);
            }
        }
    }

    private void ReportSubscriberFault(Exception fault)
    {
        if (SubscriberFaulted is not { } sinks)
            return;
        // Same isolation the Changed fan-out gets: one faulting sink must not suppress the others,
        // and a fault in a fault sink has nowhere left to go but swallowed.
        foreach (var sink in sinks.GetInvocationList())
        {
            try
            {
                ((Action<Exception>)sink)(fault);
            }
            catch
            {
                // Nothing left to report to.
            }
        }
    }
}
