using EzyImageViewer.Core.Documents.Layers;

namespace EzyImageViewer.Rendering;

/// <summary>
/// Pure geometry for the crop draft (FR-EDIT-001): ratio constraint and canvas containment are
/// solved together — clamping the axes independently after fitting the ratio silently breaks the
/// ratio at the canvas edge. Preview and commit both consume this one result, so what the overlay
/// shows is exactly what the CropOp keeps.
/// </summary>
public static class CropGeometry
{
    /// <summary>
    /// Rectangle from a fixed <paramref name="anchor"/> (inside the canvas) toward
    /// <paramref name="pointer"/>, optionally ratio-locked (width/height), never leaving the
    /// <paramref name="canvasWidth"/>×<paramref name="canvasHeight"/> canvas. Returns a zero-extent
    /// rectangle at the anchor when nothing fits in the dragged direction.
    /// </summary>
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
            // Dominant drag axis wins, then the whole rectangle shrinks uniformly until it fits —
            // the ratio survives the canvas edge.
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
