using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Rendering;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

/// <summary>축소 미리보기 주석이 전체 해상도 재디코드 뒤에도 같은 곳에 놓이는 좌표 검증.</summary>
public class AnnotationRenderingTests
{
    [Fact]
    public void FullDecode_MapsNativePixelsOneToOne()
    {
        var matrix = AnnotationRendering.NativeToContent(new PixelSize(400, 300), 400, 300);

        Assert.Equal(new SKPoint(100, 60), matrix.MapPoint(new SKPoint(100, 60)));
    }

    [Fact]
    public void ReducedPreview_ScalesNativeCoordinatesOntoTheSmallerFrame()
    {
        // 4000x3000 원본을 1000x750 미리보기로 열면 양축 1/4.
        var matrix = AnnotationRendering.NativeToContent(new PixelSize(4000, 3000), 1000, 750);

        var mapped = matrix.MapPoint(new SKPoint(2000, 1500));

        Assert.Equal(500f, mapped.X, 3);
        Assert.Equal(375f, mapped.Y, 3);
    }

    [Fact]
    public void ReducedPreviewMapping_RoundTripsBackToNative()
    {
        var matrix = AnnotationRendering.NativeToContent(new PixelSize(4000, 3000), 1000, 750);
        Assert.True(matrix.TryInvert(out var inverse));

        // 미리보기에서 만든 주석이 같은 원본 픽셀로 돌아가 전체 해상도에서도 좌표 재사용.
        var native = inverse.MapPoint(matrix.MapPoint(new SKPoint(1234, 567)));

        Assert.Equal(1234f, native.X, 2);
        Assert.Equal(567f, native.Y, 2);
    }

    [Fact]
    public void NonUniformDecoderRounding_ScalesEachAxisIndependently()
    {
        // 디코더가 각 변을 반올림해 축 비율이 항상 같지는 않음.
        var matrix = AnnotationRendering.NativeToContent(new PixelSize(1000, 1000), 501, 500);

        var mapped = matrix.MapPoint(new SKPoint(1000, 1000));

        Assert.Equal(501f, mapped.X, 3);
        Assert.Equal(500f, mapped.Y, 3);
    }

    [Fact]
    public void DegenerateSizes_FallBackToIdentityRatherThanDividingByZero()
    {
        Assert.Equal(SKMatrix.Identity, AnnotationRendering.NativeToContent(new PixelSize(0, 0), 100, 100));
        Assert.Equal(SKMatrix.Identity, AnnotationRendering.NativeToContent(new PixelSize(100, 100), 0, 100));
    }

    [Fact]
    public void DrawAnnotations_PaintsTheObjectAtItsMappedLocation()
    {
        var state = DocumentState.Empty.AddAnnotation(new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(10, 10, 20, 20),
            StrokeArgb = 0xFF00FF00,
            StrokeWidth = 4f,
        });

        using var bitmap = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);
            AnnotationRendering.DrawAnnotations(canvas, state, SKMatrix.Identity);
        }

        // y=10 가장자리에 중심을 둔 선, 내부는 그대로.
        Assert.Equal(SKColors.Black, bitmap.GetPixel(20, 20));
        Assert.NotEqual(SKColors.Black, bitmap.GetPixel(20, 10));
    }

    [Fact]
    public void DrawAnnotations_KeepsLockedLayersPainted_AndSkipsHiddenOnes()
    {
        var state = DocumentState.Empty.AddAnnotation(new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(10, 10, 20, 20),
            StrokeArgb = 0xFF00FF00,
            StrokeWidth = 4f,
        });
        var locked = state.ReplaceLayer(state.Layers[0] with { IsLocked = true });
        var hidden = state.ReplaceLayer(state.Layers[0] with { IsVisible = false });

        // 잠금은 편집만 막고 표시는 유지. 숨김만 그리기 제거.
        Assert.NotEqual(SKColors.Black, RenderPixel(locked, 20, 10));
        Assert.Equal(SKColors.Black, RenderPixel(hidden, 20, 10));
    }

    private static SKColor RenderPixel(DocumentState state, int x, int y)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);
            AnnotationRendering.DrawAnnotations(canvas, state, SKMatrix.Identity);
        }
        return bitmap.GetPixel(x, y);
    }

    [Fact]
    public void DrawAnnotations_OnAnEmptyLayer_TouchesNothing()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Black);
            AnnotationRendering.DrawAnnotations(canvas, DocumentState.Empty, SKMatrix.Identity);
        }

        Assert.Equal(SKColors.Black, bitmap.GetPixel(4, 4));
    }
}
