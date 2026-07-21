using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging.Skia;
using SkiaSharp;

namespace EzyImageViewer.Imaging.Sources;

internal sealed class SkiaDocumentFrameSource : IDocumentFrameSource
{
    private readonly IEncodedSource _source;
    private readonly SkiaImageDecoder _decoder;
    private bool _disposed;

    private SkiaDocumentFrameSource(
        IEncodedSource source,
        SkiaImageDecoder decoder,
        IReadOnlyList<DocumentFrameInfo> frames)
    {
        _source = source;
        _decoder = decoder;
        Frames = frames;
    }

    public int FrameCount => Frames.Count;
    public DocumentSequenceKind Kind => DocumentSequenceKind.Animation;
    public IReadOnlyList<DocumentFrameInfo> Frames { get; }

    public static SkiaDocumentFrameSource? TryCreate(
        IEncodedSource source,
        SkiaImageDecoder decoder,
        InputLimits limits)
    {
        using var stream = source.OpenRead();
        using var data = SKData.Create(stream)
            ?? throw new CorruptImageException("Could not buffer the image container.");
        using var codec = SKCodec.Create(data)
            ?? throw new CorruptImageException("Skia could not inspect the image container.");
        var frameCount = Math.Max(1, codec.FrameCount);
        if (frameCount <= 1)
            return null;
        if (frameCount > limits.MaxFrameCount)
            throw new SecurityLimitExceededException(
                $"Container has {frameCount:N0} frames, exceeding the {limits.MaxFrameCount:N0} frame limit.");

        var metadata = codec.FrameInfo;
        var frames = new DocumentFrameInfo[frameCount];
        for (var index = 0; index < frameCount; index++)
        {
            var duration = index < metadata.Length ? metadata[index].Duration : 100;
            frames[index] = new DocumentFrameInfo(TimeSpan.FromMilliseconds(Math.Max(10, duration)));
        }
        return new SkiaDocumentFrameSource(source, decoder, frames);
    }

    public async Task<DecodeResult> DecodeFrameAsync(
        int frameIndex,
        DecodeRequest request,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frameIndex, FrameCount);
        using var stream = _source.OpenRead();
        return await _decoder.DecodeFrameAsync(stream, frameIndex, request, cancellationToken)
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
