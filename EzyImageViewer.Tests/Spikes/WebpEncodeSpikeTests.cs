using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Spikes;

/// <summary>
/// ADR-0001 후속 M0-B.
/// 내보내기가 시스템 WebP 코덱에 기대지 않도록 SkiaSharp 내장 인코더를 고정 경로로 사용.
/// </summary>
public class WebpEncodeSpikeTests
{
    [Fact]
    public void SkiaSharp_EncodesAndDecodesWebp()
    {
        using var bitmap = new SKBitmap(32, 32, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(new SKColor(0x11, 0xAA, 0x22, 0xFF));

        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Webp, 90);
        Assert.NotNull(encoded);
        Assert.True(encoded.Size > 0, "empty webp output");

        using var decoded = SKBitmap.Decode(encoded.ToArray());
        Assert.NotNull(decoded);
        Assert.Equal(32, decoded.Width);
        var pixel = decoded.GetPixel(16, 16);
        Assert.True(pixel.Green > 150 && pixel.Red < 60, $"unexpected pixel {pixel}");
    }
}
