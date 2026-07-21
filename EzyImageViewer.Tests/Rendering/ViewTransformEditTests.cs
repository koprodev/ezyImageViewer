using EzyImageViewer.Rendering;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

/// <summary>
/// UpdateContentSize is the same-document edit path: unlike SetContent (a new source), it must
/// preserve the view rotation and mode (D3 independence — a document rotation is not a view reset).
/// </summary>
public class ViewTransformEditTests
{
    private static ViewTransform MakeView()
    {
        var view = new ViewTransform();
        view.SetContent(400, 300);
        view.SetViewport(800, 600);
        view.FitToViewport();
        return view;
    }

    [Fact]
    public void SetContent_ResetsViewRotation()
    {
        var view = MakeView();
        view.RotateClockwise();
        Assert.Equal(90, view.RotationDegrees);

        view.SetContent(500, 500);

        Assert.Equal(0, view.RotationDegrees);
    }

    [Fact]
    public void UpdateContentSize_PreservesViewRotation()
    {
        var view = MakeView();
        view.RotateClockwise();
        Assert.Equal(90, view.RotationDegrees);

        view.UpdateContentSize(300, 400);

        Assert.Equal(90, view.RotationDegrees);
        Assert.Equal(new SKSize(300, 400), view.ContentSize);
    }

    [Fact]
    public void UpdateContentSize_InFitMode_Refits()
    {
        var view = MakeView();
        Assert.Equal(ViewMode.Fit, view.Mode);
        var before = view.Scale;

        view.UpdateContentSize(200, 150); // half the content: fit scale doubles

        Assert.Equal(ViewMode.Fit, view.Mode);
        Assert.Equal(before * 2f, view.Scale, 3);
    }

    [Fact]
    public void UpdateContentSize_InActualSize_KeepsScaleOneAndRecenters()
    {
        var view = MakeView();
        view.ActualSize();

        view.UpdateContentSize(200, 150);

        Assert.Equal(ViewMode.ActualSize, view.Mode);
        Assert.Equal(1f, view.Scale);
        Assert.Equal(new SKPoint((800 - 200) / 2f, (600 - 150) / 2f), view.Offset);
    }

    [Fact]
    public void UpdateContentSize_InCustomMode_KeepsScaleAndRecenters()
    {
        var view = MakeView();
        view.ZoomAt(new SKPoint(100, 100), 2f);
        Assert.Equal(ViewMode.Custom, view.Mode);
        var scale = view.Scale;

        view.UpdateContentSize(200, 150);

        Assert.Equal(ViewMode.Custom, view.Mode);
        Assert.Equal(scale, view.Scale);
        Assert.Equal(new SKPoint((800 - 200 * scale) / 2f, (600 - 150 * scale) / 2f), view.Offset);
    }

    [Fact]
    public void UpdateContentSize_WithTheSameSize_ChangesNothing()
    {
        var view = MakeView();
        view.ZoomAt(new SKPoint(10, 10), 1.5f);
        var offset = view.Offset;

        view.UpdateContentSize(400, 300);

        Assert.Equal(offset, view.Offset);
    }

    [Fact]
    public void Fit_OnAMaxSideCanvas_ActuallyFits()
    {
        // MinScale bounds manual zoom only: a 65,500px logical canvas in a 2,560×1,369 viewport
        // needs scale ~0.021, below the interactive floor — Fit must still contain the content.
        var view = new ViewTransform();
        view.SetContent(65_500, 65_500);
        view.SetViewport(2_560, 1_369);
        view.FitToViewport();

        Assert.True(view.ContentSize.Width * view.Scale <= 2_560f + 0.5f);
        Assert.True(view.ContentSize.Height * view.Scale <= 1_369f + 0.5f);
    }
}
