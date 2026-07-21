using System.Collections.Immutable;

namespace EzyImageViewer.Core.Documents.Layers;

public static class AnnotationValidator
{
    public const int MaxNameLength = 256;
    public const int MaxFontFamilyLength = 128;
    public const int MaxTextLength = 32_768;
    public const int MaxInkPoints = 65_536;
    public const int MaxLayers = 1_000;
    public const float MaxStrokeWidth = 1_000f;
    public const float MaxFontSize = 10_000f;
    /// <summary>Protection dials. Lower bounds keep the dials from degenerating to a no-op blur or
    /// per-pixel mosaic; the sigma ceiling keeps the renderer's full 3-sigma padding affordable.</summary>
    public const float MinMosaicBlockSize = 2f;
    public const float MaxMosaicBlockSize = 1_024f;
    public const float MinBlurSigma = 0.5f;
    public const float MaxBlurSigma = 80f;
    public const long MaxRasterAssetBytes = 64L * 1024 * 1024;

    /// <summary>Validates layer-level fields only; contained annotations are validated where they
    /// enter the document (add/insert/replace paths), not re-walked on every layer touch.</summary>
    public static AnnotationLayer Validate(AnnotationLayer layer)
    {
        ArgumentNullException.ThrowIfNull(layer);
        if (layer.Id == Guid.Empty)
            throw new ArgumentException("Layer id cannot be empty.", nameof(layer));
        if (layer.Name is null || layer.Name.Length > MaxNameLength)
            throw new ArgumentException($"Layer name exceeds {MaxNameLength} characters.", nameof(layer));
        if (layer.Annotations is null)
            throw new ArgumentException("Layer annotation list cannot be null.", nameof(layer));
        return layer;
    }

    public static Annotation Validate(Annotation annotation)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (annotation.Id == Guid.Empty)
            throw new ArgumentException("Annotation id cannot be empty.", nameof(annotation));
        if (annotation.Name?.Length > MaxNameLength)
            throw new ArgumentException($"Annotation name exceeds {MaxNameLength} characters.", nameof(annotation));
        if (!float.IsFinite(annotation.RotationDegrees))
            throw new ArgumentException("Annotation rotation must be finite.", nameof(annotation));
        ValidateBounds(annotation.Bounds);

        switch (annotation)
        {
            case InkAnnotation ink:
                ValidateEnum(ink.Kind, nameof(ink.Kind));
                ValidatePoints(ink.Points);
                ValidateStroke(ink.StrokeWidth, ink.Opacity);
                break;
            case LineAnnotation line:
                ValidatePoint(line.Start, nameof(line.Start));
                ValidatePoint(line.End, nameof(line.End));
                ValidateEnum(line.StartArrowhead, nameof(line.StartArrowhead));
                ValidateEnum(line.EndArrowhead, nameof(line.EndArrowhead));
                ValidateStroke(line.StrokeWidth, line.Opacity);
                break;
            case RectangleAnnotation rectangle:
                ValidateEnum(rectangle.Shape, nameof(rectangle.Shape));
                ValidateStroke(rectangle.StrokeWidth, rectangle.Opacity);
                if (!float.IsFinite(rectangle.CornerRadius) || rectangle.CornerRadius < 0f)
                    throw new ArgumentException("Corner radius must be finite and non-negative.", nameof(annotation));
                break;
            case TextAnnotation text:
                if (text.Text is null || text.Text.Length > MaxTextLength)
                    throw new ArgumentException($"Text exceeds {MaxTextLength} characters.", nameof(annotation));
                if (string.IsNullOrWhiteSpace(text.FontFamily)
                    || text.FontFamily.Length > MaxFontFamilyLength)
                    throw new ArgumentException("Font family is empty or too long.", nameof(annotation));
                ValidateFontSize(text.FontSize);
                ValidateEnum(text.Alignment, nameof(text.Alignment));
                ValidateOpacity(text.Opacity);
                break;
            case NumberMarkerAnnotation marker:
                if (marker.Number <= 0)
                    throw new ArgumentException("Marker number must be positive.", nameof(annotation));
                ValidateFontSize(marker.FontSize);
                ValidateOpacity(marker.Opacity);
                break;
            case ImageAnnotation image:
                if (image.AssetId == Guid.Empty)
                    throw new ArgumentException("Image asset id cannot be empty.", nameof(annotation));
                ValidateOpacity(image.Opacity);
                break;
            case ProtectionAnnotation protection:
                ValidateEnum(protection.Kind, nameof(protection.Kind));
                // A rotated region would sample axis-aligned pixels but cover a rotated area —
                // a mismatch a privacy tool must not allow, so protection never rotates (ADR-0015).
                if (protection.RotationDegrees != 0f)
                    throw new ArgumentException(
                        "Protection regions cannot rotate.", nameof(annotation));
                if (!float.IsFinite(protection.BlockSize)
                    || protection.BlockSize is < MinMosaicBlockSize or > MaxMosaicBlockSize)
                    throw new ArgumentException(
                        $"Mosaic block size must be in [{MinMosaicBlockSize}, {MaxMosaicBlockSize}].",
                        nameof(annotation));
                if (!float.IsFinite(protection.BlurSigma)
                    || protection.BlurSigma is < MinBlurSigma or > MaxBlurSigma)
                    throw new ArgumentException(
                        $"Blur sigma must be in [{MinBlurSigma}, {MaxBlurSigma}].", nameof(annotation));
                break;
            default:
                throw new NotSupportedException($"Unknown annotation {annotation.GetType().Name}.");
        }
        return annotation;
    }

    public static RasterAsset Validate(RasterAsset asset)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Id == Guid.Empty)
            throw new ArgumentException("Raster asset id cannot be empty.", nameof(asset));
        if (asset.EncodedBytes.IsDefaultOrEmpty
            || asset.EncodedBytes.Length > MaxRasterAssetBytes)
            throw new ArgumentException(
                $"Raster asset must contain 1..{MaxRasterAssetBytes} encoded bytes.", nameof(asset));
        if (asset.PixelSize.IsEmpty)
            throw new ArgumentException("Raster asset dimensions must be positive.", nameof(asset));
        if (string.IsNullOrWhiteSpace(asset.Format) || asset.Format.Length > 32)
            throw new ArgumentException("Raster asset format is empty or too long.", nameof(asset));
        return asset;
    }

    public static RectF ValidateBounds(RectF bounds)
    {
        if (!float.IsFinite(bounds.X) || !float.IsFinite(bounds.Y)
            || !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height)
            || !float.IsFinite(bounds.Right) || !float.IsFinite(bounds.Bottom)
            || bounds.Width < 0f || bounds.Height < 0f)
            throw new ArgumentException($"Annotation bounds {bounds} are invalid.", nameof(bounds));
        return bounds;
    }

    private static void ValidatePoints(ImmutableArray<AnnotationPoint> points)
    {
        if (points.IsDefaultOrEmpty || points.Length > MaxInkPoints)
            throw new ArgumentException($"Ink must contain 1..{MaxInkPoints} points.", nameof(points));
        foreach (var point in points)
            ValidatePoint(point, nameof(points));
    }

    private static void ValidatePoint(AnnotationPoint point, string parameterName)
    {
        if (!float.IsFinite(point.X) || !float.IsFinite(point.Y))
            throw new ArgumentException("Annotation points must be finite.", parameterName);
    }

    private static void ValidateStroke(float width, float opacity)
    {
        if (!float.IsFinite(width) || width <= 0f || width > MaxStrokeWidth)
            throw new ArgumentException($"Stroke width must be in (0, {MaxStrokeWidth}].");
        ValidateOpacity(opacity);
    }

    private static void ValidateFontSize(float size)
    {
        if (!float.IsFinite(size) || size <= 0f || size > MaxFontSize)
            throw new ArgumentException($"Font size must be in (0, {MaxFontSize}].");
    }

    private static void ValidateOpacity(float opacity)
    {
        if (!float.IsFinite(opacity) || opacity < 0f || opacity > 1f)
            throw new ArgumentException("Opacity must be in [0, 1].");
    }

    private static void ValidateEnum<T>(T value, string parameterName) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(parameterName, value, "Unknown enum value.");
    }
}
