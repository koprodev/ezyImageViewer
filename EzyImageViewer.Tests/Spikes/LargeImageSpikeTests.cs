using System.Diagnostics;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace EzyImageViewer.Tests.Spikes;

/// <summary>
/// M0-B spike 7: large-image protection — decode a reduced-size preview instead of full pixels.
/// JPEG is used because its codec supports native scaled decode (also matches NFR-PERF-002's 24MP JPEG).
/// </summary>
public class LargeImageSpikeTests(ITestOutputHelper output)
{
    [Fact]
    public void ScaledDecode_ProducesBoundedPreviewFasterThanFullDecode()
    {
        const int size = 6000; // 36MP, ~137MB as BGRA
        byte[] encoded;
        using (var bitmap = new SKBitmap(size, size, SKColorType.Bgra8888, SKAlphaType.Opaque))
        {
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(SKColors.SteelBlue);
                using var paint = new SKPaint { Color = SKColors.Orange, IsAntialias = true };
                for (var i = 0; i < 50; i++)
                    canvas.DrawCircle(i * 120, i * 120, 400, paint);
            }
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            encoded = data.ToArray();
        }

        var fullWatch = Stopwatch.StartNew();
        using (var full = SKBitmap.Decode(encoded))
        {
            fullWatch.Stop();
            Assert.Equal(size, full.Width);
        }

        var scaledWatch = Stopwatch.StartNew();
        using var encodedStream = new SKMemoryStream(encoded);
        using var codec = SKCodec.Create(encodedStream);
        Assert.NotNull(codec);
        var dimensions = codec.GetScaledDimensions(1f / 8);
        var info = new SKImageInfo(
            dimensions.Width,
            dimensions.Height,
            SKColorType.Bgra8888,
            SKAlphaType.Premul);
        using var preview = SKBitmap.Decode(codec, info);
        scaledWatch.Stop();

        Assert.NotNull(preview);
        Assert.True(preview.Width <= size / 4, $"preview not reduced: {preview.Width}");

        // Timing is reported, not asserted: wall-clock comparisons are flaky on shared CI agents.
        output.WriteLine($"SPIKE-METRIC source={size}x{size} jpegBytes={encoded.Length}");
        output.WriteLine($"SPIKE-METRIC fullDecodeMs={fullWatch.Elapsed.TotalMilliseconds:0}");
        output.WriteLine($"SPIKE-METRIC scaledDecodeMs={scaledWatch.Elapsed.TotalMilliseconds:0} preview={preview.Width}x{preview.Height}");
    }
}
