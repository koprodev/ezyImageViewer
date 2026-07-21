using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging;
using ImageMagick;
using Xunit;

namespace EzyImageViewer.Tests.Imaging;

/// <summary>Minimal per-format fixtures through the real dispatch path (FMT-RASTER coverage floor).</summary>
public class RasterFixtureTests
{
    private static readonly DocumentLoader Loader = new();

    private static async Task<ImageDocument> RoundTrip(MagickFormat format)
    {
        using var magick = new MagickImage(MagickColors.DarkOrange, 16, 8);
        var bytes = magick.ToByteArray(format);
        return await Loader.LoadMemoryAsync(bytes, DocumentSource.FromClipboard(), CancellationToken.None);
    }

    [Theory]
    [InlineData(MagickFormat.Gif, ImageFormat.Gif)]
    [InlineData(MagickFormat.Tiff, ImageFormat.Tiff)]
    [InlineData(MagickFormat.Ico, ImageFormat.Ico)]
    [InlineData(MagickFormat.Bmp, ImageFormat.Bmp)]
    public async Task Loader_DecodesFormat(MagickFormat magickFormat, ImageFormat expected)
    {
        using var document = await RoundTrip(magickFormat);

        Assert.Equal(expected, document.Format);
        Assert.Equal(16, document.Frame.Width);
        Assert.Equal(8, document.Frame.Height);
        // Orange-dominant center pixel in BGRA layout (red high, blue low).
        var pixels = document.Frame.Pixels;
        var center = (4 * document.Frame.Width + 8) * 4;
        Assert.True(pixels[center + 2] > 0xB0 && pixels[center] < 0x60);
    }
}
