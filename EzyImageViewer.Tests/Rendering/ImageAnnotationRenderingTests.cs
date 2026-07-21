using System.Collections.Immutable;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Rendering;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

public class ImageAnnotationRenderingTests
{
    [Fact]
    public async Task WarmedAsset_RendersThroughImageAnnotation()
    {
        using var source = new SKBitmap(new SKImageInfo(4, 4));
        source.Erase(SKColors.Magenta);
        using var encoded = source.Encode(SKEncodedImageFormat.Png, 100);
        var asset = new RasterAsset
        {
            Id = Guid.NewGuid(),
            EncodedBytes = encoded.ToArray().ToImmutableArray(),
            PixelSize = new PixelSize(4, 4),
            Format = "png",
        };
        var annotation = new ImageAnnotation
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            Bounds = new RectF(2, 2, 8, 8),
        };
        var state = DocumentState.Empty.AddAsset(asset).AddAnnotation(annotation);
        using var cache = new RasterAssetImageCache();
        await cache.WarmAsync(asset, CancellationToken.None);
        using var target = new SKBitmap(new SKImageInfo(16, 16));
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);

        AnnotationRendering.DrawAnnotations(
            canvas, state, SKMatrix.Identity, assetCache: cache);

        Assert.NotEqual((byte)0, target.GetPixel(5, 5).Alpha);
        cache.Prune(DocumentState.Empty);
        Assert.Null(cache.Find(asset.Id));
    }
}
