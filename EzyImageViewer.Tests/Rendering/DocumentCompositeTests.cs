using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Rendering;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

/// <summary>
/// CPU goldens over the shared composition path (FR-EDIT-004: the preview IS the export math).
/// Quarter turns, flips, crops and integer scales assert exact pixels on an asymmetric fixture;
/// free-angle raster equality is asserted structurally (opaque interior, transparent corners) —
/// per-channel epsilons for full free-angle goldens are set from Release measurements, not guessed.
/// </summary>
public class DocumentCompositeTests
{
    private static readonly SKColor TL = new(0xFF, 0x00, 0x00, 0xFF); // red
    private static readonly SKColor TR = new(0x00, 0xFF, 0x00, 0xFF); // green
    private static readonly SKColor BL = new(0x00, 0x00, 0xFF, 0xFF); // blue
    private static readonly SKColor BR = new(0xFF, 0xFF, 0x00, 0xFF); // yellow

    /// <summary>Four solid quadrants — asymmetric under every rotation/flip.</summary>
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
    public void Rotate90_MovesQuadrantsClockwiseExactly()
    {
        using var frame = QuadrantImage(8, 8);
        using var bitmap = Render(frame, new PixelSize(8, 8), State(new RotateOp(90f)));

        Assert.Equal(8, bitmap.Width);
        Assert.Equal(8, bitmap.Height);
        Assert.Equal(BL, bitmap.GetPixel(2, 2)); // clockwise: bottom-left arrives top-left
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
        // Keep the right half (green over yellow), then rotate 90° CW: yellow left, green right.
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
        // Frame decoded at half resolution: the composite undoes the reduction, so output geometry
        // (and annotation coordinates) stay in native terms.
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

        Assert.Equal(142, bitmap.Width); // content-containing rounding: floor(min)/ceil(max)
        Assert.Equal(142, bitmap.Height);
        Assert.Equal(0, bitmap.GetPixel(2, 2).Alpha); // bounding-box corner: no source there
        Assert.Equal(0, bitmap.GetPixel(139, 139).Alpha);
        Assert.Equal(255, bitmap.GetPixel(71, 71).Alpha); // source center
    }

    [Fact]
    public void OversizedDestination_PaintsNothingOutsideTheOutputCanvas()
    {
        // FR-EDIT-004: a preview surface larger than the logical canvas must show exactly what an
        // output-sized export surface shows — the composite clips to OutputSize explicitly.
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
        // A stroke on the native left edge must follow a horizontal flip to the right edge.
        var annotationId = Guid.NewGuid();
        var state = State(new FlipOp(Horizontal: true)).AddAnnotation(new RectangleAnnotation
        {
            Id = annotationId,
            Bounds = new RectF(0f, 24f, 16f, 16f),
            StrokeArgb = 0xFFFF00FF, // magenta, absent from the fixture
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
        // Data survives (non-destructive), pixels do not: the shared clip removes it (ADR-0009).
        var state = State(new CropOp(new RectF(0f, 0f, 32f, 64f))).AddAnnotation(new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(40f, 8f, 16f, 16f), // fully inside the cropped-away right half
            StrokeArgb = 0xFFFF00FF,
            StrokeWidth = 2f,
        });
        using var frame = QuadrantImage(64, 64);
        using var bitmap = Render(frame, new PixelSize(64, 64), state);

        Assert.Single(state.Annotations); // preserved in the document
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
        // Native-px stroke contract (ADR-0009): a 2px stroke under a 4× resize covers ~4× the rows.
        var state = State(new ResizeOp(new PixelSize(256, 256))).AddAnnotation(new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(16f, 16f, 32f, 32f),
            StrokeArgb = 0xFFFF00FF,
            StrokeWidth = 2f,
        });
        using var frame = QuadrantImage(64, 64);
        using var bitmap = Render(frame, new PixelSize(64, 64), state);

        // Count magenta pixels along the vertical line crossing the top edge midpoint.
        var column = 128; // native x=32 → output 128
        var hits = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            var pixel = bitmap.GetPixel(column, y);
            if (pixel.Red > 200 && pixel.Blue > 200 && pixel.Green < 60)
                hits++;
        }
        // Two edges (top and bottom) × ~8 device px each; antialiasing blurs the boundary rows.
        Assert.InRange(hits, 10, 24);
    }

    [Fact]
    public void AnnotationStroke_DeformsWithANonUniformResize()
    {
        // Native-px stroke contract under x4/y1 resize: vertical edges become ~8px wide while
        // horizontal edges stay ~2px tall — a scalar width would be wrong on one axis.
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

        // Horizontal scanline at native y=32 crosses both vertical edges: 2 × ~8px.
        var horizontalHits = 0;
        for (var x = 0; x < bitmap.Width; x++)
        {
            if (IsMagenta(bitmap.GetPixel(x, 32)))
                horizontalHits++;
        }
        Assert.InRange(horizontalHits, 12, 24);

        // Vertical scan at native x=32 (output 128) crosses both horizontal edges: 2 × ~2px.
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
