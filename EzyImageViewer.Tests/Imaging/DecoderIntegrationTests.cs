using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging;
using EzyImageViewer.Imaging.Wic;
using ImageMagick;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Imaging;

public class DecoderIntegrationTests
{
    private static byte[] MakeJpegWithOrientation(uint width, uint height, ushort orientation)
    {
        using var image = new MagickImage(MagickColors.Teal, width, height);
        image.Orientation = (OrientationType)orientation;
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, orientation);
        image.SetProfile(exif);
        return image.ToByteArray(MagickFormat.Jpeg);
    }

    [Fact]
    public async Task Wic_AppliesExifOrientation_SwappingDimensions()
    {
        // 방향 6(RightTop): 저장 40×20은 표시 20×40이어야 함.
        var jpeg = MakeJpegWithOrientation(40, 20, 6);

        var decoder = new WicImageDecoder();
        using var stream = new MemoryStream(jpeg);
        var result = await decoder.DecodeAsync(stream, DecodeRequest.Default, CancellationToken.None);
        using var frame = result.Frame;

        Assert.Equal(20, frame.Width);
        Assert.Equal(40, frame.Height);
        Assert.False(result.IsReduced);
    }

    [Fact]
    public async Task Wic_DetectsAlphaInPng()
    {
        using var magick = new MagickImage(new MagickColor(255, 0, 0, 128), 10, 10);
        var png = magick.ToByteArray(MagickFormat.Png);

        var decoder = new WicImageDecoder();
        using var stream = new MemoryStream(png);
        var result = await decoder.DecodeAsync(stream, DecodeRequest.Default, CancellationToken.None);
        using var frame = result.Frame;

        Assert.True(frame.HasAlpha);
    }

    [Fact]
    public async Task Wic_ScaledDecode_WhenOverPixelBudget()
    {
        using var magick = new MagickImage(MagickColors.Plum, 200, 100);
        var png = magick.ToByteArray(MagickFormat.Png);

        var limits = new InputLimits { DisplayByteBudget = 2_500 * InputLimits.DisplayBytesPerPixel, HardMaxPixels = 1_000_000 };
        var decoder = new WicImageDecoder();
        using var stream = new MemoryStream(png);
        var result = await decoder.DecodeAsync(stream, new DecodeRequest(limits), CancellationToken.None);
        using var frame = result.Frame;

        Assert.True(result.IsReduced);
        Assert.True(frame.Width < 200, $"expected reduced width, got {frame.Width}");
        Assert.True((long)frame.Width * frame.Height <= 2_500 * 1.1, "scaled frame exceeds budget tolerance");
    }

    [Fact]
    public async Task Loader_RejectsOverHardPixelLimit_BeforeAllocation()
    {
        using var magick = new MagickImage(MagickColors.Gray, 300, 300);
        var png = magick.ToByteArray(MagickFormat.Png);

        var loader = new DocumentLoader(new InputLimits { HardMaxPixels = 10_000, DisplayByteBudget = 10_000 * InputLimits.DisplayBytesPerPixel });
        var exception = await Assert.ThrowsAsync<SecurityLimitExceededException>(() =>
            loader.LoadMemoryAsync(png, DocumentSource.FromClipboard(), CancellationToken.None));
        Assert.Equal(ImageLoadFailureKind.ResourceOrSecurityLimitExceeded, exception.Kind);
    }

    [Fact]
    public async Task Loader_CorruptKnownFormat_IsCorruptImageException()
    {
        // 정상 PNG 시그니처 뒤에 쓰레기 데이터.
        var corrupt = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(corrupt, 0);

        var loader = new DocumentLoader();
        await Assert.ThrowsAsync<CorruptImageException>(() =>
            loader.LoadMemoryAsync(corrupt, DocumentSource.FromClipboard(), CancellationToken.None));
    }

    [Fact]
    public async Task Loader_KnownButUnsupported_IsUnsupportedFormatException()
    {
        var pdf = System.Text.Encoding.ASCII.GetBytes("%PDF-1.7 not really a pdf but sniffs as one");
        var loader = new DocumentLoader();
        await Assert.ThrowsAsync<UnsupportedFormatException>(() =>
            loader.LoadMemoryAsync(pdf, DocumentSource.FromClipboard(), CancellationToken.None));
    }

    [Fact]
    public async Task Loader_ExtensionMismatch_IsRecordedAsDiagnostic()
    {
        using var magick = new MagickImage(MagickColors.Navy, 8, 8);
        var png = magick.ToByteArray(MagickFormat.Png);
        var path = Path.Combine(Path.GetTempPath(), $"ezy-mismatch-{Guid.NewGuid():N}.jpg");
        await File.WriteAllBytesAsync(path, png);
        try
        {
            var loader = new DocumentLoader();
            using var document = await loader.LoadFileAsync(path, CancellationToken.None);

            Assert.Equal(ImageFormat.Png, document.Format);
            Assert.Single(document.Diagnostics);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Loader_WebP_DispatchesToSkiaDecoder()
    {
        using var bitmap = new SKBitmap(12, 12, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(new SKColor(0x10, 0xC0, 0x30));
        using var image = SKImage.FromBitmap(bitmap);
        using var webp = image.Encode(SKEncodedImageFormat.Webp, 100);

        var loader = new DocumentLoader();
        using var document = await loader.LoadMemoryAsync(
            webp.ToArray(), DocumentSource.FromClipboard(), CancellationToken.None);

        Assert.Equal(ImageFormat.WebP, document.Format);
        Assert.Equal(12, document.Frame.Width);
        // BGRA 배치의 초록 우세 픽셀.
        Assert.True(document.Frame.Pixels[1] > 0x80);
    }

    [Fact]
    public async Task Loader_FileSizeOverLimit_IsRejectedWithoutDecoding()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ezy-big-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, new byte[128]);
        try
        {
            var loader = new DocumentLoader(new InputLimits { MaxFileBytes = 64 });
            var exception = await Assert.ThrowsAsync<SecurityLimitExceededException>(() =>
                loader.LoadFileAsync(path, CancellationToken.None));
            Assert.Equal(ImageLoadFailureKind.ResourceOrSecurityLimitExceeded, exception.Kind);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
