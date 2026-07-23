using SkiaSharp;

namespace EzyImageViewer.Imaging;

public enum WhiteboardStyle
{
    White,
    Black,
}

/// <summary>
/// Renders the blank whiteboard document as encoded PNG bytes so it enters the app through the
/// same hardened decode path as every other in-memory source (limits, sniffing, budget).
/// </summary>
public static class WhiteboardFactory
{
    // Fixed 4K canvas (user decision 2026-07-22): 66MB display cost, well inside the 384MB budget.
    public const int Width = 3840;
    public const int Height = 2160;
    /// <summary>Grid pitch in pixels; the grid is baked into the pixels by design.</summary>
    public const int GridCellSize = 32;

    public static byte[] CreatePng(WhiteboardStyle style)
    {
        var background = style == WhiteboardStyle.Black ? SKColors.Black : SKColors.White;
        var gridLine = style == WhiteboardStyle.Black
            ? new SKColor(0x30, 0x30, 0x30)
            : new SKColor(0xE2, 0xE2, 0xE2);

        using var surface = SKSurface.Create(new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(background);
        using var paint = new SKPaint
        {
            Color = gridLine,
            StrokeWidth = 1f,
            IsAntialias = false,
        };
        // 0.5 offset centers each 1px line on the pixel row/column so no line doubles or vanishes.
        for (var x = GridCellSize; x < Width; x += GridCellSize)
            canvas.DrawLine(x + 0.5f, 0f, x + 0.5f, Height, paint);
        for (var y = GridCellSize; y < Height; y += GridCellSize)
            canvas.DrawLine(0f, y + 0.5f, Width, y + 0.5f, paint);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
            ?? throw new InvalidOperationException("Whiteboard PNG encoding failed.");
        return data.ToArray();
    }
}
