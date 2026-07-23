using System.Numerics;
using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Core.Documents.Layers;

/// <summary>
/// Derived view of a transform pipeline over one source size. Everything here is a cache of the
/// ops — never persisted, recomputed on load (ADR-0009). Matrices use the row-vector convention
/// (<see cref="Vector2.Transform(Vector2, Matrix3x2)"/> is v·M, composition A*B applies A first).
/// </summary>
public sealed class TransformEvaluation
{
    /// <summary>Maps native source pixels to output-canvas pixels.</summary>
    public required Matrix3x2 NativeToOutput { get; init; }

    /// <summary>Integer canvas the composited document occupies (status bar, Fit, export).</summary>
    public required PixelSize OutputSize { get; init; }

    /// <summary>
    /// Convex polygon in native pixels bounding the source region that survives every crop — the
    /// full source rect when nothing is cropped, empty when a crop kept only a transparent margin.
    /// Background, annotations, selection and hit-testing all clip to this one region, so pixels a
    /// crop removed can never reappear behind a later rotation.
    /// </summary>
    public required IReadOnlyList<Vector2> SourceClip { get; init; }

    /// <summary>
    /// Native-space quads punched transparent by <see cref="EraseOp"/>s. Tracked in native space
    /// (stable under later ops) and clipped out of the background draw only — annotations above an
    /// erased region are untouched.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<Vector2>> ErasedNative { get; init; } = [];

    public bool TryGetOutputToNative(out Matrix3x2 inverse) => Matrix3x2.Invert(NativeToOutput, out inverse);

    /// <summary>True when the native point survives every crop. Boundary points count as inside.</summary>
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
    /// Per-side cap for the evaluated output — the decode-side sanity bound reused (protects stride
    /// math everywhere downstream). Deliberately the ONLY output cap: a pixel-count cap would refuse
    /// the identity pipeline on any accepted large source (decode admits up to 500MP, reduced) and
    /// the rotation bounding box of an elongated panorama. Nothing in M3 materializes output-sized
    /// buffers; the M6 export path imposes its own byte budget at the point of allocation.
    /// </summary>
    public static int MaxOutputDimension { get; } = InputLimits.Default.MaxDimension;

    /// <summary>
    /// Walks the pipeline once. Canvas contract: the size is INTEGER after every op — content-
    /// containing rounding (floor the min corner, ceil the max) at each crop and free-angle rotate,
    /// exact swaps for quarter turns — so <c>Evaluate(P).OutputSize</c> is exactly the canvas op
    /// Q of <c>P+Q</c> is interpreted in (prefix stability), and every transformed source point
    /// lands inside the declared output. Each op's result is validated (finite, invertible, side
    /// limit) before the next op runs. Throws when a crop misses the canvas or a cap is exceeded.
    /// </summary>
    public static TransformEvaluation Evaluate(BackgroundTransform transform, PixelSize nativeSize)
    {
        ArgumentNullException.ThrowIfNull(transform);
        if (nativeSize.IsEmpty)
            throw new ArgumentOutOfRangeException(nameof(nativeSize), "Source size must be positive.");

        var matrix = Matrix3x2.Identity; // native -> current canvas
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
                    // Clamped to the current canvas like a crop; a punch that misses it entirely is
                    // a caller bug, not a silent no-op.
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
                    // Snapped outward to the pixel grid: the kept region always contains the
                    // requested one, and the canvas stays integer (prefix stability).
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
                        // Exact integer-form matrices; clockwise in y-down screen coordinates.
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
                        // Content-containing: floor the origin shift, ceil the far edge — every
                        // rotated source point is inside the integer canvas (never clipped by it).
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

            // Before the next op runs, not just at the end: a hostile pipeline must not carry a
            // non-finite or oversized intermediate into arithmetic that would mask it later.
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

    /// <summary>
    /// Sutherland–Hodgman: intersects a convex subject with a convex clipper. Both windings are
    /// accepted; the clipper's interior side is derived from its signed area. May return fewer than
    /// three points when the intersection is empty.
    /// </summary>
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
        var t = dp / (dp - dq); // dp != dq: p and q are on opposite sides by construction
        return p + ((q - p) * t);
    }

    private static float SignedArea(ReadOnlySpan<Vector2> polygon)
    {
        // Shoelace on offsets from the first vertex: raw products at large native coordinates
        // (~6.5e4) dwarf the area of a small crop quad and float rounding cancels the sum to zero,
        // which would read as a degenerate clipper and blank the render.
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
