using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Core.Documents;

public enum DocumentSequenceKind
{
    SingleFrame,
    Pages,
    Animation,
    ScalableVector,
}

public readonly record struct DocumentFrameInfo(TimeSpan Duration)
{
    public static DocumentFrameInfo Still { get; } = new(TimeSpan.Zero);
}

/// <summary>페이지·애니메이션 프레임·배율별 벡터 렌더를 늦게 공급하는 원본.</summary>
public interface IDocumentFrameSource : IDisposable
{
    int FrameCount { get; }
    DocumentSequenceKind Kind { get; }
    IReadOnlyList<DocumentFrameInfo> Frames { get; }
    bool IsScaleDependent => false;

    Task<DecodeResult> DecodeFrameAsync(
        int frameIndex,
        DecodeRequest request,
        CancellationToken cancellationToken);
}
