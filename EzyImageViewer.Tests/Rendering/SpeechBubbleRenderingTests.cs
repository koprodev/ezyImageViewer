using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Rendering;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

/// <summary>
/// FR-ANNO-007 seam invariant: body and tail render as one unioned outline, so the pixels where
/// the tail base crosses the body edge are FILL, never stroke; representative interior/exterior
/// pixels pin the shape without depending on bit-exact antialiasing.
/// </summary>
public sealed class SpeechBubbleRenderingTests
{
    private static readonly SKColor Fill = new(0x00, 0xFF, 0x00, 0xFF);

    private static SKBitmap Draw(SpeechBubbleAnnotation bubble)
    {
        var bitmap = new SKBitmap(new SKImageInfo(
            64, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var state = new DocumentState
        {
            Layers = [new AnnotationLayer
            {
                Id = AnnotationLayer.InitialLayerId,
                Annotations = [bubble],
            }],
        };
        AnnotationRendering.DrawAnnotations(canvas, state, SKMatrix.Identity);
        return bitmap;
    }

    [Fact]
    public void SeamBetweenBodyAndTail_IsFillNotStroke()
    {
        // Bounds bottom edge y=25; tail base spans x 24..36 at y=23; tip (30,38).
        var bubble = new SpeechBubbleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(10f, 5f, 40f, 20f),
            TailTip = new AnnotationPoint(30f, 38f),
            Text = "",
            FillArgb = 0xFF00_FF00,
            StrokeArgb = 0xFFFF_0000,
            StrokeWidth = 2f,
        };
        using var bitmap = Draw(bubble);

        // The seam pixel on the body edge inside the tail span stays fill.
        Assert.Equal(Fill, bitmap.GetPixel(30, 25));
        // Body interior and tail interior are fill.
        Assert.Equal(Fill, bitmap.GetPixel(30, 15));
        Assert.Equal(Fill, bitmap.GetPixel(30, 30));
        // The bottom edge outside the tail span carries the stroke (red dominates).
        var edge = bitmap.GetPixel(15, 25);
        Assert.True(edge.Red > edge.Green, $"expected stroke at (15,25): {edge}");
        // Far outside stays untouched.
        Assert.Equal(default, bitmap.GetPixel(5, 40));
    }

    [Fact]
    public void CarriageReturnLineBreaks_RenderLikeLineFeeds()
    {
        // WinUI TextBox emits bare '\r'; the renderer must break lines on it like '\n'.
        static SpeechBubbleAnnotation Bubble(string text) => new()
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(2f, 2f, 60f, 60f),
            TailTip = new AnnotationPoint(32f, 32f),
            Text = text,
            FontSize = 12f,
            FillArgb = 0xFFFF_FFFF,
            StrokeArgb = 0xFFFF_0000,
            ForegroundArgb = 0xFF00_0000,
            StrokeWidth = 2f,
        };
        using var carriageReturn = Draw(Bubble("A\rB\rC"));
        using var lineFeed = Draw(Bubble("A\nB\nC"));
        using var singleLine = Draw(Bubble("ABC"));

        Assert.True(carriageReturn.Bytes.SequenceEqual(lineFeed.Bytes));
        // Guard that line splitting is visible at all, or the equality above proves nothing.
        Assert.False(carriageReturn.Bytes.SequenceEqual(singleLine.Bytes));
    }

    [Fact]
    public void TipInsideBody_RendersBodyOnly()
    {
        var bubble = new SpeechBubbleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(10f, 5f, 40f, 20f),
            TailTip = new AnnotationPoint(30f, 15f),
            Text = "",
            FillArgb = 0xFF00_FF00,
            StrokeArgb = 0xFFFF_0000,
            StrokeWidth = 2f,
        };
        using var bitmap = Draw(bubble);

        Assert.Equal(Fill, bitmap.GetPixel(30, 15));
        // Below the body there is no tail to paint.
        Assert.Equal(default, bitmap.GetPixel(30, 32));
    }
}
