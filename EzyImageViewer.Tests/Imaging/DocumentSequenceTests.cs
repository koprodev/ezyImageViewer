using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging;
using ImageMagick;
using Xunit;

namespace EzyImageViewer.Tests.Imaging;

public class DocumentSequenceTests
{
    [Fact]
    public async Task TiffPages_AreLazyAndSwitchSurfaceWithoutReplacingDocument()
    {
        var encoded = CreateSequence(MagickFormat.Tiff, animationDelay: null);
        var loader = new DocumentLoader();
        using var document = await loader.LoadMemoryAsync(
            encoded, DocumentSource.FromClipboard(), CancellationToken.None);

        Assert.Equal(DocumentSequenceKind.Pages, document.SequenceKind);
        Assert.False(document.SupportsScaleDependentRendering);
        Assert.Equal(2, document.FrameCount);
        Assert.Equal(0, document.CurrentFrameIndex);
        AssertRed(document.Frame);

        var changed = await document.LoadFrameAsync(
            1, DecodeRequest.Default, forceRerender: false, CancellationToken.None);

        Assert.True(changed);
        Assert.Equal(1, document.CurrentFrameIndex);
        Assert.Equal(1, document.SurfaceRevision);
        AssertBlue(document.Frame);
    }

    [Fact]
    public async Task GifAnimation_ExposesFrameTimingAndDecodesNextFrame()
    {
        var encoded = CreateSequence(MagickFormat.Gif, animationDelay: 7);
        var loader = new DocumentLoader();
        using var document = await loader.LoadMemoryAsync(
            encoded, DocumentSource.FromClipboard(), CancellationToken.None);

        Assert.Equal(DocumentSequenceKind.Animation, document.SequenceKind);
        Assert.Equal(2, document.FrameCount);
        Assert.All(document.Frames, frame => Assert.True(frame.Duration >= TimeSpan.FromMilliseconds(10)));

        await document.LoadFrameAsync(
            1, DecodeRequest.Default, forceRerender: false, CancellationToken.None);
        AssertBlue(document.Frame);
    }

    [Fact]
    public async Task OptimizedGifAnimation_ReconstructsSubRectOnLogicalCanvas()
    {
        using var images = new MagickImageCollection();
        var red = new MagickImage(MagickColors.Red, 16, 16)
        {
            AnimationDelay = 7,
        };
        var second = new MagickImage(MagickColors.Red, 16, 16)
        {
            AnimationDelay = 7,
        };
        using var bluePatch = new MagickImage(MagickColors.Blue, 8, 8);
        second.Composite(bluePatch, 4, 4, CompositeOperator.Over);
        images.Add(red);
        images.Add(second);
        images.Optimize();
        var encoded = images.ToByteArray(MagickFormat.Gif);

        var loader = new DocumentLoader();
        using var document = await loader.LoadMemoryAsync(
            encoded,
            DocumentSource.FromClipboard(),
            CancellationToken.None);

        Assert.Equal(DocumentSequenceKind.Animation, document.SequenceKind);
        Assert.Contains("SkiaSharp animation", document.Renderer.Name, StringComparison.Ordinal);
        Assert.Equal(new PixelSize(16, 16), document.NativeSize);

        await document.LoadFrameAsync(
            1, DecodeRequest.Default, forceRerender: false, CancellationToken.None);

        Assert.Equal(new PixelSize(16, 16), document.NativeSize);
        Assert.Equal(16, document.Frame.Width);
        Assert.Equal(16, document.Frame.Height);
        AssertPixel(document.Frame, 0, 0, blue: 0, red: 255);
        AssertPixel(document.Frame, 4, 4, blue: 255, red: 0);

        var reducedLoader = new DocumentLoader(new InputLimits
        {
            DisplayByteBudget = 8 * 8 * InputLimits.DisplayBytesPerPixel,
        });
        using var reduced = await reducedLoader.LoadMemoryAsync(
            encoded, DocumentSource.FromClipboard(), CancellationToken.None);
        await reduced.LoadFrameAsync(
            1, DecodeRequest.Default with { Limits = new InputLimits
            {
                DisplayByteBudget = 8 * 8 * InputLimits.DisplayBytesPerPixel,
            } }, forceRerender: false, CancellationToken.None);

        Assert.True(reduced.IsReducedPreview);
        Assert.Equal(new PixelSize(16, 16), reduced.NativeSize);
        Assert.Equal(8, reduced.Frame.Width);
        Assert.Equal(8, reduced.Frame.Height);
        AssertPixel(reduced.Frame, 0, 0, blue: 0, red: 255);
        AssertPixel(reduced.Frame, 2, 2, blue: 255, red: 0);
    }

    [Fact]
    public async Task WebPAnimation_UsesSkiaTimingAndCompositedFrames()
    {
        Assert.Equal(-1, new SkiaSharp.SKCodecOptions(1).PriorFrame);
        var encoded = CreateSequence(MagickFormat.WebP, animationDelay: 9);
        var loader = new DocumentLoader();
        using var document = await loader.LoadMemoryAsync(
            encoded, DocumentSource.FromClipboard(), CancellationToken.None);

        Assert.Equal(ImageFormat.WebP, document.Format);
        Assert.Equal(DocumentSequenceKind.Animation, document.SequenceKind);
        Assert.Equal(2, document.FrameCount);
        Assert.All(document.Frames, frame => Assert.True(frame.Duration >= TimeSpan.FromMilliseconds(10)));

        await document.LoadFrameAsync(
            1, DecodeRequest.Default, forceRerender: false, CancellationToken.None);
        AssertBlue(document.Frame);
    }

    [Fact]
    public async Task AnimationFlatten_KeepsCurrentPixelsAndReleasesSequenceContract()
    {
        var encoded = CreateSequence(MagickFormat.Gif, animationDelay: 7);
        var loader = new DocumentLoader();
        using var document = await loader.LoadMemoryAsync(
            encoded, DocumentSource.FromClipboard(), CancellationToken.None);
        await document.LoadFrameAsync(
            1, DecodeRequest.Default, forceRerender: false, CancellationToken.None);
        AssertBlue(document.Frame);

        var changed = await document.FlattenAnimationToCurrentFrameAsync(CancellationToken.None);

        Assert.True(changed);
        Assert.True(document.WasAnimationFlattened);
        Assert.Equal(DocumentSequenceKind.SingleFrame, document.SequenceKind);
        Assert.Equal(1, document.FrameCount);
        Assert.Equal(0, document.CurrentFrameIndex);
        AssertBlue(document.Frame);
        await Assert.ThrowsAsync<InvalidOperationException>(() => document.LoadFrameAsync(
            0, DecodeRequest.Default, forceRerender: false, CancellationToken.None));
    }

    [Fact]
    public async Task IcoFrames_AreExposedAsPages()
    {
        using var images = new MagickImageCollection();
        images.Add(new MagickImage(MagickColors.Red, 16, 16));
        images.Add(new MagickImage(MagickColors.Blue, 32, 32));
        var loader = new DocumentLoader();
        using var document = await loader.LoadMemoryAsync(
            images.ToByteArray(MagickFormat.Ico),
            DocumentSource.FromClipboard(),
            CancellationToken.None);

        Assert.Equal(DocumentSequenceKind.Pages, document.SequenceKind);
        Assert.Equal(2, document.FrameCount);
        await document.LoadFrameAsync(
            1, DecodeRequest.Default, forceRerender: false, CancellationToken.None);
        Assert.Equal(1, document.CurrentFrameIndex);
        Assert.Equal(1, document.SurfaceRevision);
    }

    [Fact]
    public async Task SvgVector_CanRerenderAtAFullerPixelBudget()
    {
        const string source =
            """<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100"><rect width="100" height="100" fill="#FF0000"/></svg>""";
        var reducedLimits = new InputLimits
        {
            DisplayByteBudget = 2_500 * InputLimits.DisplayBytesPerPixel,
        };
        var loader = new DocumentLoader(reducedLimits);
        using var document = await loader.LoadMemoryAsync(
            System.Text.Encoding.UTF8.GetBytes(source),
            DocumentSource.FromClipboard(),
            CancellationToken.None);

        Assert.Equal(DocumentSequenceKind.ScalableVector, document.SequenceKind);
        Assert.True(document.SupportsScaleDependentRendering);
        Assert.True(document.Frame.Width < 100);
        Assert.True(document.IsReducedPreview);

        await document.LoadFrameAsync(
            0,
            new DecodeRequest(InputLimits.Default, PreferredMaxDimension: 400),
            forceRerender: true,
            CancellationToken.None);

        Assert.Equal(400, document.Frame.Width);
        Assert.Equal(new PixelSize(100, 100), document.NativeSize);
        Assert.False(document.IsReducedPreview);
        Assert.Equal(1, document.SurfaceRevision);
    }

    private static byte[] CreateSequence(MagickFormat format, uint? animationDelay)
    {
        using var images = new MagickImageCollection();
        var red = new MagickImage(MagickColors.Red, 8, 8);
        var blue = new MagickImage(MagickColors.Blue, 8, 8);
        if (animationDelay is { } delay)
        {
            red.AnimationDelay = delay;
            blue.AnimationDelay = delay;
        }
        images.Add(red);
        images.Add(blue);
        return images.ToByteArray(format);
    }

    private static void AssertRed(DecodedFrame frame)
    {
        Assert.True(frame.Pixels[2] > 0xC0);
        Assert.True(frame.Pixels[0] < 0x40);
    }

    private static void AssertBlue(DecodedFrame frame)
    {
        var pixel = frame.Pixels[..4].ToArray();
        Assert.True(pixel[0] > 0xC0, $"Expected blue BGRA pixel, got {Convert.ToHexString(pixel)}.");
        Assert.True(pixel[2] < 0x40, $"Expected blue BGRA pixel, got {Convert.ToHexString(pixel)}.");
    }

    private static void AssertPixel(DecodedFrame frame, int x, int y, byte blue, byte red)
    {
        var offset = checked(y * frame.StrideBytes + x * 4);
        var pixel = frame.Pixels.Slice(offset, 4);
        Assert.InRange(pixel[0], Math.Max(0, blue - 8), Math.Min(255, blue + 8));
        Assert.InRange(pixel[2], Math.Max(0, red - 8), Math.Min(255, red + 8));
        Assert.True(pixel[3] > 0xF0);
    }
}
