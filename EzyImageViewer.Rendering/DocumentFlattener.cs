using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using SkiaSharp;

namespace EzyImageViewer.Rendering;

/// <summary>
/// Renders the edited document to a raster at its transform output size through the one composition
/// path (<see cref="DocumentComposite"/>), so an export bakes exactly the effect contract the
/// preview showed (FR-EDIT-004, ADR-0015 §5). Feed the full-resolution frame for a final export;
/// a reduced-preview frame flattens at the same document coordinates but from fewer source pixels.
/// </summary>
public static class DocumentFlattener
{
    /// <summary>
    /// Output-surface byte ceiling (BGRA, 4B/px). This is the byte budget the export path promises
    /// at its allocation point (TransformEvaluator caps sides, not area): the flatten surface plus
    /// the encoder's transient copy peak near twice this, beside the source frame.
    /// </summary>
    public const long MaxOutputBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Checked preflight: the transform's output size, refused before any allocation when
    /// the surface would exceed <see cref="MaxOutputBytes"/>.</summary>
    public static PixelSize PreflightOutputSize(DocumentState state, PixelSize nativeSize)
    {
        ArgumentNullException.ThrowIfNull(state);
        var output = TransformEvaluator.Evaluate(state.Transform, nativeSize).OutputSize;
        ValidateOutputBudget(output);
        return output;
    }

    private static void ValidateOutputBudget(PixelSize output)
    {
        long bytes;
        try
        {
            bytes = checked((long)output.Width * output.Height * 4);
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException(
                $"Export output {output.Width}x{output.Height} overflows the byte budget.");
        }
        if (bytes > MaxOutputBytes)
            throw new InvalidOperationException(
                $"Export output {output.Width}x{output.Height} ({bytes:N0} bytes) exceeds the {MaxOutputBytes:N0} byte budget.");
    }

    public static SKImage Flatten(
        SKImage frame,
        PixelSize nativeSize,
        DocumentState state,
        RasterAssetImageCache? assetCache = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(state);
        var evaluation = TransformEvaluator.Evaluate(state.Transform, nativeSize);
        ValidateOutputBudget(evaluation.OutputSize);
        var info = new SKImageInfo(
            evaluation.OutputSize.Width, evaluation.OutputSize.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException(
                $"Could not allocate a {info.Width}x{info.Height} flatten surface.");
        surface.Canvas.Clear(SKColors.Transparent);
        DocumentComposite.Render(
            surface.Canvas, frame, nativeSize, state, evaluation, SKMatrix.Identity,
            assetCache: assetCache);
        return surface.Snapshot();
    }
}
