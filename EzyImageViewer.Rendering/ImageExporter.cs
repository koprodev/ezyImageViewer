using SkiaSharp;

namespace EzyImageViewer.Rendering;

public enum ExportFormat
{
    Png,
    Jpeg,
    WebP,
}

/// <summary>내보내기별 설정. 품질은 JPEG·손실 WebP에 적용하고 PNG는 무시.</summary>
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

    /// <summary>WebP 전용(FR-OUT-005).</summary>
    public bool WebPLossless { get; init; }

    /// <summary>JPEG에는 알파가 없어 투명 영역을 이 색 위에 합성(FR-OUT-004).</summary>
    public uint JpegBackgroundArgb { get; init; } = 0xFFFF_FFFF;

    /// <summary>FR-OUT-008 유지 옵션(Q6=b).
    /// <see cref="ExportMetadata"/>가 민감 필드를 뺀 뒤 원본 EXIF 전달.
    /// false가 기본이며 구조적으로 전부 제거. 개인정보 최소화는 선택이 아니라 기본값.</summary>
    public bool KeepMetadata { get; init; }
}

/// <summary>
/// 평탄화 래스터 인코딩(FR-OUT-003~005).
/// Skia 재인코딩은 원본 메타데이터를 옮기지 않으므로 FR-OUT-008 기본 제거는 필터가 아닌 구조적 보장.
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
        // Skia 무손실 WebP는 픽스맵 인코더 옵션 경로 사용.
        using var raster = ToRaster(image);
        using var pixmap = raster.PeekPixels()
            ?? throw new InvalidOperationException("WebP encoding failed: no readable pixels.");
        using var data = pixmap.Encode(new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 100))
            ?? throw new InvalidOperationException("Lossless WebP encoding failed.");
        return data.ToArray();
    }

    /// <summary>투명 영역을 배경에 평탄화. JPEG는 알파를 담지 못함.</summary>
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
