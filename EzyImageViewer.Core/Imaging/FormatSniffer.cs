using System.Text;

namespace EzyImageViewer.Core.Imaging;

public enum ImageFormat
{
    Unknown,
    Png,
    Jpeg,
    Gif,
    Bmp,
    Tiff,
    WebP,
    Ico,
    Pdf,
    Psd,
    Svg,
    Avif,
    Heif,
}

public enum SniffStatus
{
    Supported,
    Conditional,
    KnownButUnsupported,
    Unknown,
    CorruptOrTruncated,
}

public readonly record struct SniffResult(SniffStatus Status, ImageFormat Format);

/// <summary>
/// Signature-first format detection (requirements §8.5: real bytes win over extension).
/// Decoder dispatch must key off this result — a failed decode is a failure of that format,
/// never a cue to reinterpret the file with another decoder.
/// </summary>
public static class FormatSniffer
{
    private const int MinBytesForJudgement = 12;

    private static readonly HashSet<ImageFormat> SupportedFormats =
    [
        ImageFormat.Png, ImageFormat.Jpeg, ImageFormat.Gif, ImageFormat.Bmp,
        ImageFormat.Tiff, ImageFormat.WebP, ImageFormat.Ico, ImageFormat.Svg,
    ];

    private static readonly HashSet<ImageFormat> ConditionalRaster =
    [
        ImageFormat.Avif, ImageFormat.Heif,
    ];

    public static SniffResult Sniff(ReadOnlySpan<byte> header)
    {
        var format = Detect(header);
        if (format != ImageFormat.Unknown)
        {
            var status = SupportedFormats.Contains(format)
                ? SniffStatus.Supported
                : ConditionalRaster.Contains(format)
                    ? SniffStatus.Conditional
                    : SniffStatus.KnownButUnsupported;
            return new SniffResult(status, format);
        }

        return header.Length < MinBytesForJudgement
            ? new SniffResult(SniffStatus.CorruptOrTruncated, ImageFormat.Unknown)
            : new SniffResult(SniffStatus.Unknown, ImageFormat.Unknown);
    }

    private static ImageFormat Detect(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 8 && h[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return ImageFormat.Png;
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF)
            return ImageFormat.Jpeg;
        if (h.Length >= 6 && (h[..6].SequenceEqual("GIF87a"u8) || h[..6].SequenceEqual("GIF89a"u8)))
            return ImageFormat.Gif;
        if (h.Length >= 2 && h[0] == (byte)'B' && h[1] == (byte)'M')
            return ImageFormat.Bmp;
        if (h.Length >= 4 && (h[..4].SequenceEqual((ReadOnlySpan<byte>)[0x49, 0x49, 0x2A, 0x00])
                           || h[..4].SequenceEqual((ReadOnlySpan<byte>)[0x4D, 0x4D, 0x00, 0x2A])))
            return ImageFormat.Tiff;
        if (h.Length >= 12 && h[..4].SequenceEqual("RIFF"u8) && h[8..12].SequenceEqual("WEBP"u8))
            return ImageFormat.WebP;
        if (h.Length >= 4 && h[..4].SequenceEqual((ReadOnlySpan<byte>)[0x00, 0x00, 0x01, 0x00]))
            return ImageFormat.Ico;
        if (h.Length >= 5 && h[..5].SequenceEqual("%PDF-"u8))
            return ImageFormat.Pdf;
        if (h.Length >= 4 && h[..4].SequenceEqual("8BPS"u8))
            return ImageFormat.Psd;
        var isoFormat = DetectIsoBaseMediaFormat(h);
        if (isoFormat != ImageFormat.Unknown)
            return isoFormat;
        if (LooksLikeSvg(h))
            return ImageFormat.Svg;
        return ImageFormat.Unknown;
    }

    private static ImageFormat DetectIsoBaseMediaFormat(ReadOnlySpan<byte> h)
    {
        if (h.Length < 12 || !h[4..8].SequenceEqual("ftyp"u8))
            return ImageFormat.Unknown;

        // The major brand is followed by a minor version and zero or more compatible brands.
        for (var offset = 8; offset + 4 <= h.Length; offset += 4)
        {
            var brand = h.Slice(offset, 4);
            if (brand.SequenceEqual("avif"u8) || brand.SequenceEqual("avis"u8))
                return ImageFormat.Avif;
            if (brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8)
                || brand.SequenceEqual("hevc"u8) || brand.SequenceEqual("hevx"u8)
                || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("msf1"u8))
                return ImageFormat.Heif;
        }

        return ImageFormat.Unknown;
    }

    /// <summary>Heuristic: SVG is text, so accept leading whitespace/BOM then "&lt;svg" or "&lt;?xml".</summary>
    private static bool LooksLikeSvg(ReadOnlySpan<byte> h)
    {
        var probe = h[..Math.Min(h.Length, 256)];
        var text = Encoding.UTF8.GetString(probe).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return text.StartsWith("<svg", StringComparison.OrdinalIgnoreCase)
            || (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
                && text.Contains("<svg", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Extension consistency check for the document diagnostics log (mismatch = warning, not error).</summary>
    public static bool ExtensionMatches(ImageFormat format, string? extension)
    {
        if (string.IsNullOrEmpty(extension))
            return true;
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return format switch
        {
            ImageFormat.Png => ext is "png",
            ImageFormat.Jpeg => ext is "jpg" or "jpeg" or "jfif",
            ImageFormat.Gif => ext is "gif",
            ImageFormat.Bmp => ext is "bmp" or "dib" or "rle",
            ImageFormat.Tiff => ext is "tif" or "tiff",
            ImageFormat.WebP => ext is "webp",
            ImageFormat.Ico => ext is "ico",
            ImageFormat.Pdf => ext is "pdf",
            ImageFormat.Psd => ext is "psd",
            ImageFormat.Svg => ext is "svg" or "svgz",
            ImageFormat.Avif => ext is "avif",
            ImageFormat.Heif => ext is "heic" or "heif" or "hif",
            _ => true,
        };
    }
}
