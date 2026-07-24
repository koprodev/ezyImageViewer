using EzyImageViewer.Rendering;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

/// <summary>
/// 자르기 초안은 테두리만 있는 점선 상자(2026-07-22 결정).
/// 테두리 밖 픽셀은 어둡게 가리지 않고 원본 유지.
/// 테두리는 어두운 밑선 + 밝은 점선으로 밝고 어두운 그림 모두에서 보임.
/// </summary>
public sealed class CropOverlayRenderingTests
{
    private static SKBitmap RenderOverlay(SKColor background, SKRect draft)
    {
        var bitmap = new SKBitmap(new SKImageInfo(
            40, 40, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(background);
            CropOverlayRendering.Draw(canvas, SKMatrix.Identity, draft);
        }
        return bitmap;
    }

    [Fact]
    public void Draw_AwayFromBorder_LeavesEveryPixelUntouched()
    {
        using var bitmap = RenderOverlay(SKColors.White, SKRect.Create(10f, 10f, 16f, 16f));

        // 초안 밖·안·먼 모서리까지 모두 원본 내용 유지.
        foreach (var (x, y) in new[] { (4, 4), (18, 18), (35, 35), (4, 18), (18, 4) })
            Assert.Equal(SKColors.White, bitmap.GetPixel(x, y));
    }

    [Fact]
    public void Draw_Border_IsVisibleOnLightAndDarkContent()
    {
        using var white = RenderOverlay(SKColors.White, SKRect.Create(10f, 10f, 16f, 16f));
        using var black = RenderOverlay(SKColors.Black, SKRect.Create(10f, 10f, 16f, 16f));

        static bool EdgeDiffers(SKBitmap bitmap, SKColor background)
        {
            for (var x = 10; x <= 26; x++)
                if (bitmap.GetPixel(x, 10) != background)
                    return true;
            return false;
        }

        Assert.True(EdgeDiffers(white, SKColors.White), "border invisible on white content");
        Assert.True(EdgeDiffers(black, SKColors.Black), "border invisible on black content");
    }
}
