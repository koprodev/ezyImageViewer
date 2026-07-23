using System.Numerics;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using SkiaSharp;

namespace EzyImageViewer.Rendering;

/// <summary>
/// The one composition path for the edited document: interactive paint, golden tests and M6 export
/// all draw through here, so the preview and the saved result cannot diverge (FR-EDIT-004).
/// Chain: frame px → native (undo the reduced-preview scale) → output (transform pipeline) →
/// destination. Background and annotations share the evaluation's source clip, so pixels a crop
/// removed can never reappear behind a later rotation (ADR-0009).
/// </summary>
public static class DocumentComposite
{
    /// <summary>Row-vector Matrix3x2 → Skia's column-vector form (M11→ScaleX, M21→SkewX, M31→TransX).</summary>
    public static SKMatrix ToSKMatrix(in Matrix3x2 m) =>
        new(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0f, 0f, 1f);

    /// <summary>
    /// Draws the composited document. <paramref name="outputToDestination"/> must be the full map
    /// from output-canvas pixels to device pixels, including the canvas's base total matrix — the
    /// canvas's own CTM is treated as already applied (M1 paint convention).
    /// </summary>
    public static void Render(
        SKCanvas canvas,
        SKImage frame,
        PixelSize nativeSize,
        DocumentState state,
        TransformEvaluation evaluation,
        SKMatrix outputToDestination,
        Guid selectedId = default,
        RasterAssetImageCache? assetCache = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (evaluation.SourceClip.Count < 3)
            return; // a crop kept only transparent margin: nothing survives

        var nativeToDestination = outputToDestination.PreConcat(ToSKMatrix(evaluation.NativeToOutput));

        canvas.Save();
        // The logical canvas bound comes first: preview and export must show exactly the same
        // extent (FR-EDIT-004), so nothing may paint outside the declared OutputSize even on a
        // destination larger than it.
        using (var outputRect = BuildOutputPath(evaluation.OutputSize, outputToDestination))
        {
            canvas.ClipPath(outputRect, SKClipOperation.Intersect, antialias: true);
        }
        using (var clip = BuildClipPath(evaluation.SourceClip, nativeToDestination))
        {
            canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
        }

        // Background: one more hop, frame → native, undoes a reduced-preview decode.
        var frameToNative = SKMatrix.CreateScale(
            nativeSize.Width / (float)frame.Width, nativeSize.Height / (float)frame.Height);
        canvas.Save();
        // Erased regions punch the BACKGROUND only: annotations above them still draw, and the
        // viewer's checkerboard shows through instead of a cleared hole.
        if (evaluation.ErasedNative.Count > 0)
        {
            using var punched = BuildErasePath(evaluation.ErasedNative, nativeToDestination);
            canvas.ClipPath(punched, SKClipOperation.Difference, antialias: true);
        }
        canvas.SetMatrix(nativeToDestination.PreConcat(frameToNative));
        using (var paint = new SKPaint { IsAntialias = false })
        {
            canvas.DrawImage(frame, 0f, 0f, new SKSamplingOptions(SKFilterMode.Linear), paint);
        }
        canvas.Restore();

        // Same clip is still active: annotations outside the surviving source region do not draw.
        // The frame rides along so protection effects sample the real background pixels.
        AnnotationRendering.DrawAnnotations(
            canvas, state, nativeToDestination, assetCache: assetCache,
            backgroundFrame: frame, frameToNative: frameToNative);
        canvas.Restore();

        // Selection affordances are UI overlays, not document pixels. They must remain visible
        // beyond the source clip so edge resize and rotate handles stay operable.
        if (selectedId != default && state.IsEffectivelyVisible(selectedId)
            && state.Find(selectedId) is { } selected)
            AnnotationRendering.DrawSelection(canvas, selected, nativeToDestination);
    }

    private static SKPath BuildOutputPath(PixelSize outputSize, SKMatrix outputToDestination)
    {
        using var path = new SKPathBuilder();
        path.MoveTo(outputToDestination.MapPoint(0f, 0f));
        path.LineTo(outputToDestination.MapPoint(outputSize.Width, 0f));
        path.LineTo(outputToDestination.MapPoint(outputSize.Width, outputSize.Height));
        path.LineTo(outputToDestination.MapPoint(0f, outputSize.Height));
        path.Close();
        return path.Detach();
    }

    private static SKPath BuildErasePath(
        IReadOnlyList<IReadOnlyList<Vector2>> erasedNative, SKMatrix nativeToDestination)
    {
        using var path = new SKPathBuilder();
        foreach (var quad in erasedNative)
        {
            for (var i = 0; i < quad.Count; i++)
            {
                var point = nativeToDestination.MapPoint(quad[i].X, quad[i].Y);
                if (i == 0)
                    path.MoveTo(point);
                else
                    path.LineTo(point);
            }
            path.Close();
        }
        return path.Detach();
    }

    private static SKPath BuildClipPath(IReadOnlyList<Vector2> sourceClip, SKMatrix nativeToDestination)
    {
        using var path = new SKPathBuilder();
        for (var i = 0; i < sourceClip.Count; i++)
        {
            var point = nativeToDestination.MapPoint(sourceClip[i].X, sourceClip[i].Y);
            if (i == 0)
                path.MoveTo(point);
            else
                path.LineTo(point);
        }
        path.Close();
        return path.Detach();
    }
}
