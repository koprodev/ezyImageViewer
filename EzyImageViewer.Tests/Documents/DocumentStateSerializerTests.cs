using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Documents.Serialization;
using EzyImageViewer.Core.Imaging;
using Xunit;

namespace EzyImageViewer.Tests.Documents;

/// <summary>
/// The storage-neutral v1 fragment (ADR-0003:13 / ADR-0009). Every read is hostile-input territory:
/// nothing degrades silently — unknown, missing, duplicate or absurd input fails the read.
/// </summary>
public class DocumentStateSerializerTests
{
    private static DocumentState SampleState()
    {
        var transform = BackgroundTransform.Identity
            .Append(new CropOp(new RectF(10.25f, 20.5f, 300f, 200f)))
            .Append(new RotateOp(17.5f))
            .Append(new FlipOp(Horizontal: true))
            .Append(new ResizeOp(new PixelSize(640, 480)))
            .Append(new EraseOp(new RectF(12f, 14f, 40f, 30f)));
        return new DocumentState { Transform = transform }
            .AddAnnotation(new RectangleAnnotation
            {
                Id = Guid.NewGuid(),
                Bounds = new RectF(5f, 6f, 70f, 80f),
                StrokeArgb = 0xFF123456,
                StrokeWidth = 2.5f,
            })
            .AddAnnotation(new RectangleAnnotation
            {
                Id = Guid.NewGuid(),
                Bounds = new RectF(100f, 100f, 10f, 10f),
            });
    }

    [Fact]
    public void RoundTrip_PreservesOpOrderAndAnnotations()
    {
        var state = SampleState();

        var restored = DocumentStateSerializer.Read(DocumentStateSerializer.Write(state));

        Assert.Equal(state.Transform, restored.Transform); // sequence equality, order included
        Assert.Equal(state.Annotations.Count, restored.Annotations.Count);
        for (var i = 0; i < state.Annotations.Count; i++)
            Assert.Equal(state.Annotations[i], restored.Annotations[i]); // record value equality
    }

    [Fact]
    public void EmptyState_RoundTrips()
    {
        var restored = DocumentStateSerializer.Read(DocumentStateSerializer.Write(DocumentState.Empty));
        Assert.True(restored.Transform.IsIdentity);
        Assert.Empty(restored.Annotations);
    }

    [Fact]
    public void Write_UsesClosedDiscriminators()
    {
        var json = DocumentStateSerializer.Write(SampleState());
        Assert.Contains("\"kind\":\"crop\"", json);
        Assert.Contains("\"kind\":\"rotate\"", json);
        Assert.Contains("\"kind\":\"flip\"", json);
        Assert.Contains("\"kind\":\"resize\"", json);
        Assert.Contains("\"kind\":\"erase\"", json);
        Assert.Contains("\"kind\":\"rectangle\"", json);
    }

    // ---- rejection paths ----

    [Fact]
    public void UnknownOpKind_FailsTheRead()
    {
        const string json = """{"transform":[{"kind":"skew","x":1}],"annotations":[]}""";
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(json));
    }

    [Fact]
    public void MissingDiscriminator_FailsTheRead()
    {
        const string json = """{"transform":[{"degrees":90}],"annotations":[]}""";
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(json));
    }

    [Fact]
    public void UnknownProperty_FailsTheRead_InsteadOfSilentlyDropping()
    {
        const string json = """{"transform":[{"kind":"rotate","degrees":90,"pivot":"corner"}],"annotations":[]}""";
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(json));
    }

    [Fact]
    public void MissingRequiredField_FailsTheRead()
    {
        const string json = """{"transform":[{"kind":"rotate"}],"annotations":[]}""";
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(json));
    }

    [Fact]
    public void DuplicateAnnotationIds_FailTheRead()
    {
        var id = Guid.NewGuid();
        var json = $$"""
            {"transform":[],"annotations":[
              {"kind":"rectangle","id":"{{id}}","x":0,"y":0,"width":1,"height":1,"strokeArgb":1,"strokeWidth":1},
              {"kind":"rectangle","id":"{{id}}","x":5,"y":5,"width":1,"height":1,"strokeArgb":1,"strokeWidth":1}]}
            """;
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(json));
    }

    [Fact]
    public void EmptyAnnotationId_FailsTheRead()
    {
        var json = $$"""{"transform":[],"annotations":[{"kind":"rectangle","id":"{{Guid.Empty}}","x":0,"y":0,"width":1,"height":1,"strokeArgb":1,"strokeWidth":1}]}""";
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(json));
    }

    [Fact]
    public void OutOfRangeNumbers_FailTheRead()
    {
        // 1e39 overflows float; a negative crop/erase extent fails the domain constructor.
        const string overflow = """{"transform":[{"kind":"rotate","degrees":1e39}],"annotations":[]}""";
        const string negative = """{"transform":[{"kind":"crop","x":0,"y":0,"width":-5,"height":10}],"annotations":[]}""";
        const string negativeErase = """{"transform":[{"kind":"erase","x":0,"y":0,"width":10,"height":-1}],"annotations":[]}""";
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(overflow));
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(negative));
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(negativeErase));
    }

    [Fact]
    public void AbsurdOpCount_FailsTheRead()
    {
        var ops = string.Join(",", Enumerable.Repeat("""{"kind":"flip","horizontal":true}""", DocumentStateSerializer.MaxOps + 1));
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read($$"""{"transform":[{{ops}}],"annotations":[]}"""));
    }

    [Fact]
    public void MalformedOrEmptyJson_FailsTheRead()
    {
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read("{"));
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read("null"));
    }

    [Fact]
    public void NullSections_FailTheRead_NotWithANullReference()
    {
        // `required` accepts an explicit JSON null for a reference type — the guard is ours.
        Assert.Throws<InvalidDataException>(() =>
            DocumentStateSerializer.Read("""{"transform":null,"annotations":[]}"""));
        Assert.Throws<InvalidDataException>(() =>
            DocumentStateSerializer.Read("""{"transform":[],"annotations":null}"""));
    }

    [Fact]
    public void NullElements_FailTheRead_NotWithANullReference()
    {
        // A JSON `null` list element deserializes as a null entry, not a JsonException.
        Assert.Throws<InvalidDataException>(() =>
            DocumentStateSerializer.Read("""{"transform":[null],"annotations":[]}"""));
        Assert.Throws<InvalidDataException>(() =>
            DocumentStateSerializer.Read("""{"transform":[],"annotations":[null]}"""));
    }

    [Fact]
    public void Write_RefusesWhatReadWouldRefuse()
    {
        // Symmetry invariant: a state Write accepts must round-trip through Read.
        var transform = BackgroundTransform.Identity;
        for (var i = 0; i <= DocumentStateSerializer.MaxOps; i++)
            transform = transform.Append(new FlipOp(true));
        var oversized = new DocumentState { Transform = transform };

        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Write(oversized));
    }

    [Fact]
    public void OversizedJson_IsRejectedBeforeParsing()
    {
        var huge = new string(' ', DocumentStateSerializer.MaxJsonChars + 1);
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(huge));
    }

    [Fact]
    public void FiniteComponentsWithNonFiniteExtremes_FailTheRead()
    {
        // 3e38 + 3e38 = Infinity: X and Width are individually finite, Right is not.
        var json = $$"""{"transform":[],"annotations":[{"kind":"rectangle","id":"{{Guid.NewGuid()}}","x":3e38,"y":0,"width":3e38,"height":1,"strokeArgb":1,"strokeWidth":1}]}""";
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(json));
    }
}
