using System.Collections.Immutable;

namespace EzyImageViewer.Core.Documents.Layers;

/// <summary>원본 이미지 픽셀 기준 축 정렬 사각형.</summary>
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

/// <summary>문서 주석 레이어의 불변 개체. 도형 좌표는 원본 픽셀 기준.</summary>
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

/// <summary>말풍선(FR-ANNO-007): 편집 가능한 글이 든 둥근 몸통과 사용자가 끄는 꼬리.
/// <see cref="TailTip"/>은 회전 전 주석 로컬 픽셀이며 몸통에 비례해 재배치되어 이동·크기 변경을 함께 따라감.</summary>
public sealed record SpeechBubbleAnnotation : Annotation
{
    public override required RectF Bounds { get; init; }
    public required AnnotationPoint TailTip { get; init; }
    public required string Text { get; init; }
    public string FontFamily { get; init; } = "Malgun Gothic";
    public float FontSize { get; init; } = 24f;
    public bool IsBold { get; init; }
    public bool IsItalic { get; init; }
    public uint ForegroundArgb { get; init; } = 0xFF00_0000;
    public AnnotationTextAlignment Alignment { get; init; }
    public uint FillArgb { get; init; } = 0xFFFF_FFFF;
    public uint StrokeArgb { get; init; } = 0xFF00_0000;
    public float StrokeWidth { get; init; } = 2f;
    public float CornerRadius { get; init; } = 8f;
    public float Opacity { get; init; } = 1f;

    public override long EstimatedRetainedBytes => checked(
        CommonRetainedBytes + ((long)Text.Length * sizeof(char)) +
        ((long)FontFamily.Length * sizeof(char)) + 64);

    public override Annotation WithBounds(RectF bounds) => this with
    {
        Bounds = bounds,
        TailTip = AnnotationGeometry.Remap(TailTip, Bounds, bounds),
    };
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

/// <summary>개인정보 보호 영역(FR-ANNO-008~010). 흐려지면 보호가 아니므로 항상 완전 불투명.
/// 모자이크·흐림 값은 원본 픽셀 기준이며 <see cref="Kind"/>에 맞는 필드만 유효.</summary>
public sealed record ProtectionAnnotation : Annotation
{
    public override required RectF Bounds { get; init; }
    public required ProtectionKind Kind { get; init; }
/// <summary>모자이크: 블록 한 변당 원본 픽셀 수.</summary>
    public float BlockSize { get; init; } = 12f;
/// <summary>흐림: 원본 픽셀 기준 가우시안 시그마.</summary>
    public float BlurSigma { get; init; } = 8f;
/// <summary>가리기: 채우기 색. 렌더 때 알파를 불투명으로 강제.</summary>
    public uint MaskArgb { get; init; } = 0xFF00_0000;

    public override long EstimatedRetainedBytes => CommonRetainedBytes + 32;
    public override Annotation WithBounds(RectF bounds) => this with { Bounds = bounds };
}
