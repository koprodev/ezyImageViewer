using EzyImageViewer.Core.Documents.Layers;

namespace EzyImageViewer.Rendering;

/// <summary>자르기 초안 순수 기하. 비율과 캔버스 포함을 함께 풀어 미리보기·확정 일치.</summary>
public static class CropGeometry
{
    /// <summary>고정 기준점에서 포인터 방향으로 만든 캔버스 안 사각형. 선택적으로 비율 잠금.</summary>
    public static RectF Constrain(
        (float X, float Y) anchor, (float X, float Y) pointer, float? ratio,
        float canvasWidth, float canvasHeight)
    {
        var dx = pointer.X - anchor.X;
        var dy = pointer.Y - anchor.Y;
        var signX = dx < 0f ? -1f : 1f;
        var signY = dy < 0f ? -1f : 1f;
        var availX = signX > 0f ? canvasWidth - anchor.X : anchor.X;
        var availY = signY > 0f ? canvasHeight - anchor.Y : anchor.Y;
        availX = MathF.Max(0f, availX);
        availY = MathF.Max(0f, availY);

        float width, height;
        if (ratio is { } r)
        {
            // 주 드래그 축을 고른 뒤 전체를 균일 축소해 캔버스 끝에서도 비율 유지.
            width = MathF.Abs(dx);
            height = MathF.Abs(dy);
            if (width / r >= height)
                height = width / r;
            else
                width = height * r;
            if (width > availX)
            {
                width = availX;
                height = width / r;
            }
            if (height > availY)
            {
                height = availY;
                width = height * r;
            }
        }
        else
        {
            width = MathF.Min(MathF.Abs(dx), availX);
            height = MathF.Min(MathF.Abs(dy), availY);
        }

        return RectF.FromCorners(anchor.X, anchor.Y, anchor.X + (signX * width), anchor.Y + (signY * height));
    }
}
