using System.IO.Compression;
using System.Text;
using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging;
using EzyImageViewer.Imaging.Svg;
using ImageMagick;
using Xunit;

namespace EzyImageViewer.Tests.Imaging;

public class SvgImageDecoderTests
{
    [Fact]
    public async Task SecureStatic_RendersInternalShapes_AndRecordsRenderer()
    {
        const string source =
            """<svg xmlns="http://www.w3.org/2000/svg" width="20" height="10"><script>throw 1</script><rect width="20" height="10" fill="#2255DD"/></svg>""";

        var loader = new DocumentLoader();
        using var document = await loader.LoadMemoryAsync(
            Encoding.UTF8.GetBytes(source),
            DocumentSource.FromClipboard(),
            CancellationToken.None);

        Assert.Equal(ImageFormat.Svg, document.Format);
        Assert.Equal(20, document.Frame.Width);
        Assert.Equal(10, document.Frame.Height);
        Assert.Equal("Svg.Skia (secure static)", document.Renderer.Name);
        Assert.True(document.Frame.Pixels[0] > 0x80);
    }

    [Fact]
    public async Task SecureStatic_DoesNotReadExternalFileImage()
    {
        var externalPath = Path.Combine(Path.GetTempPath(), $"ezy-svg-external-{Guid.NewGuid():N}.png");
        using (var image = new MagickImage(MagickColors.Lime, 8, 8))
            await image.WriteAsync(externalPath);

        try
        {
            var uri = new Uri(externalPath).AbsoluteUri;
            var source =
                $"""<svg xmlns="http://www.w3.org/2000/svg" width="16" height="8"><rect width="8" height="8" fill="#FF0000"/><image x="8" width="8" height="8" href="{uri}"/></svg>""";
            var decoder = new SvgImageDecoder();
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));
            var result = await decoder.DecodeAsync(stream, DecodeRequest.Default, CancellationToken.None);
            using var frame = result.Frame;

            var left = frame.Pixels.Slice(4 * 4, 4);
            var right = frame.Pixels.Slice(12 * 4, 4);
            Assert.True(left[2] > 0x80);
            Assert.Equal(0, right[3]);
        }
        finally
        {
            File.Delete(externalPath);
        }
    }

    [Fact]
    public async Task Dtd_IsRejectedAsSecurityPolicy()
    {
        const string source =
            """<!DOCTYPE svg [<!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">]><svg xmlns="http://www.w3.org/2000/svg" width="1" height="1"><text>&xxe;</text></svg>""";
        var decoder = new SvgImageDecoder();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));

        var exception = await Assert.ThrowsAsync<SecurityLimitExceededException>(() =>
            decoder.DecodeAsync(stream, DecodeRequest.Default, CancellationToken.None));
        Assert.Equal(ImageLoadFailureKind.ResourceOrSecurityLimitExceeded, exception.Kind);
    }

    [Fact]
    public async Task Loader_RecognizesAndRendersSvgzByExpandedSignature()
    {
        const string source =
            """<svg xmlns="http://www.w3.org/2000/svg" width="7" height="5"><rect width="7" height="5" fill="#00FF00"/></svg>""";
        using var encoded = new MemoryStream();
        using (var gzip = new GZipStream(encoded, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(Encoding.UTF8.GetBytes(source));

        var loader = new DocumentLoader();
        using var document = await loader.LoadMemoryAsync(
            encoded.ToArray(), DocumentSource.FromClipboard(), CancellationToken.None);

        Assert.Equal(ImageFormat.Svg, document.Format);
        Assert.Equal(7, document.Frame.Width);
        Assert.Equal(5, document.Frame.Height);
    }

    [Fact]
    public async Task OversizedViewport_IsClassifiedAsSecurityLimitInsteadOfOverflow()
    {
        const string source =
            """<svg xmlns="http://www.w3.org/2000/svg" width="3000000000" height="10"><rect width="1" height="1"/></svg>""";
        var decoder = new SvgImageDecoder();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(source));

        var exception = await Assert.ThrowsAsync<SecurityLimitExceededException>(() =>
            decoder.DecodeAsync(stream, DecodeRequest.Default, CancellationToken.None));

        Assert.Equal(ImageLoadFailureKind.ResourceOrSecurityLimitExceeded, exception.Kind);
    }
}
