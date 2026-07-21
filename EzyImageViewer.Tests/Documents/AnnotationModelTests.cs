using System.Collections.Immutable;
using EzyImageViewer.Core.Commands;
using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using Xunit;

namespace EzyImageViewer.Tests.Documents;

public class AnnotationModelTests
{
    [Fact]
    public void EveryM4AnnotationKind_PassesTheSharedValidator()
    {
        Annotation[] annotations =
        [
            new InkAnnotation
            {
                Id = Guid.NewGuid(),
                Points = [new(0, 0), new(10, 10)],
            },
            new LineAnnotation
            {
                Id = Guid.NewGuid(),
                Start = new(0, 0),
                End = new(10, 10),
                EndArrowhead = ArrowheadKind.Triangle,
            },
            new RectangleAnnotation
            {
                Id = Guid.NewGuid(),
                Bounds = new RectF(0, 0, 10, 10),
                Shape = ShapeKind.RoundedRectangle,
            },
            new TextAnnotation
            {
                Id = Guid.NewGuid(),
                Bounds = new RectF(0, 0, 100, 40),
                Text = "한글 text",
            },
            new NumberMarkerAnnotation
            {
                Id = Guid.NewGuid(),
                Bounds = new RectF(0, 0, 30, 30),
                Number = 1,
            },
        ];

        Assert.All(annotations, annotation =>
            Assert.Same(annotation, AnnotationValidator.Validate(annotation)));
    }

    [Fact]
    public void Validator_RejectsNonFiniteGeometryAndUnknownEnums()
    {
        var invalidPoint = new LineAnnotation
        {
            Id = Guid.NewGuid(),
            Start = new(float.NaN, 0),
            End = new(1, 1),
        };
        var invalidShape = new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(0, 0, 10, 10),
            Shape = (ShapeKind)999,
        };

        Assert.Throws<ArgumentException>(() => AnnotationValidator.Validate(invalidPoint));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnnotationValidator.Validate(invalidShape));
    }

    [Fact]
    public void Validator_EnforcesTextAndInkCaps()
    {
        var text = new TextAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(0, 0, 10, 10),
            Text = new string('x', AnnotationValidator.MaxTextLength + 1),
        };
        var points = Enumerable.Range(0, AnnotationValidator.MaxInkPoints + 1)
            .Select(i => new AnnotationPoint(i, i))
            .ToImmutableArray();
        var ink = new InkAnnotation { Id = Guid.NewGuid(), Points = points };

        Assert.Throws<ArgumentException>(() => AnnotationValidator.Validate(text));
        Assert.Throws<ArgumentException>(() => AnnotationValidator.Validate(ink));
    }

    [Fact]
    public void WithBounds_RetargetsLineAndInkGeometry()
    {
        var line = new LineAnnotation
        {
            Id = Guid.NewGuid(),
            Start = new(10, 20),
            End = new(30, 40),
        };
        var ink = new InkAnnotation
        {
            Id = Guid.NewGuid(),
            Points = [new(10, 20), new(20, 30), new(30, 40)],
        };
        var target = new RectF(100, 200, 40, 80);

        var movedLine = Assert.IsType<LineAnnotation>(line.WithBounds(target));
        var movedInk = Assert.IsType<InkAnnotation>(ink.WithBounds(target));

        Assert.Equal(new AnnotationPoint(100, 200), movedLine.Start);
        Assert.Equal(new AnnotationPoint(140, 280), movedLine.End);
        Assert.Equal(target, movedInk.Bounds);
        Assert.Equal(new AnnotationPoint(120, 240), movedInk.Points[1]);
    }

    [Fact]
    public void HitTest_IsGeometryAwareAndHonorsLayerState()
    {
        var ellipse = new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(0, 0, 100, 20),
            Shape = ShapeKind.Ellipse,
            RotationDegrees = 90f,
        };
        var hidden = ellipse with { Id = Guid.NewGuid(), IsVisible = false };
        var locked = ellipse with { Id = Guid.NewGuid(), IsLocked = true };
        var state = DocumentState.Empty
            .AddAnnotation(ellipse)
            .AddAnnotation(hidden)
            .AddAnnotation(locked);

        Assert.Equal(ellipse.Id, state.HitTest(50, 50)?.Id);
        Assert.Null(state.HitTest(10, 10));
    }

    [Fact]
    public void ReplaceCommand_VerifiesEndpointsAndRevertsExactly()
    {
        var before = new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(0, 0, 10, 10),
        };
        var after = before with { StrokeArgb = 0xFF00_FF00, RotationDegrees = 15f };
        var command = new ReplaceAnnotationCommand(
            AnnotationEditKind.Style, before, after, gestureId: 7);
        var initial = DocumentState.Empty.AddAnnotation(before);

        var applied = command.Apply(initial);
        Assert.Same(after, applied.Find(before.Id));
        Assert.Same(before, command.Revert(applied).Find(before.Id));
        Assert.NotNull(command.MergeKey);
        Assert.Throws<InvalidOperationException>(() => command.Apply(applied));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReplaceAnnotationCommand(
            (AnnotationEditKind)999, before, after));
    }

    [Fact]
    public void ReplaceCommand_CoalescesOneGestureIntoOneUndoStep()
    {
        using var document = new ImageDocument
        {
            Frame = new DecodedFrame(new byte[16], 2, 2, 8, hasAlpha: false),
            Source = DocumentSource.FromClipboard(),
            NativeSize = new PixelSize(2, 2),
        };
        var editor = new DocumentEditor();
        editor.Reset(document);
        var before = new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(0, 0, 10, 10),
        };
        editor.Apply(new AddAnnotationCommand(before));
        var middle = before with { StrokeWidth = 5f };
        var after = before with { StrokeWidth = 9f };
        editor.Apply(new ReplaceAnnotationCommand(
            AnnotationEditKind.Style, before, middle, gestureId: 12));
        editor.ApplyCoalesced(new ReplaceAnnotationCommand(
            AnnotationEditKind.Style, before, after, gestureId: 12));

        Assert.Same(after, editor.State.Find(before.Id));
        Assert.True(editor.Undo());
        Assert.Same(before, editor.State.Find(before.Id));
    }

    [Fact]
    public void State_RejectsDuplicateIds()
    {
        var annotation = new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(0, 0, 10, 10),
        };
        var state = DocumentState.Empty.AddAnnotation(annotation);

        Assert.Throws<InvalidOperationException>(() => state.AddAnnotation(annotation));
    }

    [Fact]
    public void MarkerNumbering_UsesMaximumPlusOneWithoutReusingGaps()
    {
        Annotation[] annotations =
        [
            new NumberMarkerAnnotation
            {
                Id = Guid.NewGuid(), Bounds = new RectF(0, 0, 10, 10), Number = 1,
            },
            new NumberMarkerAnnotation
            {
                Id = Guid.NewGuid(), Bounds = new RectF(0, 0, 10, 10), Number = 3,
            },
        ];

        Assert.True(AnnotationNumbering.TryGetNextMarkerNumber(annotations, out var number));
        Assert.Equal(4, number);
    }

    [Fact]
    public void MarkerNumbering_RefusesIntegerOverflow()
    {
        Annotation[] annotations =
        [
            new NumberMarkerAnnotation
            {
                Id = Guid.NewGuid(), Bounds = new RectF(0, 0, 10, 10), Number = int.MaxValue,
            },
        ];

        Assert.False(AnnotationNumbering.TryGetNextMarkerNumber(annotations, out var number));
        Assert.Equal(0, number);
    }
}

public class InkSimplifierTests
{
    [Fact]
    public void StraightStroke_CollapsesToEndpoints()
    {
        AnnotationPoint[] points =
            [new(0, 0), new(1, 0.05f), new(2, -0.05f), new(3, 0)];

        var simplified = InkSimplifier.Simplify(points, 0.1f);

        Assert.Equal(2, simplified.Length);
        Assert.Equal(points[0], simplified[0]);
        Assert.Equal(points[^1], simplified[^1]);
    }

    [Fact]
    public void CornerBeyondTolerance_IsPreserved()
    {
        AnnotationPoint[] points = [new(0, 0), new(10, 0), new(10, 10)];

        var simplified = InkSimplifier.Simplify(points, 0.5f);

        Assert.Equal(points, simplified);
    }

    [Fact]
    public void InvalidTolerance_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            InkSimplifier.Simplify([new(0, 0)], float.NaN));
    }
}
