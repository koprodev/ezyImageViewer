namespace EzyImageViewer.Core.Activation;

public enum OpenTarget
{
    ExistingWindow,
    NewWindow,
}

/// <summary>
/// Immutable activation payload. Ordering lives in <see cref="SequencedActivation"/> (assigned at
/// router ingress); <see cref="Timestamp"/> is diagnostic only (clock skew, equal ticks).
/// Derived records make invalid states unrepresentable (no nullable grab-bag).
/// </summary>
public abstract record ActivationRequest
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? CorrelationId { get; init; }
}

public sealed record LaunchActivation : ActivationRequest;

public sealed record FileActivation(
    IReadOnlyList<string> Paths,
    OpenTarget Target = OpenTarget.ExistingWindow,
    bool IsInitial = false)
    : ActivationRequest
{
    public string PrimaryPath => Paths[0];
}

/// <summary>IsInitial = this activation launched the process (cold start), as opposed to a warm
/// redirected activation — consumers must not infer it from their own state.</summary>
public sealed record ProtocolActivation(Uri Uri, bool IsInitial = false) : ActivationRequest;

/// <summary>Owned snapshot bytes captured at ingress; never a live clipboard view or OS handle.</summary>
public sealed record ClipboardImageActivation(ReadOnlyMemory<byte> ImageBytes, string SourceFormat)
    : ActivationRequest;
