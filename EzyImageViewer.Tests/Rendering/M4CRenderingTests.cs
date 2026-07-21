using System.Collections.Immutable;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Rendering;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

public sealed class M4CRenderingTests
{
    [Fact]
    public void Checkerboard_MatchesExactGoldenImage()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(32, 32, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        using var shader = ViewerBackgroundRendering.CreateCheckerShader();
        using var golden = SKBitmap.Decode(Path.Combine(
            AppContext.BaseDirectory, "Fixtures", "Golden", "transparent-checkerboard.png"));

        ViewerBackgroundRendering.Draw(canvas, 32, 32, shader);

        Assert.NotNull(golden);
        Assert.Equal(bitmap.Width, golden.Width);
        Assert.Equal(bitmap.Height, golden.Height);
        for (var y = 0; y < 32; y++)
        for (var x = 0; x < 32; x++)
            Assert.Equal(golden.GetPixel(x, y), bitmap.GetPixel(x, y));
    }

    [Fact]
    public void PixelSampler_ReturnsStraightColorFromComposite()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(2, 1, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.SetPixel(0, 0, new SKColor(200, 40, 20, 128));
        bitmap.SetPixel(1, 0, SKColors.Transparent);
        using var frame = SKImage.FromBitmap(bitmap);
        var native = new PixelSize(2, 1);
        var evaluation = TransformEvaluator.Evaluate(BackgroundTransform.Identity, native);

        var sampled = DocumentPixelSampler.Sample(
            frame, native, DocumentState.Empty, evaluation, 0.2f, 0.2f);

        Assert.NotNull(sampled);
        Assert.InRange(sampled.Value.Red, (byte)198, (byte)202);
        Assert.InRange(sampled.Value.Green, (byte)38, (byte)42);
        Assert.InRange(sampled.Value.Blue, (byte)18, (byte)22);
        Assert.InRange(sampled.Value.Alpha, (byte)126, (byte)130);
        Assert.Null(DocumentPixelSampler.Sample(
            frame, native, DocumentState.Empty, evaluation, 1.2f, 0.2f));
        Assert.Null(DocumentPixelSampler.Sample(
            frame, native, DocumentState.Empty, evaluation, -1f, 0f));
    }

    [Fact]
    public void PixelSampler_IncludesAnnotations()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(4, 4, SKColorType.Bgra8888, SKAlphaType.Opaque));
        bitmap.Erase(SKColors.White);
        using var frame = SKImage.FromBitmap(bitmap);
        var native = new PixelSize(4, 4);
        var evaluation = TransformEvaluator.Evaluate(BackgroundTransform.Identity, native);
        var state = new DocumentState
        {
            Layers =
            [
                new AnnotationLayer
                {
                    Id = AnnotationLayer.InitialLayerId,
                    Annotations =
                    [
                        new RectangleAnnotation
                        {
                            Id = Guid.NewGuid(), Bounds = new RectF(0, 0, 4, 4),
                            Shape = ShapeKind.Rectangle, StrokeArgb = 0xFFFF0000,
                            StrokeWidth = 1, FillArgb = 0xFFFF0000,
                        },
                    ],
                },
            ],
        };

        var sampled = DocumentPixelSampler.Sample(frame, native, state, evaluation, 2f, 2f);

        Assert.Equal(SKColors.Red, sampled);
    }

    [Fact]
    public async Task PixelSampler_IncludesWarmedImageAssets()
    {
        using var source = new SKBitmap(new SKImageInfo(2, 2));
        source.Erase(SKColors.Magenta);
        using var encoded = source.Encode(SKEncodedImageFormat.Png, 100);
        var asset = new RasterAsset
        {
            Id = Guid.NewGuid(),
            EncodedBytes = encoded.ToArray().ToImmutableArray(),
            PixelSize = new PixelSize(2, 2),
            Format = "png",
        };
        var state = DocumentState.Empty
            .AddAsset(asset)
            .AddAnnotation(new ImageAnnotation
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                Bounds = new RectF(0, 0, 4, 4),
            });
        using var cache = new RasterAssetImageCache();
        await cache.WarmAsync(asset, CancellationToken.None);
        using var background = new SKBitmap(new SKImageInfo(4, 4));
        background.Erase(SKColors.White);
        using var frame = SKImage.FromBitmap(background);
        var native = new PixelSize(4, 4);
        var evaluation = TransformEvaluator.Evaluate(BackgroundTransform.Identity, native);

        var sampled = DocumentPixelSampler.Sample(
            frame, native, state, evaluation, 2f, 2f, cache);

        Assert.Equal(SKColors.Magenta, sampled);
    }
}
