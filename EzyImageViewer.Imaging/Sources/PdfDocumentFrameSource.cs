using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Imaging.Sources;

internal sealed class PdfDocumentFrameSource : IDocumentFrameSource
{
    private readonly IEncodedSource _source;
    private readonly IPageImageDecoder _decoder;
    private bool _disposed;

    public PdfDocumentFrameSource(
        IEncodedSource source,
        IPageImageDecoder decoder,
        int pageCount,
        InputLimits limits)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageCount, 1);
        if (pageCount > limits.MaxFrameCount)
        {
            throw new SecurityLimitExceededException(
                $"Document has {pageCount:N0} pages, exceeding the {limits.MaxFrameCount:N0} page limit.");
        }
        _source = source;
        _decoder = decoder;
        Frames = Enumerable.Repeat(DocumentFrameInfo.Still, pageCount).ToArray();
    }

    public int FrameCount => Frames.Count;
    public DocumentSequenceKind Kind => DocumentSequenceKind.Pages;
    public IReadOnlyList<DocumentFrameInfo> Frames { get; }
    public bool IsScaleDependent => true;

    public async Task<DecodeResult> DecodeFrameAsync(
        int frameIndex,
        DecodeRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frameIndex, FrameCount);
        using var stream = _source.OpenRead();
        return await _decoder.DecodePageAsync(stream, frameIndex, request, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _source.Dispose();
    }
}
