using EzyImageViewer.Core.Imaging;
using SkiaSharp;

namespace EzyImageViewer.Imaging.Skia;

/// <summary>
/// SKCodec path for formats outside the WIC dispatch set (M1: WebP). Applies the codec's
/// EncodedOrigin so the output honors the same "orientation applied once" contract as WIC.
/// </summary>
public sealed class SkiaImageDecoder : IImageDecoder
{
    public Task<DecodeResult> DecodeAsync(Stream stream, DecodeRequest request, CancellationToken cancellationToken)
        => DecodeFrameAsync(stream, 0, request, cancellationToken);

    internal Task<DecodeResult> DecodeFrameAsync(
        Stream stream,
        int frameIndex,
        DecodeRequest request,
        CancellationToken cancellationToken)
        => Task.Run(() => Decode(stream, frameIndex, request, cancellationToken), cancellationToken);

    private static DecodeResult Decode(Stream stream, int frameIndex, DecodeRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        using var data = SKData.Create(stream)
            ?? throw new CorruptImageException("Could not buffer the image data.");
        using var codec = SKCodec.Create(data)
            ?? throw new CorruptImageException("Skia could not parse the image.");
        ArgumentOutOfRangeException.ThrowIfNegative(frameIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(frameIndex, Math.Max(1, codec.FrameCount));

        var origin = codec.EncodedOrigin;
        var swapsAxes = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var orientedWidth = swapsAxes ? codec.Info.Height : codec.Info.Width;
        var orientedHeight = swapsAxes ? codec.Info.Width : codec.Info.Height;

        var plan = request.Limits.PlanDimensions(orientedWidth, orientedHeight);
        if (plan.Action == DecodeAction.Reject)
            throw new SecurityLimitExceededException(plan.RejectReason!);

        var decodeSize = codec.Info.Size;
        if (plan.Action == DecodeAction.DecodeScaled)
        {
            var scale = (float)plan.TargetMaxDimension / Math.Max(orientedWidth, orientedHeight);
            decodeSize = codec.GetScaledDimensions(scale);
        }

        ct.ThrowIfCancellationRequested();
        var info = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        var bitmap = new SKBitmap(info);
        bitmap.Erase(SKColors.Transparent);
        // The one-argument options use PriorFrame=-1, making Skia reconstruct any RequiredFrame
        // chain instead of treating an animation's partial frame as a complete canvas.
        var frameOptions = new SKCodecOptions(frameIndex);
        var decodeResult = codec.GetPixels(
            info,
            bitmap.GetPixels(),
            bitmap.RowBytes,
            frameOptions);
        if (decodeResult is not SKCodecResult.Success and not SKCodecResult.IncompleteInput)
        {
            bitmap.Dispose();
            throw new CorruptImageException($"Skia failed to decode frame {frameIndex} ({decodeResult}).");
        }

        // ApplyOrigin consumes the input (disposes it when replaced); only the result is owned here.
        using var oriented = ApplyOrigin(bitmap, origin);
        var width = oriented.Width;
        var height = oriented.Height;
        var stride = oriented.RowBytes;
        var buffer = oriented.Bytes; // copy owned by the frame from here on

        var hasAlpha = PixelAnalysis.HasTransparency(buffer, stride, width, height);
        return new DecodeResult(
            new DecodedFrame(buffer, width, height, stride, hasAlpha),
            plan.Action == DecodeAction.DecodeScaled,
            new PixelSize(orientedWidth, orientedHeight));
    }

    /// <summary>
    /// Returns a bitmap with the EXIF origin baked in; consumes the input (disposed if replaced).
    /// Matrices are the EXIF mappings written directly (x' = sx·x + kx·y + tx / y' = ky·x + sy·y + ty)
    /// with w/h = source dimensions.
    /// </summary>
    internal static SKBitmap ApplyOrigin(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        if (origin == SKEncodedOrigin.TopLeft)
            return bitmap;

        float w = bitmap.Width;
        float h = bitmap.Height;
        var swaps = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
            or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

        var target = new SKBitmap(new SKImageInfo(
            swaps ? bitmap.Height : bitmap.Width,
            swaps ? bitmap.Width : bitmap.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target))
        {
            canvas.SetMatrix(origin switch
            {
                SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),      // mirror H
                SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),  // 180°
                SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),    // mirror V
                SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),        // transpose
                SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),      // 90° CW
                SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),  // transpose+180°
                SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),    // 90° CCW
                _ => SKMatrix.Identity,
            });
            canvas.DrawBitmap(bitmap, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest));
        }
        bitmap.Dispose();
        return target;
    }
}
