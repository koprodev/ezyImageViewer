using System.Numerics;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using Xunit;

namespace EzyImageViewer.Tests.Documents;

/// <summary>
/// FR-EDIT-001~004: the ordered op pipeline and its derived matrix/size/clip. Order sensitivity is
/// the whole point — the fixed-canonical-order counter-examples from the design review are pinned
/// here as regressions (ADR-0009).
/// </summary>
public class TransformEvaluatorTests
{
    private static BackgroundTransform Pipeline(params TransformOp[] ops)
    {
        var transform = BackgroundTransform.Identity;
        foreach (var op in ops)
            transform = transform.Append(op);
        return transform;
    }

    private static TransformEvaluation Evaluate(PixelSize native, params TransformOp[] ops) =>
        TransformEvaluator.Evaluate(Pipeline(ops), native);

    private static Vector2 Map(TransformEvaluation evaluation, float x, float y) =>
        Vector2.Transform(new Vector2(x, y), evaluation.NativeToOutput);

    // ---- identity / basics ----

    [Fact]
    public void Identity_MapsOneToOne()
    {
        var evaluation = Evaluate(new PixelSize(100, 50));

        Assert.Equal(new PixelSize(100, 50), evaluation.OutputSize);
        Assert.Equal(Matrix3x2.Identity, evaluation.NativeToOutput);
        Assert.Equal(4, evaluation.SourceClip.Count);
        Assert.True(evaluation.ContainsNativePoint(50, 25));
    }

    [Fact]
    public void EmptySource_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Evaluate(new PixelSize(0, 10)));
    }

    // ---- quarter turns: exact path ----

    [Fact]
    public void Rotate90_SwapsDimensionsAndMapsCornersExactly()
    {
        var evaluation = Evaluate(new PixelSize(100, 50), new RotateOp(90f));

        Assert.Equal(new PixelSize(50, 100), evaluation.OutputSize);
        // Clockwise: native top-left lands at output top-right, exactly (no trig on this path).
        Assert.Equal(new Vector2(50f, 0f), Map(evaluation, 0f, 0f));
        Assert.Equal(new Vector2(0f, 0f), Map(evaluation, 0f, 50f));
        Assert.Equal(new Vector2(50f, 100f), Map(evaluation, 100f, 0f));
    }

    [Fact]
    public void Rotate180_MapsCornersExactly()
    {
        var evaluation = Evaluate(new PixelSize(100, 50), new RotateOp(180f));

        Assert.Equal(new PixelSize(100, 50), evaluation.OutputSize);
        Assert.Equal(new Vector2(100f, 50f), Map(evaluation, 0f, 0f));
        Assert.Equal(new Vector2(0f, 0f), Map(evaluation, 100f, 50f));
    }

    [Fact]
    public void FourQuarterTurns_ComposeToTheExactIdentity()
    {
        var evaluation = Evaluate(new PixelSize(100, 50),
            new RotateOp(90f), new RotateOp(90f), new RotateOp(90f), new RotateOp(90f));

        Assert.Equal(new PixelSize(100, 50), evaluation.OutputSize);
        Assert.Equal(new Vector2(10f, 20f), Map(evaluation, 10f, 20f));
    }

    [Fact]
    public void RotateNegative90_NormalizesTo270()
    {
        var op = new RotateOp(-90f);
        Assert.Equal(270f, op.Degrees);
        Assert.True(op.IsQuarterTurn);
    }

    [Fact]
    public void Rotate360_IsANoOp()
    {
        var evaluation = Evaluate(new PixelSize(100, 50), new RotateOp(360f));
        Assert.Equal(Matrix3x2.Identity, evaluation.NativeToOutput);
    }

    [Fact]
    public void TinyNegativeAngle_NormalizesToZero_NotToAQuarterTurn()
    {
        // Regression: -1e-05 % 360 + 360 rounds to exactly 360f (half-ULP at 360 ≈ 1.5e-05), which
        // the quarter-turn switch would execute as a 270° rotation.
        var op = new RotateOp(-1e-05f);
        Assert.Equal(0f, op.Degrees);

        var evaluation = Evaluate(new PixelSize(100, 50), op);
        Assert.Equal(new PixelSize(100, 50), evaluation.OutputSize);
        Assert.Equal(Matrix3x2.Identity, evaluation.NativeToOutput);
    }

    // ---- free angle ----

    [Fact]
    public void Rotate45_OutputContainsEveryTransformedCorner()
    {
        var evaluation = Evaluate(new PixelSize(100, 100), new RotateOp(45f));

        // 100·(sin45 + cos45) = 141.42, content-containing rounding: floor(min)/ceil(max) → 142.
        Assert.Equal(new PixelSize(142, 142), evaluation.OutputSize);

        // The origin shift is the floored min corner (−21), so the source center lands at 71 exact.
        var center = Map(evaluation, 50f, 50f);
        Assert.Equal(71f, center.X, 2);
        Assert.Equal(71f, center.Y, 2);

        // Containment is the contract, not a tolerance: corners inside [0, OutputSize].
        foreach (var (x, y) in new[] { (0f, 0f), (100f, 0f), (100f, 100f), (0f, 100f) })
        {
            var mapped = Map(evaluation, x, y);
            Assert.InRange(mapped.X, 0f, evaluation.OutputSize.Width);
            Assert.InRange(mapped.Y, 0f, evaluation.OutputSize.Height);
        }
    }

    [Fact]
    public void PrefixCanvas_IsExactlyWhatTheNextOpIsInterpretedIn()
    {
        // Prefix stability: Evaluate(P).OutputSize is the integer canvas op Q of P+Q runs in.
        // Cropping that full declared canvas after a free rotation must be a geometric no-op.
        var prefix = Evaluate(new PixelSize(100, 100), new RotateOp(45f));
        var full = new RectF(0f, 0f, prefix.OutputSize.Width, prefix.OutputSize.Height);
        var extended = Evaluate(new PixelSize(100, 100), new RotateOp(45f), new CropOp(full));

        Assert.Equal(prefix.OutputSize, extended.OutputSize);
        Assert.Equal(Map(prefix, 50f, 50f), Map(extended, 50f, 50f));
    }

    // ---- order sensitivity (the fixed-canonical-order counter-examples) ----

    [Fact]
    public void ResizeThenCrop_KeepsTheOnScreenSelection()
    {
        // 100×100 shrunk to 50×50, then the right half of the *screen* cropped: the user selected
        // a 25×50 region and must get exactly that — a native-space crop re-stretched to a fixed
        // output would distort it.
        var evaluation = Evaluate(new PixelSize(100, 100),
            new ResizeOp(new PixelSize(50, 50)), new CropOp(new RectF(25f, 0f, 25f, 50f)));

        Assert.Equal(new PixelSize(25, 50), evaluation.OutputSize);
        // Native (50,0) — the source midline — is the new left edge.
        Assert.Equal(new Vector2(0f, 0f), Map(evaluation, 50f, 0f));
    }

    [Fact]
    public void CropThenResize_IsADifferentDocumentThanResizeThenCrop()
    {
        var resizeFirst = Evaluate(new PixelSize(100, 100),
            new ResizeOp(new PixelSize(50, 50)), new CropOp(new RectF(25f, 0f, 25f, 50f)));
        var cropFirst = Evaluate(new PixelSize(100, 100),
            new CropOp(new RectF(50f, 0f, 50f, 100f)), new ResizeOp(new PixelSize(50, 50)));

        Assert.NotEqual(resizeFirst.OutputSize, cropFirst.OutputSize);
        Assert.NotEqual(Map(resizeFirst, 75f, 50f), Map(cropFirst, 75f, 50f));
    }

    [Fact]
    public void RotateThenCrop_CropsInTheRotatedScreenSpace()
    {
        // 90° then "keep the top half of what I see": that is the *left* half of the source.
        var evaluation = Evaluate(new PixelSize(100, 100),
            new RotateOp(90f), new CropOp(new RectF(0f, 0f, 100f, 50f)));

        Assert.Equal(new PixelSize(100, 50), evaluation.OutputSize);
        Assert.True(evaluation.ContainsNativePoint(25f, 50f));
        Assert.False(evaluation.ContainsNativePoint(75f, 50f));
    }

    [Fact]
    public void RotateThenFlip_DiffersFromFlipThenRotate()
    {
        var rotateFirst = Evaluate(new PixelSize(100, 50), new RotateOp(90f), new FlipOp(Horizontal: true));
        var flipFirst = Evaluate(new PixelSize(100, 50), new FlipOp(Horizontal: true), new RotateOp(90f));

        Assert.NotEqual(Map(rotateFirst, 0f, 0f), Map(flipFirst, 0f, 0f));
    }

    // ---- crop clip semantics ----

    [Fact]
    public void MultipleCrops_IntersectInNativeSpace()
    {
        var evaluation = Evaluate(new PixelSize(100, 100),
            new CropOp(new RectF(10f, 10f, 80f, 80f)),   // native 10..90
            new CropOp(new RectF(0f, 0f, 40f, 40f)));    // native 10..50

        Assert.Equal(new PixelSize(40, 40), evaluation.OutputSize);
        Assert.True(evaluation.ContainsNativePoint(30f, 30f));
        Assert.False(evaluation.ContainsNativePoint(5f, 5f));
        Assert.False(evaluation.ContainsNativePoint(70f, 70f));
    }

    [Fact]
    public void RotationAfterACrop_DoesNotResurrectCroppedPixels()
    {
        // The rotation's bounding box re-exposes canvas area where cropped-away source pixels would
        // sit; the accumulated clip must keep them out (ADR-0009).
        var evaluation = Evaluate(new PixelSize(100, 100),
            new CropOp(new RectF(0f, 0f, 50f, 100f)), new RotateOp(45f));

        Assert.True(evaluation.ContainsNativePoint(25f, 50f));
        Assert.False(evaluation.ContainsNativePoint(75f, 50f));
    }

    [Fact]
    public void CropInATransparentCorner_YieldsAnEmptyClip()
    {
        // 45° bounding box corners hold no source pixels; cropping one is geometrically valid but
        // nothing survives.
        var evaluation = Evaluate(new PixelSize(100, 100),
            new RotateOp(45f), new CropOp(new RectF(0f, 0f, 20f, 20f)));

        Assert.Empty(evaluation.SourceClip);
        Assert.False(evaluation.ContainsNativePoint(50f, 50f));
    }

    [Fact]
    public void CropOutsideTheCanvas_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Evaluate(new PixelSize(100, 100), new CropOp(new RectF(200f, 200f, 10f, 10f))));
    }

    [Fact]
    public void CropOverhangingTheCanvas_IsClampedToIt()
    {
        var evaluation = Evaluate(new PixelSize(100, 100), new CropOp(new RectF(-10f, -10f, 60f, 60f)));
        Assert.Equal(new PixelSize(50, 50), evaluation.OutputSize);
    }

    [Fact]
    public void TinyCropAtLargeCoordinates_KeepsANonEmptyClip()
    {
        // Regression: the shoelace on raw coordinates (~6.5e4, float ULP 4) cancelled to zero for a
        // 2×2 crop quad, reading as a degenerate clipper and blanking the whole render.
        var evaluation = Evaluate(new PixelSize(65_500, 768),
            new CropOp(new RectF(63_946.56f, 736.79364f, 2f, 2f)));

        // Snapped outward to the pixel grid: 63946..63949 × 736..739.
        Assert.Equal(new PixelSize(3, 3), evaluation.OutputSize);
        Assert.True(evaluation.SourceClip.Count >= 3);
        Assert.True(evaluation.ContainsNativePoint(63_947.5f, 737.5f));
    }

    // ---- flip / resize ----

    [Fact]
    public void FlipHorizontal_MirrorsAcrossTheVerticalCenter()
    {
        var evaluation = Evaluate(new PixelSize(100, 50), new FlipOp(Horizontal: true));
        Assert.Equal(new PixelSize(100, 50), evaluation.OutputSize);
        Assert.Equal(new Vector2(90f, 20f), Map(evaluation, 10f, 20f));
    }

    [Fact]
    public void FlipVertical_MirrorsAcrossTheHorizontalCenter()
    {
        var evaluation = Evaluate(new PixelSize(100, 50), new FlipOp(Horizontal: false));
        Assert.Equal(new Vector2(10f, 30f), Map(evaluation, 10f, 20f));
    }

    [Fact]
    public void NonUniformResize_ScalesEachAxisIndependently()
    {
        var evaluation = Evaluate(new PixelSize(100, 100), new ResizeOp(new PixelSize(200, 50)));
        Assert.Equal(new PixelSize(200, 50), evaluation.OutputSize);
        Assert.Equal(new Vector2(100f, 25f), Map(evaluation, 50f, 50f));
    }

    // ---- op validation / caps ----

    [Fact]
    public void NonFiniteOps_AreRejectedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RotateOp(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RotateOp(float.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CropOp(new RectF(float.NaN, 0f, 10f, 10f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CropOp(new RectF(0f, 0f, -5f, 10f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResizeOp(new PixelSize(0, 10)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResizeOp(new PixelSize(TransformEvaluator.MaxOutputDimension + 1, 10)));
    }

    [Fact]
    public void FromDegrees_NormalizesInDoubleSpace_SoAnyFiniteEntryIsValid()
    {
        // Regression: 1e300 is a finite double whose float cast is Infinity — casting before
        // normalizing turned a valid dialog entry into a process-killing throw.
        var extreme = RotateOp.FromDegrees(1e300);
        Assert.True(float.IsFinite(extreme.Degrees));
        Assert.InRange(extreme.Degrees, 0f, 360f);

        Assert.Equal(45f, RotateOp.FromDegrees(45d).Degrees);
        Assert.Equal(270f, RotateOp.FromDegrees(-90d).Degrees);
        Assert.Throws<ArgumentOutOfRangeException>(() => RotateOp.FromDegrees(double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => RotateOp.FromDegrees(double.PositiveInfinity));
    }

    [Fact]
    public void IdentityOnAHugeAcceptedSource_Evaluates()
    {
        // Decode admits up to 500MP (reduced) — the identity pipeline must never be refusable, or
        // merely opening such a file would crash the status bar and paint (there is no pixel cap;
        // the M6 export path owns its own byte budget).
        var evaluation = Evaluate(new PixelSize(20_000, 10_000)); // 200MP
        Assert.Equal(new PixelSize(20_000, 10_000), evaluation.OutputSize);
    }

    [Fact]
    public void FreeRotationOfAPanorama_Evaluates()
    {
        // An elongated source's 45° bounding box far exceeds 2× its own area — a pixel-count cap
        // would refuse this legitimate edit; only the per-side bound applies.
        var evaluation = Evaluate(new PixelSize(25_000, 2_000), new RotateOp(45f));
        Assert.True(evaluation.OutputSize.Width <= TransformEvaluator.MaxOutputDimension);
        Assert.True(evaluation.OutputSize.PixelCount > 2L * 25_000 * 2_000);
    }

    [Fact]
    public void OutputPastTheSideCap_IsRejectedAtEvaluation()
    {
        // 45° on a near-cap source: the bounding-box side (w+h)/√2 ≈ 88,742 exceeds the 65,500 bound.
        Assert.Throws<InvalidOperationException>(() =>
            Evaluate(new PixelSize(65_500, 60_000), new RotateOp(45f)));
    }

    // ---- value semantics ----

    [Fact]
    public void BackgroundTransform_EqualityIsByOpSequence()
    {
        var a = Pipeline(new RotateOp(90f), new FlipOp(true));
        var b = Pipeline(new RotateOp(90f), new FlipOp(true));
        var c = Pipeline(new FlipOp(true), new RotateOp(90f));

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, c);
    }
}
