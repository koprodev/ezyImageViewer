using SkiaSharp;

namespace EzyImageViewer.Imaging;

public enum WhiteboardStyle
{
    White,
    Black,
}

/// <summary>
/// 빈 화이트보드를 PNG 바이트로 렌더.
/// 다른 메모리 원본과 같은 방어형 해석 경로(상한·판별·예산)로 앱에 넣음.
/// </summary>
public static class WhiteboardFactory
{
    // 4K 캔버스 고정(2026-07-22 결정). 표시 비용 66MB로 384MB 예산 안쪽.
    public const int Width = 3840;
    public const int Height = 2160;
    /// <summary>픽셀 단위 격자 간격. 설계상 격자를 픽셀에 구워 넣음.</summary>
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
        // 0.5 오프셋으로 1px 선을 픽셀 행·열 중심에 맞춰 두껍거나 사라지는 일을 방지.
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
