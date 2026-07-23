namespace EzyImageViewer.Core.Documents.Layers;

/// <summary>
/// Single source of the speech-bubble tail shape (FR-ANNO-007). Rendering, point hit-testing,
/// band selection and the tail handle all consume the same triangle so they can never disagree.
/// All coordinates are pre-rotation annotation-local native pixels.
/// </summary>
public static class SpeechBubbleGeometry
{
    /// <summary>Tail base sits this far inside the body so the union with the rounded body never
    /// meets at a bare tangent point (a degenerate boolean-op boundary).</summary>
    public const float BaseOverlap = 2f;

    private const float MinBaseHalf = 3f;
    private const float MaxBaseHalf = 12f;

    /// <summary>Default tip for a fresh bubble: below-left of the body, clearly outside.</summary>
    public static AnnotationPoint DefaultTailTip(RectF bounds) => new(
        bounds.X + (bounds.Width * 0.2f),
        bounds.Bottom + MathF.Max(16f, bounds.Height * 0.35f));

    /// <summary>
    /// The tail triangle for <paramref name="bubble"/>, or false when the tip lies inside the
    /// body (no tail is drawn). Base points are on the edge nearest the tip, pulled inward by
    /// <see cref="BaseOverlap"/>, centered on the tip's projection and clamped clear of the
    /// rounded corners.
    /// </summary>
    public static bool TryGetTail(
        SpeechBubbleAnnotation bubble,
        out AnnotationPoint baseA, out AnnotationPoint baseB, out AnnotationPoint tip)
    {
        ArgumentNullException.ThrowIfNull(bubble);
        return TryGetTail(bubble.Bounds, bubble.CornerRadius, bubble.TailTip,
            out baseA, out baseB, out tip);
    }

    public static bool TryGetTail(
        RectF bounds, float cornerRadius, AnnotationPoint tailTip,
        out AnnotationPoint baseA, out AnnotationPoint baseB, out AnnotationPoint tip)
    {
        baseA = default;
        baseB = default;
        tip = tailTip;
        if (!float.IsFinite(tailTip.X) || !float.IsFinite(tailTip.Y)
            || bounds.Width <= 0f || bounds.Height <= 0f)
            return false;

        var outsideX = tailTip.X < bounds.X
            ? bounds.X - tailTip.X
            : tailTip.X > bounds.Right ? tailTip.X - bounds.Right : 0f;
        var outsideY = tailTip.Y < bounds.Y
            ? bounds.Y - tailTip.Y
            : tailTip.Y > bounds.Bottom ? tailTip.Y - bounds.Bottom : 0f;
        if (outsideX <= 0f && outsideY <= 0f)
            return false;

        // Dominant escape axis picks the edge; the exact >= makes ties deterministic (horizontal).
        var horizontalEscape = outsideX >= outsideY;
        var radius = MathF.Max(0f, MathF.Min(
            cornerRadius, MathF.Min(bounds.Width, bounds.Height) / 2f));
        if (horizontalEscape)
        {
            var edgeX = tailTip.X < bounds.X ? bounds.X + BaseOverlap : bounds.Right - BaseOverlap;
            var half = BaseHalf(bounds.Height, radius);
            var center = Math.Clamp(
                tailTip.Y, bounds.Y + radius + half, bounds.Bottom - radius - half);
            baseA = new AnnotationPoint(edgeX, center - half);
            baseB = new AnnotationPoint(edgeX, center + half);
        }
        else
        {
            var edgeY = tailTip.Y < bounds.Y ? bounds.Y + BaseOverlap : bounds.Bottom - BaseOverlap;
            var half = BaseHalf(bounds.Width, radius);
            var center = Math.Clamp(
                tailTip.X, bounds.X + radius + half, bounds.Right - radius - half);
            baseA = new AnnotationPoint(center - half, edgeY);
            baseB = new AnnotationPoint(center + half, edgeY);
        }
        return true;
    }

    /// <summary>Point-in-tail test in pre-rotation local coordinates.</summary>
    public static bool HitTail(SpeechBubbleAnnotation bubble, AnnotationPoint point, float tolerance)
    {
        if (!TryGetTail(bubble, out var a, out var b, out var tip))
            return false;
        return PointInTriangle(point, a, b, tip, tolerance);
    }

    private static float BaseHalf(float edgeLength, float radius)
    {
        // Never wider than what fits between the two rounded corners.
        var room = MathF.Max(0f, (edgeLength / 2f) - radius);
        return Math.Clamp(edgeLength * 0.15f, MathF.Min(MinBaseHalf, room), MathF.Min(MaxBaseHalf, room));
    }

    private static bool PointInTriangle(
        AnnotationPoint p, AnnotationPoint a, AnnotationPoint b, AnnotationPoint c, float tolerance)
    {
        // Sign-consistent half-plane test; tolerance grows the triangle by moving the test point
        // check to each edge's distance.
        var d1 = Cross(a, b, p);
        var d2 = Cross(b, c, p);
        var d3 = Cross(c, a, p);
        var hasNegative = d1 < 0f || d2 < 0f || d3 < 0f;
        var hasPositive = d1 > 0f || d2 > 0f || d3 > 0f;
        if (!(hasNegative && hasPositive))
            return true;
        if (tolerance <= 0f)
            return false;
        return DistanceToSegment(p, a, b) <= tolerance
            || DistanceToSegment(p, b, c) <= tolerance
            || DistanceToSegment(p, c, a) <= tolerance;
    }

    private static float Cross(AnnotationPoint a, AnnotationPoint b, AnnotationPoint p) =>
        ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));

    private static float DistanceToSegment(
        AnnotationPoint point, AnnotationPoint start, AnnotationPoint end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 1e-6f)
        {
            var px = point.X - start.X;
            var py = point.Y - start.Y;
            return MathF.Sqrt((px * px) + (py * py));
        }
        var t = Math.Clamp(
            (((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared, 0f, 1f);
        var cx = point.X - (start.X + (t * dx));
        var cy = point.Y - (start.Y + (t * dy));
        return MathF.Sqrt((cx * cx) + (cy * cy));
    }
}
