using SkiaSharp;

namespace EzyImageViewer.Rendering;

/// <summary>
/// Crop draft marker: a select-style dashed box with no dim — the user decided (2026-07-22) the
/// image outside the draft must stay untouched while sizing; a translucent/blur veil is a later,
/// separately evaluated step. Dark underlay + light dash keeps the box visible on any content.
/// </summary>
public static class CropOverlayRendering
{
    public static readonly SKColor BorderLight = new(0xFF, 0xFF, 0xFF, 0xE0);

    public static readonly SKColor BorderDark = new(0x00, 0x00, 0x00, 0xA0);

    /// <summary>Same 6/4 dash rhythm as the selection rubber band, so both read as one idiom.</summary>
    private static readonly SKPathEffect DashEffect = SKPathEffect.CreateDash([6f, 4f], 0f);

    /// <summary>Draws only the draft border. Coordinates are output-space;
    /// <paramref name="viewMatrix"/> maps them to the destination surface.</summary>
    public static void Draw(SKCanvas canvas, SKMatrix viewMatrix, SKRect draft)
    {
        ArgumentNullException.ThrowIfNull(canvas);

        using var cropBuilder = new SKPathBuilder();
        cropBuilder.MoveTo(viewMatrix.MapPoint(draft.Left, draft.Top));
        cropBuilder.LineTo(viewMatrix.MapPoint(draft.Right, draft.Top));
        cropBuilder.LineTo(viewMatrix.MapPoint(draft.Right, draft.Bottom));
        cropBuilder.LineTo(viewMatrix.MapPoint(draft.Left, draft.Bottom));
        cropBuilder.Close();
        using var cropPath = cropBuilder.Detach();

        using var dark = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            Color = BorderDark,
        };
        canvas.DrawPath(cropPath, dark);

        using var light = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f,
            Color = BorderLight,
            PathEffect = DashEffect,
        };
        canvas.DrawPath(cropPath, light);
    }
}
