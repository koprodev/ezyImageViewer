using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Rendering;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

/// <summary>공유 합성 경로의 CPU 골든. 정수 변환은 정확한 픽셀, 자유 각도는 구조로 검증.</summary>
public class DocumentCompositeTests
{
    private static readonly SKColor TL = new(0xFF, 0x00, 0x00, 0xFF); // 빨강.
    private static readonly SKColor TR = new(0x00, 0xFF, 0x00, 0xFF); // 초록.
    private static readonly SKColor BL = new(0x00, 0x00, 0xFF, 0xFF); // 파랑.
    private static readonly SKColor BR = new(0xFF, 0xFF, 0x00, 0xFF); // 노랑.

    /// <summary>회전·뒤집기에서 방향을 구분하는 네 색 사분면.</summary>
    private static SKImage QuadrantImage(int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        var w = width / 2f;
        var h = height / 2f;
        using var paint = new SKPaint();
        paint.Color = TL;
        canvas.DrawRect(SKRect.Create(0, 0, w, h), paint);
        paint.Color = TR;
        canvas.DrawRect(SKRect.Create(w, 0, w, h), paint);
        paint.Color = BL;
        canvas.DrawRect(SKRect.Create(0, h, w, h), paint);
        paint.Color = BR;
        canvas.DrawRect(SKRect.Create(w, h, w, h), paint);
        return surface.Snapshot();
    }

    private static DocumentState State(params TransformOp[] ops)
    {
        var transform = BackgroundTransform.Identity;
        foreach (var op in ops)
            transform = transform.Append(op);
        return new DocumentState { Transform = transform };
    }

    private static SKBitmap Render(SKImage frame, PixelSize native, DocumentState state, Guid selectedId = default)
    {
        var evaluation = TransformEvaluator.Evaluate(state.Transform, native);
        var info = new SKImageInfo(
            evaluation.OutputSize.Width, evaluation.OutputSize.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Transparent);
        DocumentComposite.Render(surface.Canvas, frame, native, state, evaluation, SKMatrix.Identity, selectedId);
        var bitmap = new SKBitmap(info);
        Assert.True(surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0));
        return bitmap;
    }

    [Fact]
    public void Identity_ReproducesTheQuadrants()
    {
        using var frame = QuadrantImage(8, 8);
        using var bitmap = Render(frame, new PixelSize(8, 8), State());

        Assert.Equal(TL, bitmap.GetPixel(2, 2));
        Assert.Equal(TR, bitmap.GetPixel(6, 2));
        Assert.Equal(BL, bitmap.GetPixel(2, 6));
        Assert.Equal(BR, bitmap.GetPixel(6, 6));
    }

    [Fact]
    public void Erase_PunchesTheBackgroundToTransparent()
    {
        using var frame = QuadrantImage(8, 8);
        using var bitmap = Render(
            frame, new PixelSize(8, 8), State(new EraseOp(new RectF(0f, 0f, 4f, 4f))));

        Assert.Equal(SKColors.Empty, bitmap.GetPixel(2, 2)); // 왼쪽 위 사분면 지움.
        Assert.Equal(TR, bitmap.GetPixel(6, 2));
        Assert.Equal(BL, bitmap.GetPixel(2, 6));
        Assert.Equal(BR, bitmap.GetPixel(6, 6));
    }

    [Fact]
    public void Rotate90_MovesQuadrantsClockwiseExactly()
    {
        using var frame = QuadrantImage(8, 8);
        using var bitmap = Render(frame, new PixelSize(8, 8), State(new RotateOp(90f)));

        Assert.Equal(8, bitmap.Width);
        Assert.Equal(8, bitmap.Height);
        Assert.Equal(BL, bitmap.GetPixel(2, 2)); // 시계 방향으로 왼쪽 아래가 왼쪽 위 도착.
        Assert.Equal(TL, bitmap.GetPixel(6, 2));
        Assert.Equal(BR, bitmap.GetPixel(2, 6));
        Assert.Equal(TR, bitmap.GetPixel(6, 6));
    }

    [Fact]
    public void Rotate90_OnANonSquareImage_SwapsTheCanvas()
    {
        using var frame = QuadrantImage(8, 4);
        using var bitmap = Render(frame, new PixelSize(8, 4), State(new RotateOp(90f)));

        Assert.Equal(4, bitmap.Width);
        Assert.Equal(8, bitmap.Height);
        Assert.Equal(BL, bitmap.GetPixel(1, 2));
        Assert.Equal(TR, bitmap.GetPixel(2, 6));
    }

    [Fact]
    public void FlipHorizontal_MirrorsColumnsExactly()
    {
        using var frame = QuadrantImage(8, 8);
        using var bitmap = Render(frame, new PixelSize(8, 8), State(new FlipOp(Horizontal: true)));

        Assert.Equal(TR, bitmap.GetPixel(2, 2));
        Assert.Equal(TL, bitmap.GetPixel(6, 2));
        Assert.Equal(BR, bitmap.GetPixel(2, 6));
        Assert.Equal(BL, bitmap.GetPixel(6, 6));
    }

    [Fact]
    public void FlipVertical_MirrorsRowsExactly()
    {
        using var frame = QuadrantImage(8, 8);
        using var bitmap = Render(frame, new PixelSize(8, 8), State(new FlipOp(Horizontal: false)));

        Assert.Equal(BL, bitmap.GetPixel(2, 2));
        Assert.Equal(TL, bitmap.GetPixel(2, 6));
    }

    [Fact]
    public void Crop_KeepsExactlyTheRegion()
    {
        using var frame = QuadrantImage(8, 8);
        using var bitmap = Render(frame, new PixelSize(8, 8), State(new CropOp(new RectF(4f, 0f, 4f, 8f))));

        Assert.Equal(4, bitmap.Width);
        Assert.Equal(8, bitmap.Height);
        Assert.Equal(TR, bitmap.GetPixel(2, 2));
        Assert.Equal(BR, bitmap.GetPixel(2, 6));
    }

    [Fact]
    public void IntegerUpscale_KeepsQuadrantInteriorsPure()
    {
        using var frame = QuadrantImage(8, 8);
        using var bitmap = Render(frame, new PixelSize(8, 8), State(new ResizeOp(new PixelSize(16, 16))));

        Assert.Equal(16, bitmap.Width);
        Assert.Equal(TL, bitmap.GetPixel(4, 4));
        Assert.Equal(TR, bitmap.GetPixel(12, 4));
        Assert.Equal(BL, bitmap.GetPixel(4, 12));
        Assert.Equal(BR, bitmap.GetPixel(12, 12));
    }

    [Fact]
    public void CropThenRotate_ComposesInOrder()
    {
        // 오른쪽 절반(초록·노랑)만 남겨 90° 시계 회전: 노랑 왼쪽, 초록 오른쪽.
        using var frame = QuadrantImage(8, 8);
        using var bitmap = Render(frame, new PixelSize(8, 8),
            State(new CropOp(new RectF(4f, 0f, 4f, 8f)), new RotateOp(90f)));

        Assert.Equal(8, bitmap.Width);
        Assert.Equal(4, bitmap.Height);
        Assert.Equal(BR, bitmap.GetPixel(2, 2));
        Assert.Equal(TR, bitmap.GetPixel(6, 2));
    }

    [Fact]
    public void ReducedPreviewFrame_FillsTheNativeSizedOutput()
    {
        // 절반 해상도 프레임도 합성이 축소를 되돌려 출력·주석 좌표는 원본 기준 유지.
        using var frame = QuadrantImage(4, 4);
        using var bitmap = Render(frame, new PixelSize(8, 8), State());

        Assert.Equal(8, bitmap.Width);
        Assert.Equal(TL, bitmap.GetPixel(2, 2));
        Assert.Equal(BR, bitmap.GetPixel(6, 6));
    }

    [Fact]
    public void FreeAngle_CornersAreTransparent_InteriorIsOpaque()
    {
        using var frame = QuadrantImage(100, 100);
        using var bitmap = Render(frame, new PixelSize(100, 100), State(new RotateOp(45f)));

        Assert.Equal(142, bitmap.Width); // 내용을 담는 바깥쪽 반올림.
        Assert.Equal(142, bitmap.Height);
        Assert.Equal(0, bitmap.GetPixel(2, 2).Alpha); // 경계 상자 모서리에는 원본 없음.
        Assert.Equal(0, bitmap.GetPixel(139, 139).Alpha);
        Assert.Equal(255, bitmap.GetPixel(71, 71).Alpha); // 원본 중심.
    }

    [Fact]
    public void OversizedDestination_PaintsNothingOutsideTheOutputCanvas()
    {
        // 큰 미리보기와 출력 크기 내보내기가 같도록 합성은 출력 크기에 명시적으로 자름.
        var state = State(new RotateOp(45f));
        var evaluation = TransformEvaluator.Evaluate(state.Transform, new PixelSize(100, 100));
        var info = new SKImageInfo(
            evaluation.OutputSize.Width + 60, evaluation.OutputSize.Height + 60,
            SKColorType.Bgra8888, SKAlphaType.Premul);
        using var frame = QuadrantImage(100, 100);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Transparent);
        DocumentComposite.Render(surface.Canvas, frame, new PixelSize(100, 100), state, evaluation, SKMatrix.Identity);
        var bitmap = new SKBitmap(info);
        Assert.True(surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0));

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (x >= evaluation.OutputSize.Width || y >= evaluation.OutputSize.Height)
                    Assert.Equal(0, bitmap.GetPixel(x, y).Alpha);
            }
        }
        bitmap.Dispose();
    }

    [Fact]
    public void Annotations_ShareTheBackgroundTransform()
    {
        // 원본 왼쪽 선은 가로 뒤집기 뒤 오른쪽으로 이동.
        var annotationId = Guid.NewGuid();
        var state = State(new FlipOp(Horizontal: true)).AddAnnotation(new RectangleAnnotation
        {
            Id = annotationId,
            Bounds = new RectF(0f, 24f, 16f, 16f),
            StrokeArgb = 0xFFFF00FF, // 표본에 없는 자홍.
            StrokeWidth = 2f,
        });
        using var frame = QuadrantImage(64, 64);
        using var bitmap = Render(frame, new PixelSize(64, 64), state);

        Assert.True(CountColor(bitmap, new SKColor(0xFF, 0x00, 0xFF, 0xFF), 32, 64) > 0, "stroke must land on the flipped side");
        Assert.Equal(0, CountColor(bitmap, new SKColor(0xFF, 0x00, 0xFF, 0xFF), 0, 24));
    }

    [Fact]
    public void AnnotationOutsideEveryCrop_DoesNotRender()
    {
        // 비파괴라 데이터는 남고 공유 클립 때문에 픽셀만 안 보임.
        var state = State(new CropOp(new RectF(0f, 0f, 32f, 64f))).AddAnnotation(new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(40f, 8f, 16f, 16f), // 잘린 오른쪽 절반 안.
            StrokeArgb = 0xFFFF00FF,
            StrokeWidth = 2f,
        });
        using var frame = QuadrantImage(64, 64);
        using var bitmap = Render(frame, new PixelSize(64, 64), state);

        Assert.Single(state.Annotations); // 문서에는 보존.
        Assert.Equal(0, CountColor(bitmap, new SKColor(0xFF, 0x00, 0xFF, 0xFF), 0, bitmap.Width));
    }

    [Fact]
    public void SelectionRotateHandle_RemainsVisibleOutsideTheSourceClip()
    {
        var annotation = new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(5f, 0f, 10f, 10f),
            StrokeArgb = 0xFFFF00FF,
        };
        var state = State().AddAnnotation(annotation);
        using var frame = QuadrantImage(32, 32);
        using var target = new SKBitmap(new SKImageInfo(92, 92));
        using var canvas = new SKCanvas(target);
        canvas.Clear(SKColors.Transparent);

        DocumentComposite.Render(
            canvas, frame, new PixelSize(32, 32), state,
            TransformEvaluator.Evaluate(state.Transform, new PixelSize(32, 32)),
            SKMatrix.CreateTranslation(30f, 30f), annotation.Id);

        Assert.NotEqual((byte)0, target.GetPixel(40, 6).Alpha);
    }

    [Fact]
    public void AnnotationStrokeWidth_ScalesWithTheImage()
    {
        // 원본 2px 선은 4배 크기 조정 뒤 약 4배 행을 덮음.
        var state = State(new ResizeOp(new PixelSize(256, 256))).AddAnnotation(new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(16f, 16f, 32f, 32f),
            StrokeArgb = 0xFFFF00FF,
            StrokeWidth = 2f,
        });
        using var frame = QuadrantImage(64, 64);
        using var bitmap = Render(frame, new PixelSize(64, 64), state);

        // 위쪽 변 중간을 지나는 세로선의 자홍 픽셀 수 계산.
        var column = 128; // 원본 x=32 → 출력 128.
        var hits = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var pixel = bitmap.GetPixel(column, y);
            if (pixel.Red > 200 && pixel.Blue > 200 && pixel.Green < 60)
                hits++;
        }
        // 위·아래 두 변 × 장치 약 8px. 안티앨리어싱이 경계 행을 흐림.
        Assert.InRange(hits, 10, 24);
    }

    [Fact]
    public void AnnotationStroke_DeformsWithANonUniformResize()
    {
        // x4/y1 조정에서 세로 변은 약 8px, 가로 변은 약 2px. 단일 너비면 한 축이 틀림.
        var state = State(new ResizeOp(new PixelSize(256, 64))).AddAnnotation(new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(16f, 16f, 32f, 32f),
            StrokeArgb = 0xFFFF00FF,
            StrokeWidth = 2f,
        });
        using var frame = QuadrantImage(64, 64);
        using var bitmap = Render(frame, new PixelSize(64, 64), state);

        static bool IsMagenta(SKColor pixel) => pixel.Red > 200 && pixel.Blue > 200 && pixel.Green < 60;

        // 원본 y=32 가로선은 세로 변 둘과 교차: 2 × 약 8px.
        var horizontalHits = 0;
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (IsMagenta(bitmap.GetPixel(x, 32)))
                horizontalHits++;
        }
        Assert.InRange(horizontalHits, 12, 24);

        // 원본 x=32 세로선은 가로 변 둘과 교차: 2 × 약 2px.
        var verticalHits = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            if (IsMagenta(bitmap.GetPixel(128, y)))
                verticalHits++;
        }
        Assert.InRange(verticalHits, 2, 8);
    }

    private static int CountColor(SKBitmap bitmap, SKColor color, int fromX, int toX)
    {
        var count = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = fromX; x < Math.Min(toX, bitmap.Width); x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (Math.Abs(pixel.Red - color.Red) < 40
                    && Math.Abs(pixel.Green - color.Green) < 40
                    && Math.Abs(pixel.Blue - color.Blue) < 40
                    && pixel.Alpha > 200)
                    count++;
            }
        }
        return count;
    }
}
