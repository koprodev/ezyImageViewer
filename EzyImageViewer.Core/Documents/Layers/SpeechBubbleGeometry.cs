namespace EzyImageViewer.Core.Documents.Layers;

/// <summary>말풍선 꼬리 모양 단일 기준. 렌더·히트·영역 선택·손잡이가 같은 삼각형 공유.</summary>
public static class SpeechBubbleGeometry
{
    /// <summary>꼬리 밑변을 몸통 안으로 겹치는 거리. 접점 하나짜리 불안정 결합 방지.</summary>
    public const float BaseOverlap = 2f;

    private const float MinBaseHalf = 3f;
    private const float MaxBaseHalf = 12f;

    /// <summary>새 말풍선 기본 꼬리 끝. 몸통 왼쪽 아래 바깥.</summary>
    public static AnnotationPoint DefaultTailTip(RectF bounds) => new(
        bounds.X + (bounds.Width * 0.2f),
        bounds.Bottom + MathF.Max(16f, bounds.Height * 0.35f));

    /// <summary>말풍선 꼬리 삼각형. 끝점이 몸통 안이면 꼬리 없이 false.</summary>
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

        // 더 많이 벗어난 축으로 변 선택. 동률은 가로로 고정.
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

    /// <summary>회전 전 지역 좌표의 꼬리 내부 점 검사.</summary>
    public static bool HitTail(SpeechBubbleAnnotation bubble, AnnotationPoint point, float tolerance)
    {
        if (!TryGetTail(bubble, out var a, out var b, out var tip))
            return false;
        return PointInTriangle(point, a, b, tip, tolerance);
    }

    private static float BaseHalf(float edgeLength, float radius)
    {
        // 둥근 모서리 사이에 들어가는 너비만 허용.
        var room = MathF.Max(0f, (edgeLength / 2f) - radius);
        return Math.Clamp(edgeLength * 0.15f, MathF.Min(MinBaseHalf, room), MathF.Min(MaxBaseHalf, room));
    }

    private static bool PointInTriangle(
        AnnotationPoint p, AnnotationPoint a, AnnotationPoint b, AnnotationPoint c, float tolerance)
    {
        // 부호 일관 반평면 검사. 허용 오차만큼 삼각형을 넓혀 판정.
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
