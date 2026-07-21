using System.Runtime.InteropServices.WindowsRuntime;
using EzyImageViewer.Core.Imaging;
using Windows.Graphics.Imaging;

namespace EzyImageViewer.Imaging.Wic;

/// <summary>
/// WIC path via Windows.Graphics.Imaging. Output contract: BGRA8 premultiplied, sRGB-managed,
/// EXIF orientation applied once by the decoder (dimensions reported post-orientation).
/// </summary>
public sealed class WicImageDecoder : IImageDecoder
{
    public async Task<DecodeResult> DecodeAsync(Stream stream, DecodeRequest request, CancellationToken cancellationToken)
        => await DecodeFrameAsync(stream, 0, request, cancellationToken).ConfigureAwait(false);

    internal async Task<DecodeResult> DecodeFrameAsync(
        Stream stream,
        uint frameIndex,
        DecodeRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        BitmapDecoder decoder;
        try
        {
            decoder = await BitmapDecoder.CreateAsync(stream.AsRandomAccessStream()).AsTask(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new CorruptImageException($"WIC could not open the image ({ex.Message}).", ex);
        }

        if (frameIndex >= decoder.FrameCount)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));
        var frame = await decoder.GetFrameAsync(frameIndex).AsTask(cancellationToken).ConfigureAwait(false);

        var orientedWidth = checked((int)frame.OrientedPixelWidth);
        var orientedHeight = checked((int)frame.OrientedPixelHeight);
        var plan = request.Limits.PlanDimensions(orientedWidth, orientedHeight);
        if (plan.Action == DecodeAction.Reject)
            throw new SecurityLimitExceededException(plan.RejectReason!);

        // BitmapTransform scales in the unoriented frame; a uniform ratio keeps the oriented
        // output at the planned budget regardless of the 90/270 axis swap.
        var transform = new BitmapTransform();
        if (plan.Action == DecodeAction.DecodeScaled)
        {
            var ratio = (double)plan.TargetMaxDimension / Math.Max(orientedWidth, orientedHeight);
            transform.ScaledWidth = (uint)Math.Max(1, Math.Round(frame.PixelWidth * ratio));
            transform.ScaledHeight = (uint)Math.Max(1, Math.Round(frame.PixelHeight * ratio));
            transform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        using var softwareBitmap = await frame.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.ColorManageToSRgb)
            .AsTask(cancellationToken).ConfigureAwait(false);

        var width = softwareBitmap.PixelWidth;
        var height = softwareBitmap.PixelHeight;
        var stride = checked(width * 4);
        var buffer = new byte[checked((long)stride * height)];
        softwareBitmap.CopyToBuffer(buffer.AsBuffer());

        cancellationToken.ThrowIfCancellationRequested();
        var hasAlpha = PixelAnalysis.HasTransparency(buffer, stride, width, height);
        return new DecodeResult(
            new DecodedFrame(buffer, width, height, stride, hasAlpha),
            plan.Action == DecodeAction.DecodeScaled,
            new PixelSize(orientedWidth, orientedHeight));
    }
}
