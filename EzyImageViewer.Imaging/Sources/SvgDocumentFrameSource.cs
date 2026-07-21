using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging.Svg;

namespace EzyImageViewer.Imaging.Sources;

internal sealed class SvgDocumentFrameSource(
    IEncodedSource source,
    SvgImageDecoder decoder) : IDocumentFrameSource
{
    private bool _disposed;

    public int FrameCount => 1;
    public DocumentSequenceKind Kind => DocumentSequenceKind.ScalableVector;
    public IReadOnlyList<DocumentFrameInfo> Frames { get; } = [DocumentFrameInfo.Still];
    public bool IsScaleDependent => true;

    public async Task<DecodeResult> DecodeFrameAsync(
        int frameIndex,
        DecodeRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frameIndex != 0)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        using var stream = source.OpenRead();
        return await decoder.DecodeAsync(stream, request, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        source.Dispose();
    }
}
