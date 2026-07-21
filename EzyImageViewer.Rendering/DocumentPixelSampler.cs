using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using SkiaSharp;

namespace EzyImageViewer.Rendering;

public static class DocumentPixelSampler
{
    public static SKColor? Sample(
        SKImage frame,
        PixelSize nativeSize,
        DocumentState state,
        TransformEvaluation evaluation,
        float outputX,
        float outputY,
        RasterAssetImageCache? assetCache = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(evaluation);
        if (!float.IsFinite(outputX) || !float.IsFinite(outputY)
            || outputX < 0f || outputY < 0f
            || outputX >= evaluation.OutputSize.Width
            || outputY >= evaluation.OutputSize.Height)
            return null;

        using var surface = SKSurface.Create(new SKImageInfo(
            1, 1, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        var outputToSample = SKMatrix.CreateTranslation(
            -MathF.Floor(outputX), -MathF.Floor(outputY));
        DocumentComposite.Render(
            surface.Canvas, frame, nativeSize, state, evaluation,
            outputToSample, assetCache: assetCache);
        using var image = surface.Snapshot();
        using var bitmap = SKBitmap.FromImage(image);
        var color = bitmap.GetPixel(0, 0);
        return color.Alpha == 0 ? null : color;
    }
}
