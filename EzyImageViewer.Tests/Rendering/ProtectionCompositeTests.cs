using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Rendering;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

/// <summary>실제 배경 픽셀을 쓰는 보호 효과의 미리보기·내보내기 동일 합성 계약.</summary>
public sealed class ProtectionCompositeTests
{
    private static readonly SKColor TL = new(0xFF, 0x00, 0x00, 0xFF);
    private static readonly SKColor TR = new(0x00, 0xFF, 0x00, 0xFF);
    private static readonly SKColor BL = new(0x00, 0x00, 0xFF, 0xFF);
    private static readonly SKColor BR = new(0xFF, 0xFF, 0x00, 0xFF);

    private static SKImage QuadrantImage(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(
            width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
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

    private static SKImage SolidImage(int width, int height, SKColor color)
    {
        using var surface = SKSurface.Create(new SKImageInfo(
            width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(color);
        return surface.Snapshot();
    }

    private static ProtectionAnnotation Protection(ProtectionKind kind, RectF bounds) => new()
    {
        Id = Guid.NewGuid(),
        Bounds = bounds,
        Kind = kind,
    };

    private static SKBitmap Render(SKImage frame, PixelSize native, DocumentState state)
    {
        var evaluation = TransformEvaluator.Evaluate(state.Transform, native);
        var info = new SKImageInfo(
            evaluation.OutputSize.Width, evaluation.OutputSize.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        surface.Canvas.Clear(SKColors.Transparent);
        DocumentComposite.Render(surface.Canvas, frame, native, state, evaluation, SKMatrix.Identity);
        var bitmap = new SKBitmap(info);
        Assert.True(surface.ReadPixels(info, bitmap.GetPixels(), info.RowBytes, 0, 0));
        return bitmap;
    }

    [Fact]
    public void Mask_FlattensToItsExactOpaqueColor_CoveringAnnotationsBeneath()
    {
        using var frame = QuadrantImage(16, 16);
        var state = DocumentState.Empty
            .AddAnnotation(new RectangleAnnotation
            {
                Id = Guid.NewGuid(),
                Bounds = new RectF(2, 2, 12, 12),
                StrokeArgb = 0xFF00_FF00,
                FillArgb = 0xFF00_FF00,
            })
            .AddAnnotation(Protection(ProtectionKind.Mask, new RectF(0, 0, 16, 16))
                with { MaskArgb = 0x8011_2233 });
        using var bitmap = Render(frame, new PixelSize(16, 16), state);

        // 알파는 강제 불투명. 반투명 마스크면 보호가 아니라 힌트가 됨.
        var expected = new SKColor(0x11, 0x22, 0x33, 0xFF);
        Assert.Equal(expected, bitmap.GetPixel(1, 1));
        Assert.Equal(expected, bitmap.GetPixel(8, 8));
        Assert.Equal(expected, bitmap.GetPixel(14, 14));
    }

    [Fact]
    public void Mosaic_FlattensToUniformBlocks_DestroyingQuadrantDetail()
    {
        using var frame = QuadrantImage(16, 16);
        var state = DocumentState.Empty.AddAnnotation(
            Protection(ProtectionKind.Mosaic, new RectF(0, 0, 16, 16)) with { BlockSize = 16f });
        using var bitmap = Render(frame, new PixelSize(16, 16), state);

        // 블록 하나가 네 사분면을 덮어 모든 픽셀이 같은 혼합색, 원래 색은 남지 않음.
        var block = bitmap.GetPixel(2, 2);
        Assert.Equal(block, bitmap.GetPixel(13, 2));
        Assert.Equal(block, bitmap.GetPixel(2, 13));
        Assert.Equal(block, bitmap.GetPixel(13, 13));
        Assert.Equal(0xFF, block.Alpha);
        Assert.NotEqual(TL, block);
        Assert.NotEqual(TR, block);
        Assert.NotEqual(BL, block);
        Assert.NotEqual(BR, block);
    }

    private static SKImage StripeImage(int width, int height, params (int Width, SKColor Color)[] stripes)
    {
        using var surface = SKSurface.Create(new SKImageInfo(
            width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var paint = new SKPaint();
        var x = 0;
        foreach (var (stripeWidth, color) in stripes)
        {
            paint.Color = color;
            surface.Canvas.DrawRect(SKRect.Create(x, 0, stripeWidth, height), paint);
            x += stripeWidth;
        }
        return surface.Snapshot();
    }

    [Fact]
    public void Mosaic_ClipsTheTrailingPartialBlock_OnUnalignedBounds()
    {
        // 너비 25px, 블록 12면 12 / 12 / 1. 균등 8.33px 세 칸 아님.
        using var frame = StripeImage(25, 10, (12, TL), (12, TR), (1, BL));
        var state = DocumentState.Empty.AddAnnotation(
            Protection(ProtectionKind.Mosaic, new RectF(0, 0, 25, 10)) with { BlockSize = 12f });
        using var bitmap = Render(frame, new PixelSize(25, 10), state);

        // 각 칸이 단색 띠 하나만 덮으므로 평균도 그 띠 색.
        Assert.Equal(TL, bitmap.GetPixel(0, 0));
        Assert.Equal(TL, bitmap.GetPixel(11, 9));
        Assert.Equal(TR, bitmap.GetPixel(12, 0));
        Assert.Equal(TR, bitmap.GetPixel(23, 9));
        Assert.Equal(BL, bitmap.GetPixel(24, 0));
        Assert.Equal(BL, bitmap.GetPixel(24, 9));
    }

    [Fact]
    public void Mosaic_CellColor_IsTheExactBoxAverageOfItsPixels()
    {
        // 빨강·파랑 절반씩 덮은 8x8 한 블록의 정확한 평균은 (127, 0, 127).
        using var frame = StripeImage(8, 8, (4, TL), (4, BL));
        var state = DocumentState.Empty.AddAnnotation(
            Protection(ProtectionKind.Mosaic, new RectF(0, 0, 8, 8)) with { BlockSize = 8f });
        using var bitmap = Render(frame, new PixelSize(8, 8), state);

        var expected = new SKColor(0x7F, 0x00, 0x7F, 0xFF);
        Assert.Equal(expected, bitmap.GetPixel(0, 0));
        Assert.Equal(expected, bitmap.GetPixel(7, 7));
    }

    [Fact]
    public void Blur_AtTheMaximumSigma_RendersOpaqueWithFullPadding()
    {
        using var frame = QuadrantImage(100, 60);
        var state = DocumentState.Empty.AddAnnotation(
            Protection(ProtectionKind.Blur, new RectF(0, 0, 100, 60))
                with { BlurSigma = AnnotationValidator.MaxBlurSigma });
        using var first = Render(frame, new PixelSize(100, 60), state);
        using var second = Render(frame, new PixelSize(100, 60), state);

        Assert.Equal(0xFF, first.GetPixel(0, 0).Alpha);
        Assert.Equal(0xFF, first.GetPixel(50, 30).Alpha);
        Assert.NotEqual(TL, first.GetPixel(10, 10));
        Assert.Equal(first.GetPixel(50, 30), second.GetPixel(50, 30));
    }

    [Fact]
    public void Blur_SoftensQuadrantEdges_StaysOpaqueAndDeterministic()
    {
        using var frame = QuadrantImage(16, 16);
        var state = DocumentState.Empty.AddAnnotation(
            Protection(ProtectionKind.Blur, new RectF(0, 0, 16, 16)) with { BlurSigma = 4f });
        using var first = Render(frame, new PixelSize(16, 16), state);
        using var second = Render(frame, new PixelSize(16, 16), state);

        // 세로 사분면 경계는 더 이상 딱딱한 빨강|초록 선이면 안 됨.
        var boundary = first.GetPixel(8, 3);
        Assert.NotEqual(TL, boundary);
        Assert.NotEqual(TR, boundary);
        Assert.Equal(0xFF, boundary.Alpha);
        Assert.Equal(0xFF, first.GetPixel(1, 1).Alpha);
        // 같은 입력은 같은 출력. 확정 때 본 모습 그대로 내보내기.
        Assert.Equal(boundary, second.GetPixel(8, 3));
        Assert.Equal(first.GetPixel(4, 12), second.GetPixel(4, 12));
    }

    [Fact]
    public void MosaicAndBlur_SampleTheBackgroundFrame_NeverAnnotationsBeneath()
    {
        using var frame = SolidImage(16, 16, TL);
        var covered = new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(0, 0, 16, 16),
            StrokeArgb = 0xFF00_FF00,
            FillArgb = 0xFF00_FF00,
        };
        var state = DocumentState.Empty
            .AddAnnotation(covered)
            .AddAnnotation(Protection(ProtectionKind.Mosaic, new RectF(0, 0, 16, 16)));
        using var bitmap = Render(frame, new PixelSize(16, 16), state);

        // 단색 빨강 모자이크는 빨강. 아래 초록 주석은 샘플에도 표시에도 안 남음.
        Assert.Equal(TL, bitmap.GetPixel(8, 8));
        Assert.Equal(TL, bitmap.GetPixel(2, 14));
    }

    [Fact]
    public void ProtectionOnAHiddenLayer_DoesNotApply()
    {
        using var frame = QuadrantImage(16, 16);
        var state = DocumentState.Empty.AddAnnotation(
            Protection(ProtectionKind.Mask, new RectF(0, 0, 16, 16)));
        state = state.ReplaceLayer(state.Layers[0] with { IsVisible = false });
        using var bitmap = Render(frame, new PixelSize(16, 16), state);

        // 레이어 숨김은 명시적 의도라 원본 픽셀 복귀.
        Assert.Equal(TL, bitmap.GetPixel(2, 2));
        Assert.Equal(BR, bitmap.GetPixel(13, 13));
    }
}
