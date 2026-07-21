using System.Collections.Immutable;
using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Core.Documents.Layers;

/// <summary>Immutable encoded raster owned once by a document and referenced by image annotations.</summary>
public sealed record RasterAsset
{
    public required Guid Id { get; init; }
    public required ImmutableArray<byte> EncodedBytes { get; init; }
    public required PixelSize PixelSize { get; init; }
    public required string Format { get; init; }

    public long EstimatedRetainedBytes =>
        checked(64L + EncodedBytes.Length + ((long)Format.Length * sizeof(char)));
}
