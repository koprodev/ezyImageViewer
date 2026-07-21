namespace EzyImageViewer.Core.Imaging;

/// <summary>
/// Platform-neutral catalog of file extensions the viewer opens. Lives in Core so navigation
/// does not depend on the Imaging layer (no upward reference).
/// </summary>
public static class ImageFormatCatalog
{
    /// <summary>M1 raster set (requirements §8.2 first-release formats).</summary>
    public static readonly IReadOnlySet<string> RasterExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".dib", ".rle",
        ".gif", ".tif", ".tiff", ".ico", ".webp",
    };

    /// <summary>Codec-dependent formats (§8.2 conditional tier) — surfaced from M8 with codec checks.</summary>
    public static readonly IReadOnlySet<string> ConditionalExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".heic", ".heif", ".hif",
    };

    /// <summary>Document/design inputs whose product gates are independent from raster codecs.</summary>
    public static readonly IReadOnlySet<string> DocumentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".psd", ".svg", ".svgz",
    };

    public static readonly IReadOnlySet<string> VectorExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".svg", ".svgz",
    };

    /// <summary>Formats enabled in normal product open/navigation surfaces for the current release gate.</summary>
    public static readonly IReadOnlySet<string> ViewableExtensions = new HashSet<string>(
        RasterExtensions.Concat(ConditionalExtensions).Concat(VectorExtensions),
        StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlySet<string> KnownExtensions = new HashSet<string>(
        RasterExtensions.Concat(ConditionalExtensions).Concat(DocumentExtensions),
        StringComparer.OrdinalIgnoreCase);

    public static bool IsRaster(string path) => RasterExtensions.Contains(Path.GetExtension(path));

    public static bool IsViewable(string path) => ViewableExtensions.Contains(Path.GetExtension(path));

    public static bool IsKnown(string path) => KnownExtensions.Contains(Path.GetExtension(path));
}
