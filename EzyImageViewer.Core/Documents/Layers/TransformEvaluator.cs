using System.Numerics;
using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Core.Documents.Layers;

/// <summary>
/// 원본 크기에 변환 파이프라인을 적용한 파생값. 저장하지 않고 로드 때 재계산.
/// 행 벡터 규약 사용: v·M, A*B는 A부터 적용.
/// </summary>
public sealed class TransformEvaluation
{
    /// <summary>원본 픽셀을 출력 캔버스 픽셀로 변환.</summary>
    public required Matrix3x2 NativeToOutput { get; init; }

    /// <summary>합성 문서가 차지하는 정수 캔버스.</summary>
    public required PixelSize OutputSize { get; init; }

    /// <summary>모든 자르기를 통과한 원본 영역. 후속 회전 뒤 잘린 픽셀이 부활하지 않게 공유.</summary>
    public required IReadOnlyList<Vector2> SourceClip { get; init; }

    /// <summary>원본 좌표의 투명 구멍. 배경만 잘라내고 위 주석은 건드리지 않음.</summary>
    public IReadOnlyList<IReadOnlyList<Vector2>> ErasedNative { get; init; } = [];

    public bool TryGetOutputToNative(out Matrix3x2 inverse) => Matrix3x2.Invert(NativeToOutput, out inverse);

    /// <summary>원본 점이 모든 자르기를 통과하면 true. 경계는 안쪽.</summary>
    public bool ContainsNativePoint(float x, float y)
    {
        if (SourceClip.Count < 3)
            return false;
        var positive = false;
        var negative = false;
        for (var i = 0; i < SourceClip.Count; i++)
        {
            var a = SourceClip[i];
            var b = SourceClip[(i + 1) % SourceClip.Count];
            var cross = ((b.X - a.X) * (y - a.Y)) - ((b.Y - a.Y) * (x - a.X));
            if (cross > 0f)
                positive = true;
            else if (cross < 0f)
                negative = true;
            if (positive && negative)
                return false;
        }
        return true;
    }
}

public static class TransformEvaluator
{
    /// <summary>
    /// 출력 한 변 상한. 여기서는 버퍼를 만들지 않아 픽셀 수는 제한하지 않고,
    /// 실제 할당 경로가 별도 바이트 예산을 적용.
    /// </summary>
    public static int MaxOutputDimension { get; } = InputLimits.Default.MaxDimension;

    /// <summary>
    /// 파이프라인을 한 번 순회. 매 단계 캔버스를 바깥쪽 정수로 맞춰 접두 변환 안정성 유지.
    /// 다음 연산 전에 유한값·역행렬·한 변 상한을 검사.
    /// </summary>
    public static TransformEvaluation Evaluate(BackgroundTransform transform, PixelSize nativeSize)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (nativeSize.IsEmpty)
            throw new ArgumentOutOfRangeException(nameof(nativeSize), "Source size must be positive.");

        var matrix = Matrix3x2.Identity; // 원본 → 현재 캔버스.
        var size = new Vector2(nativeSize.Width, nativeSize.Height);
        var clip = new List<Vector2>(4)
        {
            new(0f, 0f), new(nativeSize.Width, 0f),
            new(nativeSize.Width, nativeSize.Height), new(0f, nativeSize.Height),
        };

        var erased = new List<IReadOnlyList<Vector2>>();
        foreach (var op in transform.Ops)
        {
            switch (op)
            {
                case EraseOp erase:
                {
                    // 현재 캔버스에 맞춤. 완전히 빗나가면 조용히 넘기지 않고 호출 오류.
                    var x0 = MathF.Max(0f, erase.Bounds.X);
                    var y0 = MathF.Max(0f, erase.Bounds.Y);
                    var x1 = MathF.Min(size.X, erase.Bounds.Right);
                    var y1 = MathF.Min(size.Y, erase.Bounds.Bottom);
                    if (x1 - x0 <= 0f || y1 - y0 <= 0f)
                        throw new InvalidOperationException("Erase region misses the canvas.");
                    if (!Matrix3x2.Invert(matrix, out var eraseToNative))
                        throw new InvalidOperationException("Transform chain is not invertible.");
                    erased.Add(
                    [
                        Vector2.Transform(new Vector2(x0, y0), eraseToNative),
                        Vector2.Transform(new Vector2(x1, y0), eraseToNative),
                        Vector2.Transform(new Vector2(x1, y1), eraseToNative),
                        Vector2.Transform(new Vector2(x0, y1), eraseToNative),
                    ]);
                    break;
                }
                case CropOp crop:
                {
                    // 픽셀 격자 바깥쪽으로 맞춰 요청 영역을 보존하고 캔버스는 정수 유지.
                    var x0 = MathF.Floor(MathF.Max(0f, crop.Bounds.X));
                    var y0 = MathF.Floor(MathF.Max(0f, crop.Bounds.Y));
                    var x1 = MathF.Ceiling(MathF.Min(size.X, crop.Bounds.Right));
                    var y1 = MathF.Ceiling(MathF.Min(size.Y, crop.Bounds.Bottom));
                    if (x1 - x0 < 1f || y1 - y0 < 1f)
                        throw new InvalidOperationException("Crop region misses the canvas.");
                    if (!Matrix3x2.Invert(matrix, out var toNative))
                        throw new InvalidOperationException("Transform chain is not invertible.");
                    clip = ClipConvex(clip,
                    [
                        Vector2.Transform(new Vector2(x0, y0), toNative),
                        Vector2.Transform(new Vector2(x1, y0), toNative),
                        Vector2.Transform(new Vector2(x1, y1), toNative),
                        Vector2.Transform(new Vector2(x0, y1), toNative),
                    ]);
                    matrix *= Matrix3x2.CreateTranslation(-x0, -y0);
                    size = new Vector2(x1 - x0, y1 - y0);
                    break;
                }
                case RotateOp rotate:
                {
                    if (rotate.Degrees == 0f)
                        break;
                    Matrix3x2 step;
                    Vector2 rotated;
                    if (rotate.IsQuarterTurn)
                    {
                        // y축 아래 방향 화면 좌표의 정확한 정수 행렬.
                        (step, rotated) = rotate.Degrees switch
                        {
                            90f => (new Matrix3x2(0f, 1f, -1f, 0f, size.Y, 0f), new Vector2(size.Y, size.X)),
                            180f => (new Matrix3x2(-1f, 0f, 0f, -1f, size.X, size.Y), size),
                            _ => (new Matrix3x2(0f, -1f, 1f, 0f, 0f, size.X), new Vector2(size.Y, size.X)),
                        };
                    }
                    else
                    {
                        var spin = Matrix3x2.CreateRotation(rotate.Degrees * (MathF.PI / 180f), size / 2f);
                        var min = new Vector2(float.PositiveInfinity);
                        var max = new Vector2(float.NegativeInfinity);
                        foreach (var corner in (ReadOnlySpan<Vector2>)
                            [new(0f, 0f), new(size.X, 0f), new(size.X, size.Y), new(0f, size.Y)])
                        {
                            var mapped = Vector2.Transform(corner, spin);
                            min = Vector2.Min(min, mapped);
                            max = Vector2.Max(max, mapped);
                        }
                        // 원점은 내림, 먼 끝은 올림. 회전한 점을 정수 캔버스 안에 모두 담음.
                        var origin = new Vector2(MathF.Floor(min.X), MathF.Floor(min.Y));
                        step = spin * Matrix3x2.CreateTranslation(-origin.X, -origin.Y);
                        rotated = new Vector2(MathF.Ceiling(max.X), MathF.Ceiling(max.Y)) - origin;
                    }
                    matrix *= step;
                    size = rotated;
                    break;
                }
                case FlipOp flip:
                {
                    matrix *= flip.Horizontal
                        ? new Matrix3x2(-1f, 0f, 0f, 1f, size.X, 0f)
                        : new Matrix3x2(1f, 0f, 0f, -1f, 0f, size.Y);
                    break;
                }
                case ResizeOp resize:
                {
                    matrix *= Matrix3x2.CreateScale(resize.Target.Width / size.X, resize.Target.Height / size.Y);
                    size = new Vector2(resize.Target.Width, resize.Target.Height);
                    break;
                }
                default:
                    throw new NotSupportedException($"Unknown transform op {op.GetType().Name}.");
            }

            // 매 단계 검사. 수상한 중간값이 다음 계산에 숨어들 틈을 주지 않음.
            ValidateCanvas(size, matrix);
        }

        var output = new PixelSize(
            Math.Max(1, (int)MathF.Round(size.X)),
            Math.Max(1, (int)MathF.Round(size.Y)));

        return new TransformEvaluation
        {
            NativeToOutput = matrix,
            OutputSize = output,
            SourceClip = clip,
            ErasedNative = erased,
        };
    }

    private static void ValidateCanvas(Vector2 size, in Matrix3x2 matrix)
    {
        if (!float.IsFinite(size.X) || !float.IsFinite(size.Y) || size.X < 1f || size.Y < 1f)
            throw new InvalidOperationException("Transform produced a degenerate canvas.");
        if (size.X > MaxOutputDimension || size.Y > MaxOutputDimension)
            throw new InvalidOperationException(
                $"Canvas {size.X}x{size.Y} exceeds the {MaxOutputDimension}px side limit.");
        var determinant = (matrix.M11 * matrix.M22) - (matrix.M12 * matrix.M21);
        if (!float.IsFinite(matrix.M31) || !float.IsFinite(matrix.M32)
            || !float.IsFinite(determinant) || determinant == 0f)
            throw new InvalidOperationException("Transform chain produced a non-invertible matrix.");
    }

    /// <summary>Sutherland–Hodgman 볼록 다각형 교차. 감김 방향 무관, 빈 교차면 빈 목록.</summary>
    private static List<Vector2> ClipConvex(List<Vector2> subject, ReadOnlySpan<Vector2> clipper)
    {
        var interiorSign = MathF.Sign(SignedArea(clipper));
        if (interiorSign == 0)
            return [];

        var output = subject;
        for (var i = 0; i < clipper.Length && output.Count > 0; i++)
        {
            var a = clipper[i];
            var b = clipper[(i + 1) % clipper.Length];
            var input = output;
            output = new List<Vector2>(input.Count + 1);
            for (var j = 0; j < input.Count; j++)
            {
                var p = input[j];
                var q = input[(j + 1) % input.Count];
                var pInside = Side(a, b, p) * interiorSign >= 0f;
                var qInside = Side(a, b, q) * interiorSign >= 0f;
                if (qInside)
                {
                    if (!pInside)
                        output.Add(IntersectEdge(a, b, p, q));
                    output.Add(q);
                }
                else if (pInside)
                {
                    output.Add(IntersectEdge(a, b, p, q));
                }
            }
        }
        return output.Count < 3 ? [] : output;
    }

    private static float Side(Vector2 a, Vector2 b, Vector2 p) =>
        ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));

    private static Vector2 IntersectEdge(Vector2 a, Vector2 b, Vector2 p, Vector2 q)
    {
        var dp = Side(a, b, p);
        var dq = Side(a, b, q);
        var t = dp / (dp - dq); // p와 q가 반대편이라 분모는 0이 아님.
        return p + ((q - p) * t);
    }

    private static float SignedArea(ReadOnlySpan<Vector2> polygon)
    {
        // 첫 꼭짓점 기준 오프셋으로 신발끈 계산. 큰 좌표의 반올림이 작은 자르기 면적을
        // 0으로 지워 화면까지 백지로 만드는 사고를 막음.
        var origin = polygon[0];
        var sum = 0f;
        for (var i = 0; i < polygon.Length; i++)
        {
            var a = polygon[i] - origin;
            var b = polygon[(i + 1) % polygon.Length] - origin;
            sum += (a.X * b.Y) - (b.X * a.Y);
        }
        return sum / 2f;
    }
}
