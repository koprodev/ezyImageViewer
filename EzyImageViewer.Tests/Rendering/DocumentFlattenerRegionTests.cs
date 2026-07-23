using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Rendering;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

/// <summary>
/// FR-EDIT-007 region copy: FlattenRegion delegates to the one CropOp/TransformEvaluator contract,
/// so its pixels equal the same rectangle cut from a full Flatten and its rounding, clamping,
/// rejection and byte budget are the evaluator's — never a private re-implementation.
/// </summary>
public sealed class DocumentFlattenerRegionTests
{
    /// <summary>Unique per-pixel color, so any coordinate shift breaks the oracle comparison.</summary>
    private static SKImage GradientImage(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(
            width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, new SKColor((byte)(x * 15), (byte)(y * 15), (byte)(x + y), 0xFF));
        return SKImage.FromBitmap(bitmap);
    }

    private static void AssertMatchesFullFlattenSubset(
        SKImage frame, PixelSize native, DocumentState state, RectF region,
        int expectedX0, int expectedY0, int expectedWidth, int expectedHeight)
    {
        using var full = DocumentFlattener.Flatten(frame, native, state);
        using var partial = DocumentFlattener.FlattenRegion(frame, native, state, region);
        Assert.Equal(expectedWidth, partial.Width);
        Assert.Equal(expectedHeight, partial.Height);

        using var fullBitmap = SKBitmap.FromImage(full);
        using var partialBitmap = SKBitmap.FromImage(partial);
        for (var y = 0; y < expectedHeight; y++)
            for (var x = 0; x < expectedWidth; x++)
                Assert.Equal(
                    fullBitmap.GetPixel(expectedX0 + x, expectedY0 + y),
                    partialBitmap.GetPixel(x, y));
    }

    [Fact]
    public void FlattenRegion_FractionalBounds_RoundOutward_AndMatchFullFlatten()
    {
        var native = new PixelSize(16, 16);
        using var frame = GradientImage(16, 16);

        // floor(1.4)=1, floor(2.6)=2, ceil(4.6)=5, ceil(4.4)=5 → 4x3 at (1,2).
        AssertMatchesFullFlattenSubset(
            frame, native, DocumentState.Empty, new RectF(1.4f, 2.6f, 3.2f, 1.8f),
            expectedX0: 1, expectedY0: 2, expectedWidth: 4, expectedHeight: 3);
    }

    [Fact]
    public void FlattenRegion_PartiallyOutside_ClampsToCanvas()
    {
        var native = new PixelSize(16, 16);
        using var frame = GradientImage(16, 16);

        // Clamped to (0,0)..(ceil(3),ceil(3)) → 3x3.
        AssertMatchesFullFlattenSubset(
            frame, native, DocumentState.Empty, new RectF(-5f, -5f, 8f, 8f),
            expectedX0: 0, expectedY0: 0, expectedWidth: 3, expectedHeight: 3);
    }

    [Fact]
    public void FlattenRegion_AfterQuarterRotate_UsesOutputSpace()
    {
        // 16x8 rotated 90° → 8x16 output; the region addresses the rotated canvas.
        var native = new PixelSize(16, 8);
        var state = DocumentState.Empty.WithTransform(
            BackgroundTransform.Identity.Append(RotateOp.FromDegrees(90)));
        using var frame = GradientImage(16, 8);

        AssertMatchesFullFlattenSubset(
            frame, native, state, new RectF(2f, 3f, 4f, 5f),
            expectedX0: 2, expectedY0: 3, expectedWidth: 4, expectedHeight: 5);
    }

    [Fact]
    public void FlattenRegion_MissingCanvasOrDegenerate_IsRejected()
    {
        var native = new PixelSize(16, 16);
        using var frame = GradientImage(16, 16);

        Assert.Throws<InvalidOperationException>(() => DocumentFlattener.FlattenRegion(
            frame, native, DocumentState.Empty, new RectF(20f, 20f, 4f, 4f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentFlattener.FlattenRegion(
            frame, native, DocumentState.Empty, new RectF(1f, 1f, 0f, 4f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => DocumentFlattener.FlattenRegion(
            frame, native, DocumentState.Empty, new RectF(float.NaN, 1f, 4f, 4f)));
    }

    [Fact]
    public void FlattenRegion_SmallRegionSucceeds_WhereFullOutputExceedsBudget()
    {
        // 40000x40000 BGRA ≈ 6.4GB > 2GiB: the full flatten must refuse, the region must not.
        var native = new PixelSize(4, 4);
        var state = DocumentState.Empty.WithTransform(
            BackgroundTransform.Identity.Append(new ResizeOp(new PixelSize(40_000, 40_000))));
        using var frame = GradientImage(4, 4);

        Assert.Throws<InvalidOperationException>(() =>
            DocumentFlattener.Flatten(frame, native, state));

        using var partial = DocumentFlattener.FlattenRegion(
            frame, native, state, new RectF(100f, 100f, 64f, 64f));
        Assert.Equal(64, partial.Width);
        Assert.Equal(64, partial.Height);
    }

    [Fact]
    public void FlattenRegion_LeavesCallerStateUntouched()
    {
        var native = new PixelSize(16, 16);
        var state = DocumentState.Empty;
        var transformBefore = state.Transform;
        using var frame = GradientImage(16, 16);

        using var _ = DocumentFlattener.FlattenRegion(
            frame, native, state, new RectF(2f, 2f, 4f, 4f));

        Assert.Same(transformBefore, state.Transform);
        Assert.True(state.Transform.IsIdentity);
    }
}
