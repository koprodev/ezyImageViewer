using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Rendering;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

/// <summary>말풍선 몸통·꼬리 접합선이 선이 아닌 채우기로 남는 렌더 계약.</summary>
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
        // 몸통 아래 y=25, 꼬리 밑변 y=23의 x 24..36, 끝점 (30,38).
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

        // 꼬리 범위 안 몸통 경계 접합 픽셀은 채우기 유지.
        Assert.Equal(Fill, bitmap.GetPixel(30, 25));
        // 몸통·꼬리 내부는 채우기.
        Assert.Equal(Fill, bitmap.GetPixel(30, 15));
        Assert.Equal(Fill, bitmap.GetPixel(30, 30));
        // 꼬리 밖 아래 변은 선이 지배.
        var edge = bitmap.GetPixel(15, 25);
        Assert.True(edge.Red > edge.Green, $"expected stroke at (15,25): {edge}");
        // 멀리 바깥은 그대로.
        Assert.Equal(default, bitmap.GetPixel(5, 40));
    }

    [Fact]
    public void CarriageReturnLineBreaks_RenderLikeLineFeeds()
    {
        // WinUI TextBox의 '\r'도 '\n'처럼 줄바꿈 처리.
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
        // 줄 분리가 실제 보이는지 확인해야 위 동등성도 의미가 있음.
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
        // 몸통 아래에 그릴 꼬리 없음.
        Assert.Equal(default, bitmap.GetPixel(30, 32));
    }
}
