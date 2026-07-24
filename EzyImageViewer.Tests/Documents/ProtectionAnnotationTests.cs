using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Documents.Serialization;
using Xunit;

namespace EzyImageViewer.Tests.Documents;

/// <summary>M5 인수: 보호 개체(FR-ANNO-008~010)는 직렬화 뒤에도 유지되고 가리지 못하는 강도는 거부.</summary>
public sealed class ProtectionAnnotationTests
{
    private static ProtectionAnnotation Protection(ProtectionKind kind) => new()
    {
        Id = Guid.NewGuid(),
        Bounds = new RectF(4, 5, 60, 40),
        Kind = kind,
        BlockSize = 16f,
        BlurSigma = 6f,
        MaskArgb = 0xFF11_2233,
    };

    [Fact]
    public void AllProtectionKinds_RoundTripTheirFields()
    {
        var state = DocumentState.Empty
            .AddAnnotation(Protection(ProtectionKind.Mosaic))
            .AddAnnotation(Protection(ProtectionKind.Blur))
            .AddAnnotation(Protection(ProtectionKind.Mask) with { IsLocked = true, Name = "가림" });

        var restored = DocumentStateSerializer.Read(DocumentStateSerializer.Write(state));

        Assert.Equal(state.Annotations, restored.Annotations);
    }

    [Fact]
    public void Validator_RejectsDialsThatWouldNotObscure()
    {
        Assert.Throws<ArgumentException>(() => AnnotationValidator.Validate(
            Protection(ProtectionKind.Mosaic) with { BlockSize = 1f }));
        Assert.Throws<ArgumentException>(() => AnnotationValidator.Validate(
            Protection(ProtectionKind.Mosaic) with { BlockSize = 2_000f }));
        Assert.Throws<ArgumentException>(() => AnnotationValidator.Validate(
            Protection(ProtectionKind.Blur) with { BlurSigma = 0.1f }));
        Assert.Throws<ArgumentException>(() => AnnotationValidator.Validate(
            Protection(ProtectionKind.Blur) with { BlurSigma = 100f }));
        Assert.Throws<ArgumentException>(() => AnnotationValidator.Validate(
            Protection(ProtectionKind.Blur) with { BlurSigma = float.PositiveInfinity }));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnnotationValidator.Validate(
            Protection((ProtectionKind)99)));
    }

    [Fact]
    public void RotatedProtection_IsRejectedEverywhere()
    {
        // 회전 영역은 축 정렬 픽셀을 뽑고 비스듬한 곳을 덮으므로 금지(ADR-0015).
        Assert.Throws<ArgumentException>(() => AnnotationValidator.Validate(
            Protection(ProtectionKind.Mask) with { RotationDegrees = 15f }));
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read($$"""
            {"transform":[],"layers":[{"id":"{{Guid.NewGuid()}}","annotations":[
            {"kind":"protection","id":"{{Guid.NewGuid()}}","x":1,"y":2,"width":3,"height":4,
            "rotationDegrees":15,"protectionKind":2,"blockSize":12,"blurSigma":8,"maskArgb":4278190080}]}]}
            """));
    }

    [Fact]
    public void HostileProtectionFragments_FailTheRead()
    {
        var id = Guid.NewGuid();
        // 모르는 보호 종류.
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read($$"""
            {"transform":[],"layers":[{"id":"{{Guid.NewGuid()}}","annotations":[
            {"kind":"protection","id":"{{id}}","x":1,"y":2,"width":3,"height":4,
            "protectionKind":99,"blockSize":12,"blurSigma":8,"maskArgb":4278190080}]}]}
            """));
        // 실제로 가리지 못하는 최소값 미만 블록.
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read($$"""
            {"transform":[],"layers":[{"id":"{{Guid.NewGuid()}}","annotations":[
            {"kind":"protection","id":"{{id}}","x":1,"y":2,"width":3,"height":4,
            "protectionKind":0,"blockSize":0.5,"blurSigma":8,"maskArgb":4278190080}]}]}
            """));
    }
}
