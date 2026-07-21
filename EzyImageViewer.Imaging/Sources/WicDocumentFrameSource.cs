using System.Runtime.InteropServices.WindowsRuntime;
using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging.Wic;
using Windows.Graphics.Imaging;

namespace EzyImageViewer.Imaging.Sources;

internal sealed class WicDocumentFrameSource : IDocumentFrameSource
{
    private readonly IEncodedSource _source;
    private readonly WicImageDecoder _decoder;
    private bool _disposed;

    private WicDocumentFrameSource(
        IEncodedSource source,
        WicImageDecoder decoder,
        DocumentSequenceKind kind,
        IReadOnlyList<DocumentFrameInfo> frames)
    {
        _source = source;
        _decoder = decoder;
        Kind = kind;
        Frames = frames;
    }

    public int FrameCount => Frames.Count;
    public DocumentSequenceKind Kind { get; }
    public IReadOnlyList<DocumentFrameInfo> Frames { get; }

    public static async Task<WicDocumentFrameSource?> TryCreateAsync(
        IEncodedSource source,
        WicImageDecoder decoder,
        ImageFormat format,
        InputLimits limits,
        CancellationToken cancellationToken)
    {
        using var stream = source.OpenRead();
        BitmapDecoder container;
        try
        {
            container = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream())
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CorruptImageException("WIC could not inspect the image container.", ex);
        }

        var frameCount = checked((int)container.FrameCount);
        if (frameCount <= 1)
            return null;
        if (frameCount > limits.MaxFrameCount)
            throw new SecurityLimitExceededException(
                $"Container has {frameCount:N0} frames, exceeding the {limits.MaxFrameCount:N0} frame limit.");

        var kind = format is ImageFormat.Gif or ImageFormat.Avif
            ? DocumentSequenceKind.Animation
            : DocumentSequenceKind.Pages;
        var frames = kind == DocumentSequenceKind.Animation
            ? await ReadAnimationFramesAsync(container, frameCount, cancellationToken).ConfigureAwait(false)
            : Enumerable.Repeat(DocumentFrameInfo.Still, frameCount).ToArray();
        return new WicDocumentFrameSource(source, decoder, kind, frames);
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
        return await _decoder.DecodeFrameAsync(stream, (uint)frameIndex, request, cancellationToken)
            .ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _source.Dispose();
    }

    private static async Task<IReadOnlyList<DocumentFrameInfo>> ReadAnimationFramesAsync(
        BitmapDecoder decoder,
        int frameCount,
        CancellationToken cancellationToken)
    {
        var result = new DocumentFrameInfo[frameCount];
        for (var index = 0; index < frameCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var frame = await decoder.GetFrameAsync((uint)index).AsTask(cancellationToken).ConfigureAwait(false);
            result[index] = new DocumentFrameInfo(await ReadGifDelayAsync(frame, cancellationToken).ConfigureAwait(false));
        }
        return result;
    }

    private static async Task<TimeSpan> ReadGifDelayAsync(
        BitmapFrame frame,
        CancellationToken cancellationToken)
    {
        try
        {
            var properties = await frame.BitmapProperties
                .GetPropertiesAsync(["/grctlext/Delay"])
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
            if (properties.TryGetValue("/grctlext/Delay", out var delayProperty)
                && delayProperty.Value is not null)
            {
                var hundredths = Convert.ToUInt32(delayProperty.Value, System.Globalization.CultureInfo.InvariantCulture);
                return TimeSpan.FromMilliseconds(Math.Max(10, hundredths * 10));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Missing or malformed optional timing metadata falls back to a stable visible delay.
        }
        return TimeSpan.FromMilliseconds(100);
    }
}
