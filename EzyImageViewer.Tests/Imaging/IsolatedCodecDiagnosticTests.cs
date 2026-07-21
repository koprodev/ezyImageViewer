using EzyImageViewer.CodecProtocol;
using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging;
using EzyImageViewer.Imaging.Codecs;
using Xunit;

namespace EzyImageViewer.Tests.Imaging;

public sealed class IsolatedCodecDiagnosticTests
{
    [Theory]
    [InlineData(
        "psd-composite-cmyk-to-srgb",
        "PSD_CMYK_TO_SRGB",
        DocumentDiagnosticSeverity.Information)]
    [InlineData(
        "psd-composite-lab-to-srgb",
        "PSD_LAB_TO_SRGB",
        DocumentDiagnosticSeverity.Information)]
    [InlineData(
        "psd-composite-multichannel-spot-to-srgb",
        "PSD_SPOT_TO_SRGB",
        DocumentDiagnosticSeverity.Warning)]
    [InlineData(
        "psd-composite-duotone-spot-to-srgb",
        "PSD_SPOT_TO_SRGB",
        DocumentDiagnosticSeverity.Warning)]
    public async Task PsdSuccessDiagnostic_IsMappedToStableDocumentStatus(
        string hostDiagnostic,
        string expectedCode,
        DocumentDiagnosticSeverity expectedSeverity)
    {
        var decoder = new IsolatedCodecImageDecoder(
            new DiagnosticCodecClient(hostDiagnostic),
            CodecFormat.Psd);
        using var input = new MemoryStream("8BPS"u8.ToArray(), writable: false);

        var result = await decoder.DecodeAsync(
            input,
            DecodeRequest.Default,
            CancellationToken.None);

        using (result.Frame)
        {
            var diagnostic = Assert.Single(Assert.IsAssignableFrom<
                IReadOnlyList<DocumentDiagnostic>>(result.Diagnostics));
            Assert.Equal(expectedCode, diagnostic.Code);
            Assert.Equal(expectedSeverity, diagnostic.Severity);
        }
    }

    [Theory]
    [InlineData(CodecFormat.Psd, "psd-composite")]
    [InlineData(CodecFormat.Psd, "untrusted-success-text")]
    [InlineData(CodecFormat.Pdf, "pdf-page")]
    public async Task NormalOrUnknownSuccessDiagnostic_IsNotSurfaced(
        CodecFormat format,
        string hostDiagnostic)
    {
        var decoder = new IsolatedCodecImageDecoder(
            new DiagnosticCodecClient(hostDiagnostic),
            format);
        using var input = new MemoryStream([1], writable: false);

        var result = await decoder.DecodeAsync(
            input,
            DecodeRequest.Default,
            CancellationToken.None);

        using (result.Frame)
            Assert.Empty(result.Diagnostics ?? []);
    }

    [Fact]
    public async Task DocumentLoader_PublishesDecoderDiagnostics()
    {
        var diagnostic = new DocumentDiagnostic(
            "TEST_COLOR_STATUS",
            DocumentDiagnosticSeverity.Information,
            "Color conversion status.");
        var decoder = new DiagnosticImageDecoder(diagnostic);
        var loader = new DocumentLoader(null, decoder, decoder);
        var pngHeader = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };

        using var document = await loader.LoadMemoryAsync(
            pngHeader,
            DocumentSource.FromClipboard(),
            CancellationToken.None);

        Assert.Equal(diagnostic, Assert.Single(document.DiagnosticEntries));
        Assert.Equal(diagnostic.Message, Assert.Single(document.Diagnostics));
    }

    private sealed class DiagnosticCodecClient(string diagnostic)
        : IIsolatedDocumentCodecClient
    {
        public string RendererVersion => "test";

        public Task<IsolatedCodecDecodedImage> DecodeAsync(
            Stream input,
            CodecFormat format,
            int pageIndex,
            int targetMaxDimension,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new IsolatedCodecDecodedImage(
                Pixels: [0, 0, 0, 255],
                PixelLength: 4,
                Width: 1,
                Height: 1,
                Stride: 4,
                NativeSize: new PixelSize(1, 1),
                PageCount: 1,
                Diagnostic: diagnostic));
        }
    }

    private sealed class DiagnosticImageDecoder(DocumentDiagnostic diagnostic) : IImageDecoder
    {
        public Task<DecodeResult> DecodeAsync(
            Stream stream,
            DecodeRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new DecodeResult(
                new DecodedFrame([0, 0, 0, 255], 1, 1, 4, hasAlpha: false),
                IsReduced: false,
                new PixelSize(1, 1),
                Diagnostics: [diagnostic]));
        }
    }
}
