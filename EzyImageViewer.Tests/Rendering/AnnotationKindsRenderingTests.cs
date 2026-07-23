using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Rendering;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

public class AnnotationKindsRenderingTests
{
    public static IEnumerable<object[]> EveryKind()
    {
        yield return
        [
            new InkAnnotation
            {
                Id = Guid.NewGuid(), Points = [new(5, 10), new(50, 10)],
                StrokeWidth = 4f,
            },
        ];
        yield return
        [
            new LineAnnotation
            {
                Id = Guid.NewGuid(), Start = new(5, 20), End = new(50, 20),
                EndArrowhead = ArrowheadKind.Triangle,
            },
        ];
        yield return
        [
            new RectangleAnnotation
            {
                Id = Guid.NewGuid(), Bounds = new RectF(5, 5, 40, 25),
                Shape = ShapeKind.RoundedRectangle, FillArgb = 0xFF00_FF00,
            },
        ];
        yield return
        [
            new RectangleAnnotation
            {
                Id = Guid.NewGuid(), Bounds = new RectF(5, 5, 40, 25),
                Shape = ShapeKind.Ellipse, FillArgb = 0xFF00_00FF,
            },
        ];
        yield return
        [
            new TextAnnotation
            {
                Id = Guid.NewGuid(), Bounds = new RectF(2, 2, 110, 45),
                Text = "한글 العربية", FontFamily = "Malgun Gothic", FontSize = 22f,
            },
        ];
        yield return
        [
            new NumberMarkerAnnotation
            {
                Id = Guid.NewGuid(), Bounds = new RectF(10, 10, 36, 36), Number = 12,
            },
        ];
        yield return
        [
            new SpeechBubbleAnnotation
            {
                Id = Guid.NewGuid(), Bounds = new RectF(5, 5, 50, 25),
                TailTip = new AnnotationPoint(15, 45), Text = "말풍선",
            },
        ];
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void EveryM4Kind_ProducesPixels(Annotation annotation)
    {
        using var bitmap = Draw(annotation);

        Assert.True(CountNonTransparent(bitmap) > 0);
    }

    [Fact]
    public void HiddenObject_DoesNotPaint()
    {
        var hidden = new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(0, 0, 32, 32),
            FillArgb = 0xFFFF_0000,
            IsVisible = false,
        };

        using var bitmap = Draw(hidden);

        Assert.Equal(0, CountNonTransparent(bitmap));
    }

    [Fact]
    public void ObjectOpacity_MultipliesTheColorAlphaOnce()
    {
        var shape = new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(5, 5, 40, 40),
            FillArgb = 0x80FF_0000,
            StrokeArgb = 0x0000_0000,
            Opacity = 0.5f,
        };

        using var bitmap = Draw(shape);

        Assert.InRange(bitmap.GetPixel(20, 20).Alpha, (byte)63, (byte)65);
    }

    [Fact]
    public void LiveInkDraft_RendersDirectlyFromThePointerBuffer()
    {
        AnnotationPoint[] points = [new(5, 5), new(30, 15), new(55, 5)];
        using var bitmap = new SKBitmap(
            new SKImageInfo(64, 32, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            AnnotationRendering.DrawInkDraft(
                canvas, points, SKMatrix.Identity, 0xFFFF0000, 4f, 0.5f);
        }

        Assert.True(CountNonTransparent(bitmap) > 0);
    }

    [Fact]
    public void RotatedSelectionAndObject_RenderWithoutUsingAxisAlignedGeometry()
    {
        var shape = new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(30, 10, 40, 10),
            FillArgb = 0xFFFF_0000,
            RotationDegrees = 90f,
        };
        var state = DocumentState.Empty.AddAnnotation(shape);
        using var bitmap = new SKBitmap(
            new SKImageInfo(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            AnnotationRendering.DrawAnnotations(canvas, state, SKMatrix.Identity, shape.Id);
        }

        Assert.NotEqual((byte)0, bitmap.GetPixel(50, 30).Alpha);
        Assert.Equal((byte)0, bitmap.GetPixel(31, 11).Alpha);
    }

    private static SKBitmap Draw(Annotation annotation)
    {
        var bitmap = new SKBitmap(
            new SKImageInfo(128, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        AnnotationRendering.DrawAnnotations(
            canvas, DocumentState.Empty.AddAnnotation(annotation), SKMatrix.Identity);
        return bitmap;
    }

    private static int CountNonTransparent(SKBitmap bitmap)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).Alpha != 0)
                    count++;
            }
        }
        return count;
    }
}
