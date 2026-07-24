namespace EzyImageViewer.Core.Activation;

public enum OpenTarget
{
    ExistingWindow,
    NewWindow,
}

/// <summary>
/// 불변 활성화 데이터. 순서는 라우터 진입 때 붙는 <see cref="SequencedActivation"/>이 담당.
/// <see cref="Timestamp"/>는 시계 오차·동일 틱 진단용일 뿐.
/// 파생 레코드로 잘못된 상태를 애초에 표현하지 못하게 함.
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

/// <summary>IsInitial은 이 활성화가 프로세스를 시작한 콜드 스타트라는 뜻.
/// 소비자가 자기 상태를 보고 짐작하면 틀릴 수 있음.</summary>
public sealed record ProtocolActivation(Uri Uri, bool IsInitial = false) : ActivationRequest;

/// <summary>진입 때 복사해 소유한 스냅숏 바이트. 살아 있는 클립보드 뷰나 OS 핸들이 아님.</summary>
public sealed record ClipboardImageActivation(ReadOnlyMemory<byte> ImageBytes, string SourceFormat)
    : ActivationRequest;
