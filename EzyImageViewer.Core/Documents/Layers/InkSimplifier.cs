using System.Collections.Immutable;

namespace EzyImageViewer.Core.Documents.Layers;

/// <summary>완성된 원본 픽셀 획에 반복형 Ramer-Douglas-Peucker 단순화 적용.</summary>
public static class InkSimplifier
{
    public static ImmutableArray<AnnotationPoint> Simplify(
        IReadOnlyList<AnnotationPoint> points, float tolerance)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (!float.IsFinite(tolerance) || tolerance < 0f)
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        if (points.Count == 0)
            return [];
        if (points.Count > AnnotationValidator.MaxInkPoints)
            throw new ArgumentException($"Stroke exceeds {AnnotationValidator.MaxInkPoints} points.", nameof(points));
        if (points.Count <= 2 || tolerance == 0f)
            return [.. points];

        var keep = new bool[points.Count];
        keep[0] = true;
        keep[^1] = true;
        var stack = new Stack<(int Start, int End)>();
        stack.Push((0, points.Count - 1));
        var toleranceSquared = tolerance * tolerance;

        while (stack.Count > 0)
        {
            var (start, end) = stack.Pop();
            var farthest = -1;
            var maxDistance = toleranceSquared;
            for (var i = start + 1; i < end; i++)
            {
                var distance = DistanceToSegmentSquared(points[i], points[start], points[end]);
                if (distance > maxDistance)
                {
                    maxDistance = distance;
                    farthest = i;
                }
            }
            if (farthest < 0)
                continue;
            keep[farthest] = true;
            stack.Push((start, farthest));
            stack.Push((farthest, end));
        }

        var result = ImmutableArray.CreateBuilder<AnnotationPoint>();
        for (var i = 0; i < points.Count; i++)
        {
            if (keep[i])
                result.Add(points[i]);
        }
        return result.ToImmutable();
    }

    private static float DistanceToSegmentSquared(
        AnnotationPoint point, AnnotationPoint start, AnnotationPoint end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared == 0f)
            return DistanceSquared(point, start);
        var t = Math.Clamp(
            (((point.X - start.X) * dx) + ((point.Y - start.Y) * dy)) / lengthSquared,
            0f,
            1f);
        return DistanceSquared(point, new AnnotationPoint(start.X + (t * dx), start.Y + (t * dy)));
    }

    private static float DistanceSquared(AnnotationPoint a, AnnotationPoint b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return (dx * dx) + (dy * dy);
    }
}
