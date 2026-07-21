using System.Collections.Immutable;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Documents.Serialization;
using Xunit;

namespace EzyImageViewer.Tests.Documents;

public class AnnotationSerializationTests
{
    [Fact]
    public void AllM4Kinds_RoundTripTheirCommonAndSpecificFields()
    {
        Annotation[] annotations =
        [
            new InkAnnotation
            {
                Id = Guid.NewGuid(), Name = "pen", IsLocked = true, RotationDegrees = 3f,
                Points = [new(1, 2), new(3, 4)], Kind = InkKind.Highlighter,
                StrokeArgb = 0xFF12_3456, StrokeWidth = 12f, Opacity = 0.35f,
            },
            new LineAnnotation
            {
                Id = Guid.NewGuid(), Name = "arrow", Start = new(5, 6), End = new(7, 8),
                StartArrowhead = ArrowheadKind.Open, EndArrowhead = ArrowheadKind.Triangle,
                StrokeArgb = 0xFF65_4321, StrokeWidth = 4f, Opacity = 0.8f,
            },
            new RectangleAnnotation
            {
                Id = Guid.NewGuid(), Name = "ellipse", Bounds = new RectF(1, 2, 30, 40),
                Shape = ShapeKind.Ellipse, FillArgb = 0x8011_2233,
                CornerRadius = 6f, Opacity = 0.6f,
            },
            new TextAnnotation
            {
                Id = Guid.NewGuid(), Name = "text", Bounds = new RectF(5, 6, 200, 80),
                Text = "한글 العربية", FontFamily = "Malgun Gothic", FontSize = 28f,
                IsBold = true, IsItalic = true, ForegroundArgb = 0xFF01_0203,
                BackgroundArgb = 0x4004_0506, Alignment = AnnotationTextAlignment.Center,
                Opacity = 0.9f,
            },
            new NumberMarkerAnnotation
            {
                Id = Guid.NewGuid(), Name = "number", IsVisible = false,
                Bounds = new RectF(9, 10, 32, 32), Number = 42,
                FillArgb = 0xFFAA_BBCC, ForegroundArgb = 0xFF11_2233,
                FontSize = 17f, Opacity = 0.75f,
            },
        ];
        var state = new DocumentState
        {
            Layers = [new AnnotationLayer { Id = AnnotationLayer.InitialLayerId, Annotations = annotations }],
        };

        var restored = DocumentStateSerializer.Read(DocumentStateSerializer.Write(state));

        Assert.Equal(annotations.Length, restored.Annotations.Count);
        Assert.Equal(annotations[1..], restored.Annotations.Skip(1));
        var expectedInk = Assert.IsType<InkAnnotation>(annotations[0]);
        var actualInk = Assert.IsType<InkAnnotation>(restored.Annotations[0]);
        Assert.Equal(expectedInk with { Points = ImmutableArray<AnnotationPoint>.Empty },
            actualInk with { Points = ImmutableArray<AnnotationPoint>.Empty });
        Assert.Equal(expectedInk.Points.ToArray(), actualInk.Points.ToArray());
    }

    [Fact]
    public void LegacyRectangleWithoutM4Fields_UsesCompatibleDefaults()
    {
        var id = Guid.NewGuid();
        var json = $$"""
            {"transform":[],"annotations":[{"kind":"rectangle","id":"{{id}}",
            "x":1,"y":2,"width":3,"height":4,"strokeArgb":4294901760,"strokeWidth":2}]}
            """;

        var rectangle = Assert.IsType<RectangleAnnotation>(
            Assert.Single(DocumentStateSerializer.Read(json).Annotations));

        Assert.Equal(ShapeKind.Rectangle, rectangle.Shape);
        Assert.True(rectangle.IsVisible);
        Assert.Null(rectangle.FillArgb);
        Assert.Equal(1f, rectangle.Opacity);
    }

    [Fact]
    public void UnknownAnnotationEnum_FailsTheRead()
    {
        var id = Guid.NewGuid();
        var json = $$"""
            {"transform":[],"annotations":[{"kind":"rectangle","id":"{{id}}",
            "x":1,"y":2,"width":3,"height":4,"strokeArgb":1,"strokeWidth":2,"shape":999}]}
            """;

        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(json));
    }

    [Fact]
    public void NullInkPoint_AndMissingRequiredField_FailTheRead()
    {
        var id = Guid.NewGuid();
        var nullPoint = $$"""
            {"transform":[],"annotations":[{"kind":"ink","id":"{{id}}","points":[null],
            "inkKind":0,"strokeArgb":1,"strokeWidth":2,"opacity":1}]}
            """;
        var missingPoints = $$"""
            {"transform":[],"annotations":[{"kind":"ink","id":"{{id}}",
            "inkKind":0,"strokeArgb":1,"strokeWidth":2,"opacity":1}]}
            """;

        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(nullPoint));
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(missingPoints));
    }

    [Fact]
    public void WriteRejectsAStateThatBypassedMutationValidation()
    {
        var invalid = new TextAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(0, 0, 10, 10),
            Text = new string('x', AnnotationValidator.MaxTextLength + 1),
        };
        var state = new DocumentState
        {
            Layers = [new AnnotationLayer { Id = AnnotationLayer.InitialLayerId, Annotations = [invalid] }],
        };

        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Write(state));
    }
}
