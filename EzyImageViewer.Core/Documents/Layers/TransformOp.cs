using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Core.Documents.Layers;

/// <summary>
/// One background edit in the transform pipeline (FR-EDIT-001~004). Ops are ordered and immutable;
/// each is defined in the output space of the ops before it — the only representation closed under
/// arbitrary edit order (resize-then-crop, straighten-then-crop). Matrix, output size and clip are
/// derived by <see cref="TransformEvaluator"/>, never stored (ADR-0009).
/// </summary>
public abstract record TransformOp
{
    private protected TransformOp() { }

    /// <summary>Bytes one op retains inside a history entry (FR-HIST-002 accounting).</summary>
    public const long EstimatedRetainedBytes = 40;
}

/// <summary>Keeps <see cref="Bounds"/>, expressed in the output space of the preceding ops.</summary>
public sealed record CropOp : TransformOp
{
    public CropOp(RectF bounds)
    {
        if (!float.IsFinite(bounds.X) || !float.IsFinite(bounds.Y)
            || !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height))
            throw new ArgumentOutOfRangeException(nameof(bounds), "Crop bounds must be finite.");
        if (bounds.Width <= 0f || bounds.Height <= 0f)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Crop bounds must have positive extent.");
        Bounds = bounds;
    }

    public RectF Bounds { get; }
}

/// <summary>
/// Clears <see cref="Bounds"/> to transparency, expressed in the output space of the preceding ops
/// (UR-009 region cut/lift). Geometry-neutral: the canvas keeps its size; the evaluator maps the
/// region back to native space so the punch survives later rotations/crops like SourceClip does.
/// </summary>
public sealed record EraseOp : TransformOp
{
    public EraseOp(RectF bounds)
    {
        if (!float.IsFinite(bounds.X) || !float.IsFinite(bounds.Y)
            || !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height))
            throw new ArgumentOutOfRangeException(nameof(bounds), "Erase bounds must be finite.");
        if (bounds.Width <= 0f || bounds.Height <= 0f)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Erase bounds must have positive extent.");
        Bounds = bounds;
    }

    public RectF Bounds { get; }
}

/// <summary>Rotates clockwise about the canvas center at this point in the pipeline (FR-EDIT-003).</summary>
public sealed record RotateOp : TransformOp
{
    /// <summary>
    /// The user-input path: normalizes in double space FIRST, because a finite double like 1e300
    /// overflows a float cast to Infinity and would turn a valid dialog entry into a throw.
    /// </summary>
    public static RotateOp FromDegrees(double degrees)
    {
        if (!double.IsFinite(degrees))
            throw new ArgumentOutOfRangeException(nameof(degrees), "Rotation must be finite.");
        return new RotateOp((float)(degrees % 360d));
    }

    public RotateOp(float degrees)
    {
        if (!float.IsFinite(degrees))
            throw new ArgumentOutOfRangeException(nameof(degrees), "Rotation must be finite.");
        var normalized = degrees % 360f;
        if (normalized < 0f)
            normalized += 360f;
        // A tiny negative remainder rounds up to exactly 360f (half-ULP at 360 ≈ 1.5e-05), which
        // would masquerade as a quarter turn downstream — wrap it back to the documented [0, 360).
        if (normalized >= 360f)
            normalized -= 360f;
        Degrees = normalized;
    }

    /// <summary>Normalized to [0, 360).</summary>
    public float Degrees { get; }

    /// <summary>Quarter turns evaluate on the exact integer path — no trigonometry, no float drift.</summary>
    public bool IsQuarterTurn => Degrees % 90f == 0f;
}

/// <summary>Mirrors across the canvas center axis (FR-EDIT-004): left↔right when horizontal, else top↔bottom.</summary>
public sealed record FlipOp(bool Horizontal) : TransformOp;

/// <summary>Scales the canvas to an explicit output size (FR-EDIT-002). Non-uniform when the caller
/// did not keep the aspect ratio; the op stores the user's stated intent, not a factor.</summary>
public sealed record ResizeOp : TransformOp
{
    public ResizeOp(PixelSize target)
    {
        if (target.Width < 1 || target.Height < 1)
            throw new ArgumentOutOfRangeException(nameof(target), "Resize target must be at least 1×1.");
        if (target.Width > TransformEvaluator.MaxOutputDimension || target.Height > TransformEvaluator.MaxOutputDimension)
            throw new ArgumentOutOfRangeException(nameof(target),
                $"Resize target exceeds the {TransformEvaluator.MaxOutputDimension}px side limit.");
        Target = target;
    }

    public PixelSize Target { get; }
}
