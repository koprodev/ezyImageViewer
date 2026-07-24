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
/// 창 하나의 현재 문서 소유. 최신 로드 우선이며 묵은 결과는 게시 없이 해제.
/// 게시·교체·이전 문서 해제는 한 게이트에서 처리하고 실패한 재로드는 기존 문서 유지.
/// </summary>
public sealed class DocumentSession : IDisposable
{
    private readonly object _gate = new();
    private long _generation;
    private CancellationTokenSource? _activeCts;
    private ImageDocument? _current;
    private SessionState _state = SessionState.Idle;
    private Exception? _lastError;

    /// <summary>상태·문서 변경 뒤 게이트 밖에서 발생. 구독자 예외는 서로 격리.</summary>
    public event Action? Changed;

    /// <summary>Changed 구독자에서 빠져나온 예외 진단.</summary>
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

    /// <summary>로드를 시작하고 즉시 반환. 라우터 완료 대기 금지.</summary>
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
            // 게이트 안에서 토큰 확보. 후속 로드가 CTS를 먼저 해제해도 안전.
            token = _activeCts.Token;
            _state = SessionState.Loading;
        }
        RaiseChanged();

        return Task.Run(async () =>
        {
            ImageDocument? document = null;
            Exception? error = null;
            var canceled = false;
            // catch는 로더만 감쌈. 완료 알림 예외를 로드 실패로 오인하지 않게 함.
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
                toDispose = document; // 묵었거나 해제 뒤 결과라 게시 안 함.
            }
            else if (canceled)
            {
                toDispose = document; // 취소 로드도 프레임을 만들었을 수 있어 해제.
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
        // Changed와 같은 구독자 격리. 오류 처리 구독자의 오류는 더 갈 곳 없어 삼킴.
        foreach (var sink in sinks.GetInvocationList())
        {
            try
            {
                ((Action<Exception>)sink)(fault);
            }
            catch
            {
                // 더 보고할 곳 없음.
            }
        }
    }
}
