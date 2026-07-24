using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using Windows.Graphics.Imaging;

namespace EzyImageViewer.Imaging.Wic;

internal interface IWicCodecCatalog
{
    bool TryGetRenderer(ImageFormat format, out DocumentRendererInfo renderer);
}

/// <summary>설치된 WIC 디코더의 런타임 목록. 조건부 형식은 확인 실패 시 닫음.</summary>
internal sealed class WicCodecCatalog : IWicCodecCatalog
{
    private static readonly Lazy<IReadOnlyList<CodecEntry>> Decoders = new(EnumerateDecoders);

    public bool TryGetRenderer(ImageFormat format, out DocumentRendererInfo renderer)
    {
        IReadOnlySet<string> extensions = format switch
        {
            ImageFormat.Avif => AvifExtensions,
            ImageFormat.Heif => HeifExtensions,
            _ => EmptyExtensions,
        };

        var match = Decoders.Value.FirstOrDefault(decoder =>
            decoder.Extensions.Any(extension => extensions.Contains(extension)));
        if (match is null)
        {
            renderer = DocumentRendererInfo.Unknown;
            return false;
        }

        renderer = new DocumentRendererInfo(
            $"Windows Imaging Component ({match.FriendlyName})",
            Environment.OSVersion.Version.ToString());
        return true;
    }

    private static IReadOnlyList<CodecEntry> EnumerateDecoders()
    {
        try
        {
            return BitmapDecoder.GetDecoderInformationEnumerator()
                .Select(codec => new CodecEntry(
                    codec.FriendlyName,
                    codec.FileExtensions.Select(extension => extension.ToLowerInvariant()).ToArray()))
                .ToArray();
        }
        catch
        {
            // 코덱 열거 자체가 가용성 확인. 플랫폼 오류가 나면 미지원으로 처리.
            return [];
        }
    }

    private static readonly IReadOnlySet<string> AvifExtensions =
        new HashSet<string>([".avif", ".avifs"], StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> HeifExtensions =
        new HashSet<string>([".heic", ".heif", ".hif", ".heics", ".heifs"], StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlySet<string> EmptyExtensions = new HashSet<string>();

    private sealed record CodecEntry(string FriendlyName, IReadOnlyList<string> Extensions);
}
