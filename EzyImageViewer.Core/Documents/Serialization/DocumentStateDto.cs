using System.Text.Json.Serialization;

namespace EzyImageViewer.Core.Documents.Serialization;

/// <summary>
/// document.json의 저장소 중립 편집 상태 조각. 순서 있는 변환과 레이어 주석만 포함.
/// 버전은 컨테이너 manifest가 단독 소유하며 v1은 평면 주석, v2는 레이어.
/// 행렬은 저장하지 않고 로드 때 계산.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DocumentStateDto
{
    public required List<TransformOpDto> Transform { get; init; }

    /// <summary>v1 전용 평면 그리기 순서. 읽을 때 기본 레이어 하나로 승격.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AnnotationDto>? Annotations { get; init; }

    /// <summary>v2 전용 레이어 그리기 순서. 0번이 맨 뒤.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AnnotationLayerDto>? Layers { get; init; }

    /// <summary>v2 선택 작성 힌트. 있으면 기존 레이어를 가리켜야 함.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ActiveLayerId { get; init; }

    public List<RasterAssetDto>? Assets { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AnnotationLayerDto
{
    public required Guid Id { get; init; }
    public string? Name { get; init; }
    public bool? IsVisible { get; init; }
    public bool? IsLocked { get; init; }
    public required List<AnnotationDto> Annotations { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RasterAssetDto
{
    public required Guid Id { get; init; }
    public required byte[] EncodedBytes { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string Format { get; init; }
}

/// <summary>닫힌 변환 종류 집합. 모르는 종류는 조용히 버리지 않고 읽기 실패.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(CropOpDto), "crop")]
[JsonDerivedType(typeof(RotateOpDto), "rotate")]
[JsonDerivedType(typeof(FlipOpDto), "flip")]
[JsonDerivedType(typeof(ResizeOpDto), "resize")]
[JsonDerivedType(typeof(EraseOpDto), "erase")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract class TransformOpDto
{
    private protected TransformOpDto() { }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class CropOpDto : TransformOpDto
{
    public required float X { get; init; }
    public required float Y { get; init; }
    public required float Width { get; init; }
    public required float Height { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RotateOpDto : TransformOpDto
{
    public required float Degrees { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class FlipOpDto : TransformOpDto
{
    public required bool Horizontal { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ResizeOpDto : TransformOpDto
{
    public required int Width { get; init; }
    public required int Height { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class EraseOpDto : TransformOpDto
{
    public required float X { get; init; }
    public required float Y { get; init; }
    public required float Width { get; init; }
    public required float Height { get; init; }
}

/// <summary>닫힌 주석 종류 집합. 새 선택 필드는 v1 사각형 입력 호환 유지.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind",
    UnknownDerivedTypeHandling = JsonUnknownDerivedTypeHandling.FailSerialization)]
[JsonDerivedType(typeof(RectangleAnnotationDto), "rectangle")]
[JsonDerivedType(typeof(InkAnnotationDto), "ink")]
[JsonDerivedType(typeof(LineAnnotationDto), "line")]
[JsonDerivedType(typeof(TextAnnotationDto), "text")]
[JsonDerivedType(typeof(NumberMarkerAnnotationDto), "number")]
[JsonDerivedType(typeof(SpeechBubbleAnnotationDto), "speechBubble")]
[JsonDerivedType(typeof(ImageAnnotationDto), "image")]
[JsonDerivedType(typeof(ProtectionAnnotationDto), "protection")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract class AnnotationDto
{
    private protected AnnotationDto() { }

    public required Guid Id { get; init; }
    public string? Name { get; init; }
    public bool? IsVisible { get; init; }
    public bool? IsLocked { get; init; }
    public float? RotationDegrees { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RectangleAnnotationDto : AnnotationDto
{
    public required float X { get; init; }
    public required float Y { get; init; }
    public required float Width { get; init; }
    public required float Height { get; init; }
    public required uint StrokeArgb { get; init; }
    public required float StrokeWidth { get; init; }
    public int? Shape { get; init; }
    public uint? FillArgb { get; init; }
    public float? CornerRadius { get; init; }
    public float? Opacity { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class AnnotationPointDto
{
    public required float X { get; init; }
    public required float Y { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class InkAnnotationDto : AnnotationDto
{
    public required List<AnnotationPointDto> Points { get; init; }
    public required int InkKind { get; init; }
    public required uint StrokeArgb { get; init; }
    public required float StrokeWidth { get; init; }
    public required float Opacity { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class LineAnnotationDto : AnnotationDto
{
    public required AnnotationPointDto Start { get; init; }
    public required AnnotationPointDto End { get; init; }
    public required int StartArrowhead { get; init; }
    public required int EndArrowhead { get; init; }
    public required uint StrokeArgb { get; init; }
    public required float StrokeWidth { get; init; }
    public required float Opacity { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class TextAnnotationDto : AnnotationDto
{
    public required float X { get; init; }
    public required float Y { get; init; }
    public required float Width { get; init; }
    public required float Height { get; init; }
    public required string Text { get; init; }
    public required string FontFamily { get; init; }
    public required float FontSize { get; init; }
    public required bool IsBold { get; init; }
    public required bool IsItalic { get; init; }
    public required uint ForegroundArgb { get; init; }
    public uint? BackgroundArgb { get; init; }
    public required int Alignment { get; init; }
    public required float Opacity { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SpeechBubbleAnnotationDto : AnnotationDto
{
    public required float X { get; init; }
    public required float Y { get; init; }
    public required float Width { get; init; }
    public required float Height { get; init; }
    public required float TailX { get; init; }
    public required float TailY { get; init; }
    public required string Text { get; init; }
    public required string FontFamily { get; init; }
    public required float FontSize { get; init; }
    public required bool IsBold { get; init; }
    public required bool IsItalic { get; init; }
    public required uint ForegroundArgb { get; init; }
    public required int Alignment { get; init; }
    public required uint FillArgb { get; init; }
    public required uint StrokeArgb { get; init; }
    public required float StrokeWidth { get; init; }
    public required float CornerRadius { get; init; }
    public required float Opacity { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class NumberMarkerAnnotationDto : AnnotationDto
{
    public required float X { get; init; }
    public required float Y { get; init; }
    public required float Width { get; init; }
    public required float Height { get; init; }
    public required int Number { get; init; }
    public required uint FillArgb { get; init; }
    public required uint ForegroundArgb { get; init; }
    public required float FontSize { get; init; }
    public required float Opacity { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ImageAnnotationDto : AnnotationDto
{
    public required float X { get; init; }
    public required float Y { get; init; }
    public required float Width { get; init; }
    public required float Height { get; init; }
    public required Guid AssetId { get; init; }
    public required float Opacity { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class ProtectionAnnotationDto : AnnotationDto
{
    public required float X { get; init; }
    public required float Y { get; init; }
    public required float Width { get; init; }
    public required float Height { get; init; }
    public required int ProtectionKind { get; init; }
    public required float BlockSize { get; init; }
    public required float BlurSigma { get; init; }
    public required uint MaskArgb { get; init; }
}
