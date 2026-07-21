using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Documents.Serialization;
using Xunit;

namespace EzyImageViewer.Tests.Documents;

/// <summary>M5 acceptance: protection objects (FR-ANNO-008~010) survive serialization and reject
/// dials that would not actually obscure.</summary>
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
        // A rotated region would sample axis-aligned pixels but cover a rotated area (ADR-0015).
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
        // Unknown protection kind.
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read($$"""
            {"transform":[],"layers":[{"id":"{{Guid.NewGuid()}}","annotations":[
            {"kind":"protection","id":"{{id}}","x":1,"y":2,"width":3,"height":4,
            "protectionKind":99,"blockSize":12,"blurSigma":8,"maskArgb":4278190080}]}]}
            """));
        // Block size below the obscuring minimum.
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read($$"""
            {"transform":[],"layers":[{"id":"{{Guid.NewGuid()}}","annotations":[
            {"kind":"protection","id":"{{id}}","x":1,"y":2,"width":3,"height":4,
            "protectionKind":0,"blockSize":0.5,"blurSigma":8,"maskArgb":4278190080}]}]}
            """));
    }
}
