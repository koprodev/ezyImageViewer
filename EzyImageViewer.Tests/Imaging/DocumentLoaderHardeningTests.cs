using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging;
using ImageMagick;
using Xunit;

namespace EzyImageViewer.Tests.Imaging;

public class DocumentLoaderHardeningTests
{
    [Fact]
    public void DecodedFrame_PreservesLogicalLengthForOversizedOwnedBuffer()
    {
        var backing = new byte[64];
        using var frame = new DecodedFrame(
            backing,
            pixelLength: 16,
            width: 2,
            height: 2,
            strideBytes: 8,
            hasAlpha: false);

        Assert.Equal(16, frame.Pixels.Length);
        Assert.Same(backing, frame.DangerousGetBuffer());
        Assert.Throws<ArgumentException>(() => new DecodedFrame(
            backing,
            pixelLength: 20,
            width: 2,
            height: 2,
            strideBytes: 8,
            hasAlpha: false));
    }

    private sealed class OomOnceDecoder : IImageDecoder
    {
        public int Calls { get; private set; }
        public long? SecondCallBudget { get; private set; }

        public Task<DecodeResult> DecodeAsync(Stream stream, DecodeRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1)
                throw new OutOfMemoryException("simulated");
            SecondCallBudget = request.Limits.FullDecodePixelBudget;
            var frame = new DecodedFrame(new byte[16], 2, 2, 8, hasAlpha: false);
            return Task.FromResult(new DecodeResult(frame, IsReduced: false, new PixelSize(2, 2)));
        }
    }

    [Fact]
    public async Task MemoryLoad_EnforcesByteLimit()
    {
        var loader = new DocumentLoader(new InputLimits { MaxFileBytes = 32 });
        var oversized = new byte[64];

        var exception = await Assert.ThrowsAsync<SecurityLimitExceededException>(() =>
            loader.LoadMemoryAsync(oversized, DocumentSource.FromClipboard(), CancellationToken.None));
        Assert.Equal(ImageLoadFailureKind.ResourceOrSecurityLimitExceeded, exception.Kind);
    }

    [Fact]
    public async Task OutOfMemory_RetriesOnceWithReducedBudget_AndFlagsPreview()
    {
        var decoder = new OomOnceDecoder();
        var limits = new InputLimits { DisplayByteBudget = 48_000_000L * InputLimits.DisplayBytesPerPixel };
        var loader = new DocumentLoader(limits, wic: decoder, skia: decoder);
        // 판별기가 가짜 "WIC" 디코더로 보내도록 정상 PNG 헤더 사용.
        using var magick = new MagickImage(MagickColors.Red, 4, 4);
        var png = magick.ToByteArray(MagickFormat.Png);

        using var document = await loader.LoadMemoryAsync(png, DocumentSource.FromClipboard(), CancellationToken.None);

        Assert.Equal(2, decoder.Calls);
        Assert.Equal(48_000_000 / 4, decoder.SecondCallBudget);
        Assert.True(document.IsReducedPreview);
        Assert.Contains(document.Diagnostics, d => d.Contains("Low memory"));
        Assert.Contains(document.DiagnosticEntries, d => d.Code == "REDUCED_PREVIEW_LOW_MEMORY");
        Assert.Equal(nameof(OomOnceDecoder), document.Renderer.Name);
    }

    [Fact]
    public async Task OutOfMemory_Twice_Propagates()
    {
        var loader = new DocumentLoader(null, wic: new AlwaysOomDecoder(), skia: new AlwaysOomDecoder());
        using var magick = new MagickImage(MagickColors.Red, 4, 4);
        var png = magick.ToByteArray(MagickFormat.Png);

        await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            loader.LoadMemoryAsync(png, DocumentSource.FromClipboard(), CancellationToken.None));
    }

    private sealed class AlwaysOomDecoder : IImageDecoder
    {
        public Task<DecodeResult> DecodeAsync(Stream stream, DecodeRequest request, CancellationToken cancellationToken)
            => throw new OutOfMemoryException("simulated");
    }

    private sealed class UnavailableWicCodecCatalog : EzyImageViewer.Imaging.Wic.IWicCodecCatalog
    {
        public bool TryGetRenderer(ImageFormat format, out DocumentRendererInfo renderer)
        {
            renderer = DocumentRendererInfo.Unknown;
            return false;
        }
    }

    private sealed class AvailableWicCodecCatalog : EzyImageViewer.Imaging.Wic.IWicCodecCatalog
    {
        public bool TryGetRenderer(ImageFormat format, out DocumentRendererInfo renderer)
        {
            renderer = new DocumentRendererInfo("Test WIC codec", "1.2.3");
            return true;
        }
    }

    private sealed class SolidDecoder : IImageDecoder
    {
        public Task<DecodeResult> DecodeAsync(
            Stream stream,
            DecodeRequest request,
            CancellationToken cancellationToken)
        {
            var frame = new DecodedFrame(new byte[16], 2, 2, 8, hasAlpha: true);
            return Task.FromResult(new DecodeResult(frame, false, new PixelSize(2, 2)));
        }
    }

    [Fact]
    public async Task ConditionalFormat_ReportsMissingCodecCategoryUntilCodecGateRuns()
    {
        var avifHeader = new byte[24];
        avifHeader[3] = 24;
        "ftyp"u8.CopyTo(avifHeader.AsSpan(4));
        "avif"u8.CopyTo(avifHeader.AsSpan(8));

        var decoder = new AlwaysOomDecoder();
        var loader = new DocumentLoader(
            null,
            decoder,
            decoder,
            decoder,
            new UnavailableWicCodecCatalog());
        var exception = await Assert.ThrowsAsync<CodecUnavailableException>(() =>
            loader.LoadMemoryAsync(avifHeader, DocumentSource.FromClipboard(), CancellationToken.None));

        Assert.Equal(ImageLoadFailureKind.SystemCodecUnavailable, exception.Kind);
    }

    [Fact]
    public async Task ConditionalFormat_DispatchesOnlyAfterCodecCatalogAcceptance()
    {
        var avifHeader = new byte[24];
        avifHeader[3] = 24;
        "ftyp"u8.CopyTo(avifHeader.AsSpan(4));
        "avif"u8.CopyTo(avifHeader.AsSpan(8));
        var decoder = new SolidDecoder();
        var loader = new DocumentLoader(
            null,
            decoder,
            decoder,
            decoder,
            new AvailableWicCodecCatalog());

        using var document = await loader.LoadMemoryAsync(
            avifHeader, DocumentSource.FromClipboard(), CancellationToken.None);

        Assert.Equal(ImageFormat.Avif, document.Format);
        Assert.Equal("Test WIC codec", document.Renderer.Name);
    }

    [Fact]
    public void RejectionTypes_MapToRequirementsCategories()
    {
        ImageRejectedException[] exceptions =
        [
            new CorruptImageException("test"),
            new ProtectedDocumentException("test"),
            new UnsupportedFormatException("test"),
            new CodecUnavailableException("test"),
            new SecurityLimitExceededException("test"),
        ];
        ImageLoadFailureKind[] expected =
        [
            ImageLoadFailureKind.CorruptFile,
            ImageLoadFailureKind.CredentialsOrPermissionRequired,
            ImageLoadFailureKind.UnsupportedFeature,
            ImageLoadFailureKind.SystemCodecUnavailable,
            ImageLoadFailureKind.ResourceOrSecurityLimitExceeded,
        ];

        Assert.Equal(expected, exceptions.Select(exception => exception.Kind));
    }
}
