using SkiaSharp;

namespace EzyImageViewer.Rendering;

public enum ExportFormat
{
    Png,
    Jpeg,
    WebP,
}

/// <summary>Per-export knobs. Quality applies to JPEG and lossy WebP; PNG ignores it.</summary>
public sealed record ExportOptions
{
    public static ExportOptions Default { get; } = new();

    private readonly int _quality = 90;

    public int Quality
    {
        get => _quality;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 100);
            _quality = value;
        }
    }

    /// <summary>WebP only (FR-OUT-005).</summary>
    public bool WebPLossless { get; init; }

    /// <summary>JPEG has no alpha: transparency composites over this color (FR-OUT-004).</summary>
    public uint JpegBackgroundArgb { get; init; } = 0xFFFF_FFFF;

    /// <summary>FR-OUT-008 keep option (Q6 = b): carry source EXIF minus the sensitive fields
    /// <see cref="ExportMetadata"/> always strips. False (default) keeps the structural
    /// full strip — privacy minimization is the default, not a choice.</summary>
    public bool KeepMetadata { get; init; }
}

/// <summary>
/// Encodes a flattened raster (FR-OUT-003~005). Re-encoding through Skia carries no source
/// metadata — the FR-OUT-008 default (privacy-minimizing strip) is structural, not a filter.
/// </summary>
public static class ImageExporter
{
    public static byte[] Encode(SKImage image, ExportFormat format, ExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        options ??= ExportOptions.Default;
        return format switch
        {
            ExportFormat.Png => EncodeSimple(image, SKEncodedImageFormat.Png, 100),
            ExportFormat.Jpeg => EncodeJpeg(image, options),
            ExportFormat.WebP => EncodeWebP(image, options),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown export format."),
        };
    }

    public static string ExtensionFor(ExportFormat format) => format switch
    {
        ExportFormat.Png => ".png",
        ExportFormat.Jpeg => ".jpg",
        ExportFormat.WebP => ".webp",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown export format."),
    };

    private static byte[] EncodeSimple(SKImage image, SKEncodedImageFormat format, int quality)
    {
        using var data = image.Encode(format, quality)
            ?? throw new InvalidOperationException($"{format} encoding failed.");
        return data.ToArray();
    }

    private static byte[] EncodeJpeg(SKImage image, ExportOptions options)
    {
        using var opaque = CompositeOver(image, new SKColor(options.JpegBackgroundArgb));
        return EncodeSimple(opaque, SKEncodedImageFormat.Jpeg, options.Quality);
    }

    private static byte[] EncodeWebP(SKImage image, ExportOptions options)
    {
        if (!options.WebPLossless)
            return EncodeSimple(image, SKEncodedImageFormat.Webp, options.Quality);
        // Skia's lossless WebP path goes through the pixmap encoder options.
        using var raster = ToRaster(image);
        using var pixmap = raster.PeekPixels()
            ?? throw new InvalidOperationException("WebP encoding failed: no readable pixels.");
        using var data = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100))
            ?? throw new InvalidOperationException("Lossless WebP encoding failed.");
        return data.ToArray();
    }

    /// <summary>Flattens transparency onto a background: JPEG cannot carry alpha.</summary>
    private static SKImage CompositeOver(SKImage image, SKColor background)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException("Could not allocate the JPEG composite surface.");
        surface.Canvas.Clear(background.WithAlpha(0xFF));
        surface.Canvas.DrawImage(image, 0f, 0f, new SKSamplingOptions(SKFilterMode.Nearest));
        return surface.Snapshot();
    }

    private static SKBitmap ToRaster(SKImage image)
    {
        var bitmap = SKBitmap.FromImage(image)
            ?? throw new InvalidOperationException("Could not read image pixels for encoding.");
        return bitmap;
    }
}
