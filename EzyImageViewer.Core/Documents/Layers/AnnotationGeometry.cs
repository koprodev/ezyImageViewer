using System.Collections.Immutable;

namespace EzyImageViewer.Core.Documents.Layers;

public static class AnnotationGeometry
{
    private const float Epsilon = 1e-6f;

    public static RectF BoundsOf(ImmutableArray<AnnotationPoint> points)
    {
        if (points.IsDefaultOrEmpty)
            return default;
        var minX = points[0].X;
        var maxX = minX;
        var minY = points[0].Y;
        var maxY = minY;
        for (var i = 1; i < points.Length; i++)
        {
            minX = MathF.Min(minX, points[i].X);
            maxX = MathF.Max(maxX, points[i].X);
            minY = MathF.Min(minY, points[i].Y);
            maxY = MathF.Max(maxY, points[i].Y);
        }
        return new RectF(minX, minY, maxX - minX, maxY - minY);
    }

    public static ImmutableArray<AnnotationPoint> Remap(
        ImmutableArray<AnnotationPoint> points, RectF from, RectF to)
    {
        if (points.IsDefaultOrEmpty)
            return points;
        var builder = ImmutableArray.CreateBuilder<AnnotationPoint>(points.Length);
        foreach (var point in points)
            builder.Add(Remap(point, from, to));
        return builder.MoveToImmutable();
    }

    public static AnnotationPoint Remap(AnnotationPoint point, RectF from, RectF to) => new(
        RemapAxis(point.X, from.X, from.Width, to.X, to.Width),
        RemapAxis(point.Y, from.Y, from.Height, to.Y, to.Height));

    public static bool HitTest(Annotation annotation, float x, float y, float tolerance = 0f)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(tolerance) || tolerance < 0f)
            return false;

        var point = UndoRotation(new AnnotationPoint(x, y), annotation.Bounds, annotation.RotationDegrees);
        return annotation switch
        {
            InkAnnotation ink => HitPolyline(ink.Points, point, tolerance + (ink.StrokeWidth / 2f)),
            LineAnnotation line => DistanceToSegment(point, line.Start, line.End)
                <= tolerance + (line.StrokeWidth / 2f),
            RectangleAnnotation shape when shape.Shape == ShapeKind.Ellipse =>
                HitEllipse(shape.Bounds, point, tolerance),
            _ => Contains(annotation.Bounds, point, tolerance),
        };
    }

    public static bool Intersects(Annotation annotation, RectF selectionBounds)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        AnnotationValidator.ValidateBounds(selectionBounds);
        var quad = Corners(annotation.Bounds, annotation.RotationDegrees);
        if (quad.Any(point => Contains(selectionBounds, point, 0f)))
            return true;

        AnnotationPoint[] selection =
        [
            new(selectionBounds.X, selectionBounds.Y),
            new(selectionBounds.Right, selectionBounds.Y),
            new(selectionBounds.Right, selectionBounds.Bottom),
            new(selectionBounds.X, selectionBounds.Bottom),
        ];
        if (selection.Any(point => Contains(
            annotation.Bounds,
            UndoRotation(point, annotation.Bounds, annotation.RotationDegrees), 0f)))
            return true;

        for (var i = 0; i < 4; i++)
        {
            var q0 = quad[i];
            var q1 = quad[(i + 1) % 4];
            for (var j = 0; j < 4; j++)
            {
                if (SegmentsIntersect(q0, q1, selection[j], selection[(j + 1) % 4]))
                    return true;
            }
        }
        return false;
    }

    public static AnnotationPoint Rotate(AnnotationPoint point, RectF bounds, float degrees)
    {
        if (degrees == 0f)
            return point;
        var radians = degrees * (MathF.PI / 180f);
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var x = point.X - bounds.CenterX;
        var y = point.Y - bounds.CenterY;
        return new AnnotationPoint(
            bounds.CenterX + (x * cos) - (y * sin),
            bounds.CenterY + (x * sin) + (y * cos));
    }

    public static AnnotationPoint UndoRotation(
        AnnotationPoint point, RectF bounds, float degrees) => Rotate(point, bounds, -degrees);

    private static AnnotationPoint[] Corners(RectF bounds, float degrees) =>
    [
        Rotate(new(bounds.X, bounds.Y), bounds, degrees),
        Rotate(new(bounds.Right, bounds.Y), bounds, degrees),
        Rotate(new(bounds.Right, bounds.Bottom), bounds, degrees),
        Rotate(new(bounds.X, bounds.Bottom), bounds, degrees),
    ];

    private static bool SegmentsIntersect(
        AnnotationPoint a, AnnotationPoint b, AnnotationPoint c, AnnotationPoint d)
    {
        var abC = Cross(a, b, c);
        var abD = Cross(a, b, d);
        var cdA = Cross(c, d, a);
        var cdB = Cross(c, d, b);
        if (((abC > Epsilon && abD < -Epsilon) || (abC < -Epsilon && abD > Epsilon))
            && ((cdA > Epsilon && cdB < -Epsilon) || (cdA < -Epsilon && cdB > Epsilon)))
            return true;
        return (MathF.Abs(abC) <= Epsilon && OnSegment(a, b, c))
            || (MathF.Abs(abD) <= Epsilon && OnSegment(a, b, d))
            || (MathF.Abs(cdA) <= Epsilon && OnSegment(c, d, a))
            || (MathF.Abs(cdB) <= Epsilon && OnSegment(c, d, b));
    }

    private static bool OnSegment(AnnotationPoint a, AnnotationPoint b, AnnotationPoint point) =>
        point.X >= MathF.Min(a.X, b.X) - Epsilon
        && point.X <= MathF.Max(a.X, b.X) + Epsilon
        && point.Y >= MathF.Min(a.Y, b.Y) - Epsilon
        && point.Y <= MathF.Max(a.Y, b.Y) + Epsilon;

    private static float Cross(AnnotationPoint a, AnnotationPoint b, AnnotationPoint point) =>
        ((b.X - a.X) * (point.Y - a.Y)) - ((b.Y - a.Y) * (point.X - a.X));

    private static bool HitPolyline(
        ImmutableArray<AnnotationPoint> points, AnnotationPoint point, float tolerance)
    {
        if (points.IsDefaultOrEmpty)
            return false;
        if (points.Length == 1)
            return Distance(point, points[0]) <= tolerance;
        for (var i = 1; i < points.Length; i++)
        {
            if (DistanceToSegment(point, points[i - 1], points[i]) <= tolerance)
                return true;
        }
        return false;
    }

    private static bool HitEllipse(RectF bounds, AnnotationPoint point, float tolerance)
    {
        var rx = (bounds.Width / 2f) + tolerance;
        var ry = (bounds.Height / 2f) + tolerance;
        if (rx <= Epsilon || ry <= Epsilon)
            return Contains(bounds, point, tolerance);
        var dx = (point.X - bounds.CenterX) / rx;
        var dy = (point.Y - bounds.CenterY) / ry;
        return (dx * dx) + (dy * dy) <= 1f;
    }

    private static bool Contains(RectF bounds, AnnotationPoint point, float tolerance) =>
        point.X >= bounds.X - tolerance && point.X <= bounds.Right + tolerance &&
        point.Y >= bounds.Y - tolerance && point.Y <= bounds.Bottom + tolerance;

    private static float DistanceToSegment(AnnotationPoint point, AnnotationPoint start, AnnotationPoint end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= Epsilon)
            return Distance(point, start);
        var t = Math.Clamp(
            (((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared,
            0f,
            1f);
        return Distance(point, new AnnotationPoint(start.X + (t * dx), start.Y + (t * dy)));
    }

    private static float Distance(AnnotationPoint a, AnnotationPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return MathF.Sqrt((dx * dx) + (dy * dy));
    }

    private static float RemapAxis(float value, float fromStart, float fromLength, float toStart, float toLength) =>
        MathF.Abs(fromLength) <= Epsilon
            ? toStart + (toLength / 2f)
            : toStart + (((value - fromStart) / fromLength) * toLength);
}
