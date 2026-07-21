using System.Collections.Immutable;

namespace EzyImageViewer.Core.Documents.Layers;

/// <summary>Axis-aligned rectangle in native image pixels.</summary>
public readonly record struct RectF(float X, float Y, float Width, float Height)
{
    public float Right => X + Width;
    public float Bottom => Y + Height;
    public float CenterX => X + (Width / 2f);
    public float CenterY => Y + (Height / 2f);

    public static RectF FromCorners(float x0, float y0, float x1, float y1) =>
        new(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0), Math.Abs(y1 - y0));

    public RectF Translated(float dx, float dy) => this with { X = X + dx, Y = Y + dy };
}

public readonly record struct AnnotationPoint(float X, float Y);

public enum InkKind
{
    Pen,
    Highlighter,
}

public enum ArrowheadKind
{
    None,
    Open,
    Triangle,
}

public enum ShapeKind
{
    Rectangle,
    RoundedRectangle,
    Ellipse,
}

public enum AnnotationTextAlignment
{
    Left,
    Center,
    Right,
}

/// <summary>Immutable object on the document's annotation layer; geometry uses native pixels.</summary>
public abstract record Annotation
{
    public required Guid Id { get; init; }
    public string? Name { get; init; }
    public bool IsVisible { get; init; } = true;
    public bool IsLocked { get; init; }
    public float RotationDegrees { get; init; }

    public abstract long EstimatedRetainedBytes { get; }
    public abstract RectF Bounds { get; init; }
    public abstract Annotation WithBounds(RectF bounds);

    protected long CommonRetainedBytes => 48L + ((long)(Name?.Length ?? 0) * sizeof(char));
}

public sealed record InkAnnotation : Annotation
{
    public required ImmutableArray<AnnotationPoint> Points { get; init; }
    public InkKind Kind { get; init; }
    public uint StrokeArgb { get; init; } = 0xFFE8_3B2E;
    public float StrokeWidth { get; init; } = 3f;
    public float Opacity { get; init; } = 1f;

    public override RectF Bounds
    {
        get => AnnotationGeometry.BoundsOf(Points);
        init { }
    }
    public override long EstimatedRetainedBytes =>
        checked(CommonRetainedBytes + ((long)Points.Length * sizeof(float) * 2));

    public override Annotation WithBounds(RectF bounds) =>
        this with { Points = AnnotationGeometry.Remap(Points, Bounds, bounds) };
}

public sealed record LineAnnotation : Annotation
{
    public required AnnotationPoint Start { get; init; }
    public required AnnotationPoint End { get; init; }
    public ArrowheadKind StartArrowhead { get; init; }
    public ArrowheadKind EndArrowhead { get; init; }
    public uint StrokeArgb { get; init; } = 0xFFE8_3B2E;
    public float StrokeWidth { get; init; } = 3f;
    public float Opacity { get; init; } = 1f;

    public override RectF Bounds
    {
        get => RectF.FromCorners(Start.X, Start.Y, End.X, End.Y);
        init { }
    }
    public override long EstimatedRetainedBytes => CommonRetainedBytes + 40;

    public override Annotation WithBounds(RectF bounds) => this with
    {
        Start = AnnotationGeometry.Remap(Start, Bounds, bounds),
        End = AnnotationGeometry.Remap(End, Bounds, bounds),
    };
}

public sealed record RectangleAnnotation : Annotation
{
    public override required RectF Bounds { get; init; }
    public ShapeKind Shape { get; init; }
    public uint StrokeArgb { get; init; } = 0xFFE8_3B2E;
    public float StrokeWidth { get; init; } = 3f;
    public uint? FillArgb { get; init; }
    public float CornerRadius { get; init; } = 8f;
    public float Opacity { get; init; } = 1f;

    public override long EstimatedRetainedBytes => CommonRetainedBytes + 40;
    public override Annotation WithBounds(RectF bounds) => this with { Bounds = bounds };
}

public sealed record TextAnnotation : Annotation
{
    public override required RectF Bounds { get; init; }
    public required string Text { get; init; }
    public string FontFamily { get; init; } = "Malgun Gothic";
    public float FontSize { get; init; } = 24f;
    public bool IsBold { get; init; }
    public bool IsItalic { get; init; }
    public uint ForegroundArgb { get; init; } = 0xFF00_0000;
    public uint? BackgroundArgb { get; init; }
    public AnnotationTextAlignment Alignment { get; init; }
    public float Opacity { get; init; } = 1f;

    public override long EstimatedRetainedBytes => checked(
        CommonRetainedBytes + ((long)Text.Length * sizeof(char)) +
        ((long)FontFamily.Length * sizeof(char)) + 48);

    public override Annotation WithBounds(RectF bounds) => this with { Bounds = bounds };
}

public sealed record NumberMarkerAnnotation : Annotation
{
    public override required RectF Bounds { get; init; }
    public required int Number { get; init; }
    public uint FillArgb { get; init; } = 0xFFE8_3B2E;
    public uint ForegroundArgb { get; init; } = 0xFFFF_FFFF;
    public float FontSize { get; init; } = 18f;
    public float Opacity { get; init; } = 1f;

    public override long EstimatedRetainedBytes => CommonRetainedBytes + 32;
    public override Annotation WithBounds(RectF bounds) => this with { Bounds = bounds };
}

public sealed record ImageAnnotation : Annotation
{
    public override required RectF Bounds { get; init; }
    public required Guid AssetId { get; init; }
    public float Opacity { get; init; } = 1f;

    public override long EstimatedRetainedBytes => CommonRetainedBytes + 32;
    public override Annotation WithBounds(RectF bounds) => this with { Bounds = bounds };
}

public enum ProtectionKind
{
    Mosaic,
    Blur,
    Mask,
}

/// <summary>Privacy region (FR-ANNO-008~010). Always fully opaque — a protection that could be
/// faded would defeat its purpose, so there is no Opacity. Mosaic/blur parameters are in native
/// pixels; only the field matching <see cref="Kind"/> is meaningful.</summary>
public sealed record ProtectionAnnotation : Annotation
{
    public override required RectF Bounds { get; init; }
    public required ProtectionKind Kind { get; init; }
    /// <summary>Mosaic: native pixels per block edge.</summary>
    public float BlockSize { get; init; } = 12f;
    /// <summary>Blur: gaussian sigma in native pixels.</summary>
    public float BlurSigma { get; init; } = 8f;
    /// <summary>Mask: fill color; the alpha byte is forced opaque at render.</summary>
    public uint MaskArgb { get; init; } = 0xFF00_0000;

    public override long EstimatedRetainedBytes => CommonRetainedBytes + 32;
    public override Annotation WithBounds(RectF bounds) => this with { Bounds = bounds };
}
