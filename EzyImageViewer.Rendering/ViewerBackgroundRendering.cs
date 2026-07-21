using SkiaSharp;

namespace EzyImageViewer.Rendering;

public static class ViewerBackgroundRendering
{
    public const int CheckerCellSize = 8;
    public static readonly SKColor Light = new(0x3A, 0x3A, 0x3E);
    public static readonly SKColor Dark = new(0x2E, 0x2E, 0x32);
    public static readonly SKColor Outside = new(0x33, 0x33, 0x37);

    public static SKShader CreateCheckerShader()
    {
        using var tile = new SKBitmap(
            CheckerCellSize * 2, CheckerCellSize * 2,
            SKColorType.Bgra8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(tile))
        using (var dark = new SKPaint { Color = Dark })
        {
            canvas.Clear(Light);
            canvas.DrawRect(SKRect.Create(CheckerCellSize, CheckerCellSize), dark);
            canvas.DrawRect(SKRect.Create(
                CheckerCellSize, CheckerCellSize, CheckerCellSize, CheckerCellSize), dark);
        }
        return SKShader.CreateBitmap(
            tile.Copy(), SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
    }

    public static void Draw(SKCanvas canvas, int width, int height, SKShader checkerShader)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(checkerShader);
        canvas.Clear(Outside);
        using var paint = new SKPaint { Shader = checkerShader };
        canvas.DrawRect(SKRect.Create(width, height), paint);
    }
}
