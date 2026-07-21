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

/// <summary>Lazy source for pages, animation frames, and scale-dependent vector renders.</summary>
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
