using System.Threading.Channels;

namespace EzyImageViewer.Core.Activation;

/// <summary>Immutable ordering envelope; requests themselves are never mutated by the router.</summary>
public sealed record SequencedActivation(long Sequence, ActivationRequest Request);

/// <summary>
/// Serializes activation dispatch across initial launch, redirected activations, and in-app requests.
/// Ordering contract: the sequence number is assigned and enqueued atomically at ingress, so
/// dispatch order always equals sequence order (channel FIFO). Handlers must return quickly
/// (kick off work, e.g. DocumentSession.StartLoad, then yield) — the router serializes dispatch,
/// not load completion. Handler exceptions surface via <see cref="DispatchFailed"/> and never stop
/// the loop. Posting before <see cref="Start"/> buffers in the channel.
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
