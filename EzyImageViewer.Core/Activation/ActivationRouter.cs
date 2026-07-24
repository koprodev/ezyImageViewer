using System.Threading.Channels;

namespace EzyImageViewer.Core.Activation;

/// <summary>불변 순서 봉투. 라우터는 요청 본문을 손대지 않음.</summary>
public sealed record SequencedActivation(long Sequence, ActivationRequest Request);

/// <summary>
/// 최초 실행·리디렉션·앱 내부 활성화 요청을 차례로 전달.
/// 진입 시 순번 부여와 큐 삽입을 한 덩어리로 처리해 전달 순서 = 순번(FIFO).
/// 처리기는 작업만 시작하고 빠르게 반환해야 함. 직렬화 대상은 로드 완료가 아니라 전달.
/// 처리기 예외는 <see cref="DispatchFailed"/>로 알리고 루프는 계속 감.
/// <see cref="Start"/> 전 요청은 채널에서 얌전히 대기.
/// </summary>
public sealed class ActivationRouter : IAsyncDisposable
{
    private readonly Channel<SequencedActivation> _channel =
        Channel.CreateUnbounded<SequencedActivation>(new UnboundedChannelOptions { SingleReader = true });

    private readonly object _ingressGate = new();
    private long _nextSequence;
    private Task? _pump;

    public event Action<SequencedActivation, Exception>? DispatchFailed;

    public long Post(ActivationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_ingressGate)
        {
            var envelope = new SequencedActivation(_nextSequence, request);
            if (!_channel.Writer.TryWrite(envelope))
                throw new InvalidOperationException("Activation router is completed.");
            return _nextSequence++;
        }
    }

    public void Start(Func<SequencedActivation, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (_pump is not null)
            throw new InvalidOperationException("Activation router is already started.");
        _pump = Task.Run(() => PumpAsync(handler));
    }

    private async Task PumpAsync(Func<SequencedActivation, Task> handler)
    {
        await foreach (var envelope in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await handler(envelope).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                DispatchFailed?.Invoke(envelope, ex);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        if (_pump is not null)
            await _pump.ConfigureAwait(false);
    }
}
