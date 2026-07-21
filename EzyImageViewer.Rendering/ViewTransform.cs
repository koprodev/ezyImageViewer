using SkiaSharp;

namespace EzyImageViewer.Rendering;

public enum ViewMode
{
    Fit,
    ActualSize,
    Custom,
}

/// <summary>
/// Pure view-state math for the canvas: fit / actual size / anchored zoom / 90° view rotation.
/// All coordinates are physical pixels (the GL canvas and pointer input are unified to device px),
/// so "actual size" is scale 1.0: one image pixel per physical pixel (FR-VIEW-002).
/// Content space = post-EXIF decoded pixels (rotation here is view-only and never re-applies EXIF).
/// The active <see cref="Mode"/> is re-applied when the viewport changes (resize/DPI):
/// Fit refits, ActualSize re-centers, Custom keeps the viewport-center content point stationary.
/// </summary>
public sealed class ViewTransform
{
    public const float MinScale = 0.05f;
    public const float MaxScale = 32f;

    public float Scale { get; private set; } = 1f;
    public SKPoint Offset { get; private set; }
    /// <summary>0/90/180/270, clockwise.</summary>
    public int RotationDegrees { get; private set; }
    public ViewMode Mode { get; private set; } = ViewMode.Fit;

    public SKSize Viewport { get; private set; }
    public SKSize ContentSize { get; private set; }

    /// <summary>Content size as seen by the viewport (axis swap at 90/270).</summary>
    public SKSize RotatedContentSize =>
        RotationDegrees is 90 or 270
            ? new SKSize(ContentSize.Height, ContentSize.Width)
            : ContentSize;

    /// <summary>New-source reset: view rotation returns to 0 (it belongs to the previous image).</summary>
    public void SetContent(float width, float height)
    {
        ContentSize = new SKSize(width, height);
        RotationDegrees = 0;
    }

    /// <summary>
    /// Same-document content-size change (an edit moved the transform output). Unlike
    /// <see cref="SetContent"/> this preserves the view rotation and the mode: Fit refits to the
    /// new canvas; ActualSize and Custom keep their scale and re-center — an edited canvas has no
    /// stable anchor across output spaces, so centering is the declared policy (ADR-0009).
    /// </summary>
    public void UpdateContentSize(float width, float height)
    {
        if (ContentSize.Width == width && ContentSize.Height == height)
            return;
        ContentSize = new SKSize(width, height);
        if (Mode == ViewMode.Fit)
            FitToViewport();
        else
            CenterInViewport();
    }

    /// <summary>Applies a viewport change and re-establishes the active mode's invariant.</summary>
    public void SetViewport(float width, float height)
    {
        var previous = Viewport;
        Viewport = new SKSize(width, height);
        if (previous == Viewport)
            return;

        switch (Mode)
        {
            case ViewMode.Fit:
                FitToViewport();
                break;
            case ViewMode.ActualSize:
                CenterInViewport();
                break;
            case ViewMode.Custom when previous.Width > 0 && previous.Height > 0:
                // Keep the content point at the old viewport center under the new center.
                Offset = new SKPoint(
                    Offset.X + (Viewport.Width - previous.Width) / 2f,
                    Offset.Y + (Viewport.Height - previous.Height) / 2f);
                break;
        }
    }

    public void FitToViewport()
    {
        Mode = ViewMode.Fit;
        var rotated = RotatedContentSize;
        if (rotated.Width <= 0 || rotated.Height <= 0 || Viewport.Width <= 0 || Viewport.Height <= 0)
        {
            Scale = 1f;
            Offset = SKPoint.Empty;
            return;
        }
        // MinScale bounds manual zoom only: Fit must actually fit, and a max-side output (65,500px
        // logical canvas) needs scales well below the interactive floor.
        Scale = Math.Min(Math.Min(Viewport.Width / rotated.Width, Viewport.Height / rotated.Height), MaxScale);
        CenterInViewport();
    }

    /// <summary>1 image pixel = 1 physical pixel (all coordinates here are physical).</summary>
    public void ActualSize()
    {
        Mode = ViewMode.ActualSize;
        Scale = 1f;
        CenterInViewport();
    }

    /// <summary>Zooms keeping the content point under <paramref name="viewAnchor"/> stationary (FR-VIEW-003).</summary>
    public void ZoomAt(SKPoint viewAnchor, float factor)
    {
        Mode = ViewMode.Custom;
        var newScale = Math.Clamp(Scale * factor, MinScale, MaxScale);
        factor = newScale / Scale;
        if (Math.Abs(factor - 1f) < float.Epsilon)
            return;
        Offset = new SKPoint(
            viewAnchor.X - factor * (viewAnchor.X - Offset.X),
            viewAnchor.Y - factor * (viewAnchor.Y - Offset.Y));
        Scale = newScale;
    }

    public void Pan(float dx, float dy)
    {
        Mode = ViewMode.Custom;
        Offset = new SKPoint(Offset.X + dx, Offset.Y + dy);
    }

    /// <summary>View-only clockwise rotation; Fit refits, other modes re-center (FR-VIEW-005).</summary>
    public void RotateClockwise()
    {
        RotationDegrees = (RotationDegrees + 90) % 360;
        if (Mode == ViewMode.Fit)
            FitToViewport();
        else
            CenterInViewport();
    }

    private void CenterInViewport()
    {
        var rotated = RotatedContentSize;
        Offset = new SKPoint(
            (Viewport.Width - rotated.Width * Scale) / 2f,
            (Viewport.Height - rotated.Height * Scale) / 2f);
    }

    /// <summary>Matrix mapping content pixels to view coordinates (rotation about the content center).</summary>
    public SKMatrix ToViewMatrix()
    {
        var rotated = RotatedContentSize;
        var matrix = SKMatrix.CreateTranslation(Offset.X, Offset.Y);
        matrix = matrix.PreConcat(SKMatrix.CreateScale(Scale, Scale));
        matrix = matrix.PreConcat(RotationDegrees switch
        {
            90 => SKMatrix.CreateRotationDegrees(90).PostConcat(SKMatrix.CreateTranslation(rotated.Width, 0)),
            180 => SKMatrix.CreateRotationDegrees(180).PostConcat(SKMatrix.CreateTranslation(ContentSize.Width, ContentSize.Height)),
            270 => SKMatrix.CreateRotationDegrees(270).PostConcat(SKMatrix.CreateTranslation(0, rotated.Height)),
            _ => SKMatrix.Identity,
        });
        return matrix;
    }

    public SKPoint ViewToContent(SKPoint viewPoint) =>
        ToViewMatrix().TryInvert(out var inverse) ? inverse.MapPoint(viewPoint) : viewPoint;
}
