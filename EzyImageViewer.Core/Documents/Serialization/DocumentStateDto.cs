using System.Text.Json.Serialization;

namespace EzyImageViewer.Core.Documents.Serialization;

/// <summary>
/// Storage-neutral fragment of document.json (ADR-0003:13, ADR-0009): the editable state only —
/// ordered transform ops and layered annotations. Background reference, pages and screen state join
/// at M6 (SSOT §7.10); versioning rides the container manifest so there is exactly one migration
/// contract (ProjectContainer schema checks), not a second version field here.
/// Shape per version: v1 uses the flat <see cref="Annotations"/> list, v2 uses <see cref="Layers"/>
/// (UR-007). Exactly one of the two must be present; the writer emits v2 only.
/// Ops are stored as ordered parameters; matrices are derived on load, never persisted.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class DocumentStateDto
{
    public required List<TransformOpDto> Transform { get; init; }

    /// <summary>v1 only: flat paint order. Migrated to a single default layer on read.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AnnotationDto>? Annotations { get; init; }

    /// <summary>v2 only: layered paint order, index 0 farthest back.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<AnnotationLayerDto>? Layers { get; init; }

    /// <summary>v2 optional authoring hint; when present it must reference an existing layer.</summary>
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

/// <summary>Closed discriminator set: an unknown kind fails the read (never silently dropped).</summary>
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

/// <summary>Closed annotation discriminator set. New optional fields preserve v1 rectangle input.</summary>
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
