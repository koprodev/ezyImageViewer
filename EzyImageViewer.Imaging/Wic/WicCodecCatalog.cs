using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using Windows.Graphics.Imaging;

namespace EzyImageViewer.Imaging.Wic;

internal interface IWicCodecCatalog
{
    bool TryGetRenderer(ImageFormat format, out DocumentRendererInfo renderer);
}

/// <summary>Runtime view of installed WIC decoders; conditional formats fail closed.</summary>
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
            // Codec enumeration is an availability probe. Any platform failure is unavailable.
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
