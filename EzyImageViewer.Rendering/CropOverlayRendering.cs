using SkiaSharp;

namespace EzyImageViewer.Rendering;

/// <summary>
/// 자르기 초안 표식은 흐림 없는 선택형 점선 상자.
/// 크기 조정 중 바깥 이미지는 그대로 유지하기로 결정(2026-07-22).
/// 어두운 밑선 + 밝은 점선으로 어떤 그림에서도 보이게 함.
/// </summary>
public static class CropOverlayRendering
{
    public static readonly SKColor BorderLight = new(0xFF, 0xFF, 0xFF, 0xE0);

    public static readonly SKColor BorderDark = new(0x00, 0x00, 0x00, 0xA0);

    /// <summary>선택 고무줄과 같은 6/4 점선 박자. 한 식구처럼 보임.</summary>
    private static readonly SKPathEffect DashEffect = SKPathEffect.CreateDash([6f, 4f], 0f);

    /// <summary>초안 테두리만 그림. 출력 공간 좌표를 <paramref name="viewMatrix"/>로 대상 표면에 매핑.</summary>
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
