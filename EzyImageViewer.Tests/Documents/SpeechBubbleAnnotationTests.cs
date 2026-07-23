using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Documents.Serialization;
using Xunit;

namespace EzyImageViewer.Tests.Documents;

/// <summary>
/// FR-ANNO-007 core contracts: the tail rides WithBounds proportionally (move = same delta,
/// resize = preserved normalized position), the tail geometry is one deterministic SSOT shared
/// by hit-testing and band selection, and the DTO kind "speechBubble" round-trips exactly.
/// </summary>
public sealed class SpeechBubbleAnnotationTests
{
    private static SpeechBubbleAnnotation Bubble(
        RectF? bounds = null, AnnotationPoint? tail = null, float rotation = 0f) => new()
        {
            Id = Guid.NewGuid(),
            Bounds = bounds ?? new RectF(10f, 10f, 100f, 50f),
            TailTip = tail ?? new AnnotationPoint(30f, 90f),
            Text = "말풍선",
            RotationDegrees = rotation,
        };

    [Fact]
    public void WithBounds_Move_TranslatesTailByTheSameDelta()
    {
        var bubble = Bubble();
        var moved = (SpeechBubbleAnnotation)bubble.WithBounds(
            bubble.Bounds.Translated(25f, -5f));

        Assert.Equal(bubble.TailTip.X + 25f, moved.TailTip.X, 3);
        Assert.Equal(bubble.TailTip.Y - 5f, moved.TailTip.Y, 3);
    }

    [Fact]
    public void WithBounds_Resize_PreservesNormalizedTailPosition()
    {
        // Tip at normalized (0.2, 1.6) relative to the 100x50 body must stay there after 2x/0.5x.
        var bubble = Bubble();
        var resized = (SpeechBubbleAnnotation)bubble.WithBounds(new RectF(10f, 10f, 200f, 25f));

        Assert.Equal(10f + (0.2f * 200f), resized.TailTip.X, 3);
        Assert.Equal(10f + (1.6f * 25f), resized.TailTip.Y, 3);
    }

    [Fact]
    public void TailTriangle_TipBelow_UsesBottomEdgeWithCornerClamp()
    {
        var bubble = Bubble(new RectF(10f, 5f, 40f, 20f), new AnnotationPoint(30f, 38f));

        Assert.True(SpeechBubbleGeometry.TryGetTail(bubble, out var a, out var b, out var tip));
        // Base sits BaseOverlap inside the bottom edge, centered on the tip's projection.
        Assert.Equal(25f - SpeechBubbleGeometry.BaseOverlap, a.Y, 3);
        Assert.Equal(a.Y, b.Y, 3);
        Assert.True(a.X < 30f && b.X > 30f);
        Assert.Equal(new AnnotationPoint(30f, 38f), tip);

        // A tip projected past the rounded corner clamps clear of it.
        var cornered = Bubble(new RectF(10f, 5f, 40f, 20f), new AnnotationPoint(0f, 38f));
        Assert.True(SpeechBubbleGeometry.TryGetTail(cornered, out var ca, out _, out _));
        Assert.True(ca.X >= 10f + cornered.CornerRadius);
    }

    [Fact]
    public void TailTriangle_TipInsideBody_HasNoTail()
    {
        var bubble = Bubble(new RectF(10f, 10f, 100f, 50f), new AnnotationPoint(50f, 30f));
        Assert.False(SpeechBubbleGeometry.TryGetTail(bubble, out _, out _, out _));
    }

    [Fact]
    public void HitTest_BodyAndTailHit_OutsideMisses()
    {
        var bubble = Bubble(new RectF(10f, 5f, 40f, 20f), new AnnotationPoint(30f, 38f));

        Assert.True(AnnotationGeometry.HitTest(bubble, 30f, 15f));
        Assert.True(AnnotationGeometry.HitTest(bubble, 30f, 32f));
        Assert.False(AnnotationGeometry.HitTest(bubble, 55f, 38f));
        Assert.False(AnnotationGeometry.HitTest(bubble, 10f, 38f));
    }

    [Fact]
    public void BandSelection_TouchingOnlyTheTail_SelectsTheBubble()
    {
        var bubble = Bubble(new RectF(10f, 5f, 40f, 20f), new AnnotationPoint(30f, 38f));

        Assert.True(AnnotationGeometry.Intersects(bubble, new RectF(28f, 30f, 4f, 4f)));
        Assert.False(AnnotationGeometry.Intersects(bubble, new RectF(60f, 30f, 8f, 8f)));
    }

    [Fact]
    public void TailHandle_OnlyBubblesExposeIt_AndItSitsOnTheTip()
    {
        var bubble = Bubble(new RectF(10f, 5f, 40f, 20f), new AnnotationPoint(30f, 38f));
        Assert.Equal(SelectionHandle.Tail, SelectionGeometry.HitTest(
            bubble, new AnnotationPoint(30f, 38f), 4f, 24f));

        // A rectangle probed at its center must not report the Tail fallback point.
        var rectangle = new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(10f, 5f, 40f, 20f),
        };
        Assert.Equal(SelectionHandle.None, SelectionGeometry.HitTest(
            rectangle, new AnnotationPoint(30f, 15f), 4f, 24f));
        Assert.False(SelectionGeometry.HandleApplies(rectangle, SelectionHandle.Tail));
    }

    [Fact]
    public void MoveTail_UnderRotation_StoresPreRotationLocalCoordinates()
    {
        var bubble = Bubble(new RectF(10f, 10f, 100f, 50f), new AnnotationPoint(30f, 90f), 90f);
        var pointerAtLocal = AnnotationGeometry.Rotate(
            new AnnotationPoint(20f, 80f), bubble.Bounds, 90f);

        var moved = (SpeechBubbleAnnotation)SelectionGeometry.MoveTail(bubble, pointerAtLocal);

        Assert.Equal(20f, moved.TailTip.X, 3);
        Assert.Equal(80f, moved.TailTip.Y, 3);
        Assert.Throws<ArgumentOutOfRangeException>(() => SelectionGeometry.MoveTail(
            new RectangleAnnotation { Id = Guid.NewGuid(), Bounds = new RectF(0, 0, 10, 10) },
            new AnnotationPoint(1f, 1f)));
    }

    [Fact]
    public void Serialization_RoundTripsEveryField()
    {
        var bubble = new SpeechBubbleAnnotation
        {
            Id = Guid.NewGuid(),
            Name = "bubble",
            IsVisible = false,
            IsLocked = true,
            RotationDegrees = 15f,
            Bounds = new RectF(5f, 6f, 120f, 60f),
            TailTip = new AnnotationPoint(0f, 96f),
            Text = "한글 العربية",
            FontFamily = "Malgun Gothic",
            FontSize = 20f,
            IsBold = true,
            IsItalic = true,
            ForegroundArgb = 0xFF10_2030,
            Alignment = AnnotationTextAlignment.Center,
            FillArgb = 0xEEFF_FFEE,
            StrokeArgb = 0xFFAA_0000,
            StrokeWidth = 3f,
            CornerRadius = 5f,
            Opacity = 0.8f,
        };
        var state = new DocumentState
        {
            Layers = [new AnnotationLayer
            {
                Id = AnnotationLayer.InitialLayerId,
                Annotations = [bubble],
            }],
        };

        var restored = DocumentStateSerializer.Read(DocumentStateSerializer.Write(state));

        Assert.Equal(bubble, Assert.IsType<SpeechBubbleAnnotation>(restored.Annotations[0]));
    }

    [Fact]
    public void Validator_RejectsHostileFields()
    {
        Assert.Throws<ArgumentException>(() => AnnotationValidator.Validate(
            Bubble(tail: new AnnotationPoint(float.NaN, 0f))));
        Assert.Throws<ArgumentException>(() => AnnotationValidator.Validate(
            Bubble(new RectF(0f, 0f, 0.5f, 40f))));
        Assert.Throws<ArgumentException>(() => AnnotationValidator.Validate(
            Bubble() with { FontFamily = " " }));
        Assert.Throws<ArgumentException>(() => AnnotationValidator.Validate(
            Bubble() with { Opacity = 1.5f }));
        Assert.Throws<ArgumentException>(() => AnnotationValidator.Validate(
            Bubble() with { StrokeWidth = 0f }));
        Assert.Throws<ArgumentException>(() => AnnotationValidator.Validate(
            Bubble() with { CornerRadius = -1f }));
        Assert.Throws<ArgumentException>(() => AnnotationValidator.Validate(
            Bubble() with { Text = new string('a', AnnotationValidator.MaxTextLength + 1) }));
    }

    [Fact]
    public void RetainedBytes_AreExact()
    {
        var bubble = Bubble() with { Name = "nm" };
        var expected = 48L + (2L * sizeof(char))
            + ((long)bubble.Text.Length * sizeof(char))
            + ((long)bubble.FontFamily.Length * sizeof(char)) + 64L;
        Assert.Equal(expected, bubble.EstimatedRetainedBytes);
    }
}
