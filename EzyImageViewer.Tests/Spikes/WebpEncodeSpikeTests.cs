using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Spikes;

/// <summary>
/// M0-B follow-up to ADR-0001: exports must not depend on system-installed WebP codecs,
/// so SkiaSharp's built-in WebP encoder is the fixed export path.
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
