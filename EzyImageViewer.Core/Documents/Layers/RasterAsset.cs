using System.Collections.Immutable;
using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Core.Documents.Layers;

/// <summary>문서가 한 번 소유하고 이미지 주석이 참조하는 불변 인코딩 래스터.</summary>
public sealed record RasterAsset
{
    public required Guid Id { get; init; }
    public required ImmutableArray<byte> EncodedBytes { get; init; }
    public required PixelSize PixelSize { get; init; }
    public required string Format { get; init; }

    public long EstimatedRetainedBytes =>
        checked(64L + EncodedBytes.Length + ((long)Format.Length * sizeof(char)));
}
