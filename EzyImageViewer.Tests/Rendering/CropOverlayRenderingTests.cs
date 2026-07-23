using EzyImageViewer.Rendering;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

/// <summary>
/// The crop draft marker is a border-only dashed box (user decision 2026-07-22): every pixel
/// away from the border keeps its original content — no dim, no veil — while the border itself
/// stays visible on both light and dark content thanks to the dark underlay + light dash.
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

        // Outside the draft, inside the draft, and the far corner: all original content.
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
