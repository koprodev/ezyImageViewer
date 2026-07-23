using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using SkiaSharp;

namespace EzyImageViewer.Rendering;

public sealed class RasterAssetImageCache : IDisposable
{
    private readonly Dictionary<Guid, SKImage> _images = [];

    public async Task WarmAsync(
        RasterAsset asset, DecodedFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(frame);
        var image = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return frame.ToSKImage();
        }, cancellationToken).ConfigureAwait(true);
        Replace(asset.Id, image);
    }

    public async Task WarmAsync(RasterAsset asset, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        var bytes = asset.EncodedBytes.ToArray();
        var image = await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var data = SKData.CreateCopy(bytes);
            return SKImage.FromEncodedData(data)
                ?? throw new InvalidDataException("Raster asset cannot be decoded.");
        }, cancellationToken).ConfigureAwait(true);
        Replace(asset.Id, image);
    }

    /// <summary>Synchronous warm for an image the caller already materialized (UR-009 region
    /// lift); the cache takes ownership of <paramref name="image"/>.</summary>
    public void Warm(RasterAsset asset, SKImage image)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(image);
        Replace(asset.Id, image);
    }

    public SKImage? Find(Guid assetId) =>
        _images.TryGetValue(assetId, out var image) ? image : null;

    public void Prune(DocumentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var live = state.Assets.Select(asset => asset.Id).ToHashSet();
        foreach (var id in _images.Keys.Where(id => !live.Contains(id)).ToArray())
        {
            _images[id].Dispose();
            _images.Remove(id);
        }
    }

    public void Clear()
    {
        foreach (var image in _images.Values)
            image.Dispose();
        _images.Clear();
    }

    public void Dispose() => Clear();

    private void Replace(Guid id, SKImage image)
    {
        if (_images.Remove(id, out var previous))
            previous.Dispose();
        _images.Add(id, image);
    }
}
