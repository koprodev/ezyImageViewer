using System.Diagnostics;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace EzyImageViewer.Tests.Spikes;

/// <summary>
/// M0-B 스파이크 7: 대형 이미지 보호. 전체 픽셀 대신 축소 미리보기 해석.
/// 코덱이 네이티브 축소 해석을 지원하고 NFR-PERF-002 24MP와도 맞아 JPEG 사용.
/// </summary>
public class LargeImageSpikeTests(ITestOutputHelper output)
{
    [Fact]
    public void ScaledDecode_ProducesBoundedPreviewFasterThanFullDecode()
    {
        const int size = 6000; // 36MP, BGRA면 약 137MB.
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

        // 시간은 보고만 하고 단정하지 않음. 공유 CI의 벽시계 비교는 변덕쟁이.
        output.WriteLine($"SPIKE-METRIC source={size}x{size} jpegBytes={encoded.Length}");
        output.WriteLine($"SPIKE-METRIC fullDecodeMs={fullWatch.Elapsed.TotalMilliseconds:0}");
        output.WriteLine($"SPIKE-METRIC scaledDecodeMs={scaledWatch.Elapsed.TotalMilliseconds:0} preview={preview.Width}x{preview.Height}");
    }
}
