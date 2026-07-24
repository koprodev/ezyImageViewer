using System.Numerics;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using Xunit;

namespace EzyImageViewer.Tests.Documents;

/// <summary>순서 있는 변환 파이프라인의 행렬·크기·클립 회귀 검증.</summary>
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

    // ---- 항등·기본 --------------------------------------------------------------------------

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

    // ---- 지우기: 크기 불변, 원본 좌표 추적 -------------------------------------------------

    [Fact]
    public void Erase_TracksNativeQuad_AndKeepsGeometry()
    {
        var evaluation = Evaluate(
            new PixelSize(100, 50), new EraseOp(new RectF(10f, 20f, 30f, 10f)));

        Assert.Equal(new PixelSize(100, 50), evaluation.OutputSize);
        Assert.Equal(Matrix3x2.Identity, evaluation.NativeToOutput);
        var quad = Assert.Single(evaluation.ErasedNative);
        Assert.Contains(quad, point => point == new Vector2(10f, 20f));
        Assert.Contains(quad, point => point == new Vector2(40f, 30f));
    }

    [Fact]
    public void Erase_AfterRotate90_MapsBackToNativeSpace()
    {
        // 지우기 영역은 회전 출력 50×100 좌표, 추적 사각형은 원본 좌표.
        var evaluation = Evaluate(
            new PixelSize(100, 50), new RotateOp(90f), new EraseOp(new RectF(0f, 0f, 50f, 100f)));

        var quad = Assert.Single(evaluation.ErasedNative);
        Assert.Contains(quad, point => point == new Vector2(0f, 0f));
        Assert.Contains(quad, point => point == new Vector2(100f, 50f));
    }

    [Fact]
    public void Erase_MissingTheCanvas_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Evaluate(new PixelSize(100, 50), new EraseOp(new RectF(200f, 0f, 10f, 10f))));
    }

    // ---- 직각 회전: 정확한 경로 -------------------------------------------------------------

    [Fact]
    public void Rotate90_SwapsDimensionsAndMapsCornersExactly()
    {
        var evaluation = Evaluate(new PixelSize(100, 50), new RotateOp(90f));

        Assert.Equal(new PixelSize(50, 100), evaluation.OutputSize);
        // 시계 방향이면 원본 왼쪽 위가 출력 오른쪽 위에 정확히 도착.
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
        // -1e-05 나머지가 360f로 반올림돼 270° 회전으로 둔갑하던 회귀.
        var op = new RotateOp(-1e-05f);
        Assert.Equal(0f, op.Degrees);

        var evaluation = Evaluate(new PixelSize(100, 50), op);
        Assert.Equal(new PixelSize(100, 50), evaluation.OutputSize);
        Assert.Equal(Matrix3x2.Identity, evaluation.NativeToOutput);
    }

    // ---- 자유 각도 --------------------------------------------------------------------------

    [Fact]
    public void Rotate45_OutputContainsEveryTransformedCorner()
    {
        var evaluation = Evaluate(new PixelSize(100, 100), new RotateOp(45f));

        // 100·(sin45 + cos45) = 141.42, 내용을 담는 바깥쪽 반올림 결과 142.
        Assert.Equal(new PixelSize(142, 142), evaluation.OutputSize);

        // 원점 이동은 최소 모서리 내림 -21이라 원본 중심이 정확히 71에 도착.
        var center = Map(evaluation, 50f, 50f);
        Assert.Equal(71f, center.X, 2);
        Assert.Equal(71f, center.Y, 2);

        // 허용 오차가 아니라 포함이 계약. 모서리는 [0, 출력 크기] 안.
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
        // 접두 안정성: P의 출력 크기가 P+Q에서 Q가 받는 정수 캔버스.
        // 자유 회전 뒤 선언 캔버스 전체 자르기는 기하학적 변화 없음.
        var prefix = Evaluate(new PixelSize(100, 100), new RotateOp(45f));
        var full = new RectF(0f, 0f, prefix.OutputSize.Width, prefix.OutputSize.Height);
        var extended = Evaluate(new PixelSize(100, 100), new RotateOp(45f), new CropOp(full));

        Assert.Equal(prefix.OutputSize, extended.OutputSize);
        Assert.Equal(Map(prefix, 50f, 50f), Map(extended, 50f, 50f));
    }

    // ---- 순서 민감도 ------------------------------------------------------------------------

    [Fact]
    public void ResizeThenCrop_KeepsTheOnScreenSelection()
    {
        // 100×100을 50×50으로 줄인 뒤 화면 오른쪽 절반 자르기. 결과는 정확히 25×50.
        var evaluation = Evaluate(new PixelSize(100, 100),
            new ResizeOp(new PixelSize(50, 50)), new CropOp(new RectF(25f, 0f, 25f, 50f)));

        Assert.Equal(new PixelSize(25, 50), evaluation.OutputSize);
        // 원본 중간선 (50,0)이 새 왼쪽 가장자리.
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
        // 90° 회전 뒤 보이는 위 절반은 원본의 왼쪽 절반.
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

    // ---- 자르기 클립 의미 -------------------------------------------------------------------

    [Fact]
    public void MultipleCrops_IntersectInNativeSpace()
    {
        var evaluation = Evaluate(new PixelSize(100, 100),
            new CropOp(new RectF(10f, 10f, 80f, 80f)),   // 원본 10..90
            new CropOp(new RectF(0f, 0f, 40f, 40f)));    // 원본 10..50

        Assert.Equal(new PixelSize(40, 40), evaluation.OutputSize);
        Assert.True(evaluation.ContainsNativePoint(30f, 30f));
        Assert.False(evaluation.ContainsNativePoint(5f, 5f));
        Assert.False(evaluation.ContainsNativePoint(70f, 70f));
    }

    [Fact]
    public void RotationAfterACrop_DoesNotResurrectCroppedPixels()
    {
        // 회전 경계가 잘린 픽셀 자리를 다시 열어도 누적 클립이 막아야 함.
        var evaluation = Evaluate(new PixelSize(100, 100),
            new CropOp(new RectF(0f, 0f, 50f, 100f)), new RotateOp(45f));

        Assert.True(evaluation.ContainsNativePoint(25f, 50f));
        Assert.False(evaluation.ContainsNativePoint(75f, 50f));
    }

    [Fact]
    public void CropInATransparentCorner_YieldsAnEmptyClip()
    {
        // 45° 경계 상자 모서리에는 원본 픽셀이 없어 유효하게 잘라도 남는 내용 없음.
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
        // 큰 원시 좌표의 신발끈 계산이 2×2 영역을 0으로 지워 전체 렌더가 비던 회귀.
        var evaluation = Evaluate(new PixelSize(65_500, 768),
            new CropOp(new RectF(63_946.56f, 736.79364f, 2f, 2f)));

        // 픽셀 격자 바깥쪽 맞춤: 63946..63949 × 736..739.
        Assert.Equal(new PixelSize(3, 3), evaluation.OutputSize);
        Assert.True(evaluation.SourceClip.Count >= 3);
        Assert.True(evaluation.ContainsNativePoint(63_947.5f, 737.5f));
    }

    // ---- 뒤집기·크기 조정 -------------------------------------------------------------------

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

    // ---- 작업 검증·상한 ---------------------------------------------------------------------

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
        // 유한 double 1e300을 정규화 전에 float로 바꿔 Infinity 예외가 나던 회귀.
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
        // 디코드가 받는 대형 원본의 항등 파이프라인은 거절 불가. 픽셀 상한은 내보내기 몫.
        var evaluation = Evaluate(new PixelSize(20_000, 10_000)); // 200MP.
        Assert.Equal(new PixelSize(20_000, 10_000), evaluation.OutputSize);
    }

    [Fact]
    public void FreeRotationOfAPanorama_Evaluates()
    {
        // 긴 원본의 45° 경계는 면적이 크게 늘어 픽셀 수 대신 한 변 상한만 적용.
        var evaluation = Evaluate(new PixelSize(25_000, 2_000), new RotateOp(45f));
        Assert.True(evaluation.OutputSize.Width <= TransformEvaluator.MaxOutputDimension);
        Assert.True(evaluation.OutputSize.PixelCount > 2L * 25_000 * 2_000);
    }

    [Fact]
    public void OutputPastTheSideCap_IsRejectedAtEvaluation()
    {
        // 상한 근처 원본 45° 회전은 경계 한 변 약 88,742로 65,500 초과.
        Assert.Throws<InvalidOperationException>(() =>
            Evaluate(new PixelSize(65_500, 60_000), new RotateOp(45f)));
    }

    // ---- 값 의미 -----------------------------------------------------------------------------

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
