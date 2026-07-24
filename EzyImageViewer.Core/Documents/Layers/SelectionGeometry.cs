namespace EzyImageViewer.Core.Documents.Layers;

public enum SelectionHandle
{
    None,
    NorthWest,
    North,
    NorthEast,
    East,
    SouthEast,
    South,
    SouthWest,
    West,
    Rotate,
/// <summary>말풍선 꼬리 끝(FR-ANNO-007). 말풍선만 제공.</summary>
    Tail,
}

public static class SelectionGeometry
{
    public const float MinimumExtent = 1f;

    public static AnnotationPoint HandlePoint(
        Annotation annotation, SelectionHandle handle, float rotationOffset)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var bounds = annotation.Bounds;
        var point = handle switch
        {
            SelectionHandle.NorthWest => new AnnotationPoint(bounds.X, bounds.Y),
            SelectionHandle.North => new AnnotationPoint(bounds.CenterX, bounds.Y),
            SelectionHandle.NorthEast => new AnnotationPoint(bounds.Right, bounds.Y),
            SelectionHandle.East => new AnnotationPoint(bounds.Right, bounds.CenterY),
            SelectionHandle.SouthEast => new AnnotationPoint(bounds.Right, bounds.Bottom),
            SelectionHandle.South => new AnnotationPoint(bounds.CenterX, bounds.Bottom),
            SelectionHandle.SouthWest => new AnnotationPoint(bounds.X, bounds.Bottom),
            SelectionHandle.West => new AnnotationPoint(bounds.X, bounds.CenterY),
            SelectionHandle.Rotate => new AnnotationPoint(bounds.CenterX, bounds.Y - rotationOffset),
            SelectionHandle.Tail when annotation is SpeechBubbleAnnotation bubble => bubble.TailTip,
            _ => new AnnotationPoint(bounds.CenterX, bounds.CenterY),
        };
        return AnnotationGeometry.Rotate(point, bounds, annotation.RotationDegrees);
    }

    /// <summary><paramref name="handle"/>이 이 개체에 실제로 있으면 true.
    /// 꼬리 핸들은 말풍선 전용. 다른 개체에 허용하면 중심에 유령 핸들이 생김.</summary>
    public static bool HandleApplies(Annotation annotation, SelectionHandle handle) =>
        handle != SelectionHandle.Tail || annotation is SpeechBubbleAnnotation;

    public static SelectionHandle HitTest(
        Annotation annotation, AnnotationPoint point, float radius, float rotationOffset)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (!float.IsFinite(radius) || radius < 0f)
            throw new ArgumentOutOfRangeException(nameof(radius));
        foreach (var handle in Enum.GetValues<SelectionHandle>())
        {
            if (handle == SelectionHandle.None || !HandleApplies(annotation, handle))
                continue;
            var target = HandlePoint(annotation, handle, rotationOffset);
            var dx = point.X - target.X;
            var dy = point.Y - target.Y;
            if ((dx * dx) + (dy * dy) <= radius * radius)
                return handle;
        }
        return SelectionHandle.None;
    }

    public static Annotation Resize(Annotation annotation, SelectionHandle handle, AnnotationPoint pointer)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var (horizontal, vertical) = Direction(handle);
        if (horizontal == 0 && vertical == 0)
            throw new ArgumentOutOfRangeException(nameof(handle), handle, "Handle does not resize.");

        var bounds = annotation.Bounds;
        var radians = annotation.RotationDegrees * (MathF.PI / 180f);
        var ux = new AnnotationPoint(MathF.Cos(radians), MathF.Sin(radians));
        var uy = new AnnotationPoint(-ux.Y, ux.X);
        var anchor = AnnotationGeometry.Rotate(
            new AnnotationPoint(
                horizontal < 0 ? bounds.Right : horizontal > 0 ? bounds.X : bounds.CenterX,
                vertical < 0 ? bounds.Bottom : vertical > 0 ? bounds.Y : bounds.CenterY),
            bounds, annotation.RotationDegrees);

        var dx = pointer.X - anchor.X;
        var dy = pointer.Y - anchor.Y;
        var width = horizontal == 0
            ? bounds.Width
            : MathF.Max(MinimumExtent, (dx * ux.X + dy * ux.Y) * horizontal);
        var height = vertical == 0
            ? bounds.Height
            : MathF.Max(MinimumExtent, (dx * uy.X + dy * uy.Y) * vertical);
        var centerX = anchor.X
            + (horizontal == 0 ? 0f : ux.X * width * horizontal / 2f)
            + (vertical == 0 ? 0f : uy.X * height * vertical / 2f);
        var centerY = anchor.Y
            + (horizontal == 0 ? 0f : ux.Y * width * horizontal / 2f)
            + (vertical == 0 ? 0f : uy.Y * height * vertical / 2f);
        var next = new RectF(centerX - (width / 2f), centerY - (height / 2f), width, height);
        return annotation.WithBounds(next);
    }

    /// <summary>말풍선 꼬리 끝 드래그.
    /// 회전된 문서 공간의 포인터를 적중 검사와 같은 방식으로 역회전해 로컬 좌표에 저장.</summary>
    public static Annotation MoveTail(Annotation annotation, AnnotationPoint pointer)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        if (annotation is not SpeechBubbleAnnotation bubble)
            throw new ArgumentOutOfRangeException(
                nameof(annotation), annotation.GetType().Name, "Only speech bubbles have a tail.");
        if (!float.IsFinite(pointer.X) || !float.IsFinite(pointer.Y))
            return annotation;
        var local = AnnotationGeometry.UndoRotation(
            pointer, bubble.Bounds, bubble.RotationDegrees);
        return bubble with { TailTip = local };
    }

    public static Annotation Rotate(Annotation annotation, AnnotationPoint pointer)
    {
        ArgumentNullException.ThrowIfNull(annotation);
        var dx = pointer.X - annotation.Bounds.CenterX;
        var dy = pointer.Y - annotation.Bounds.CenterY;
        if (MathF.Abs(dx) < float.Epsilon && MathF.Abs(dy) < float.Epsilon)
            return annotation;
        var degrees = (MathF.Atan2(dy, dx) * 180f / MathF.PI) + 90f;
        degrees %= 360f;
        if (degrees < 0f)
            degrees += 360f;
        return annotation with { RotationDegrees = degrees };
    }

    private static (int Horizontal, int Vertical) Direction(SelectionHandle handle) => handle switch
    {
        SelectionHandle.NorthWest => (-1, -1),
        SelectionHandle.North => (0, -1),
        SelectionHandle.NorthEast => (1, -1),
        SelectionHandle.East => (1, 0),
        SelectionHandle.SouthEast => (1, 1),
        SelectionHandle.South => (0, 1),
        SelectionHandle.SouthWest => (-1, 1),
        SelectionHandle.West => (-1, 0),
        _ => (0, 0),
    };
}
