using EzyImageViewer.Rendering;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

public class ViewTransformTests
{
    private static ViewTransform Make(float contentW = 200, float contentH = 100, float viewW = 100, float viewH = 100)
    {
        var transform = new ViewTransform();
        transform.SetContent(contentW, contentH);
        transform.SetViewport(viewW, viewH);
        return transform;
    }

    [Fact]
    public void Fit_ScalesToContainAndCenters()
    {
        var transform = Make();
        transform.FitToViewport();

        Assert.Equal(0.5f, transform.Scale, 3);
        Assert.Equal(0f, transform.Offset.X, 2);
        Assert.Equal(25f, transform.Offset.Y, 2);
    }

    [Fact]
    public void Fit_WithZeroViewport_IsSafe()
    {
        var transform = Make(viewW: 0, viewH: 0);
        transform.FitToViewport();
        Assert.Equal(1f, transform.Scale);
        Assert.False(float.IsNaN(transform.Offset.X));
    }

    [Fact]
    public void ZoomAt_KeepsAnchorContentPointStationary()
    {
        var transform = Make();
        transform.FitToViewport();
        var anchor = new SKPoint(30, 40);
        var before = transform.ViewToContent(anchor);

        transform.ZoomAt(anchor, 1.7f);
        var after = transform.ViewToContent(anchor);

        Assert.Equal(before.X, after.X, 2);
        Assert.Equal(before.Y, after.Y, 2);
    }

    [Fact]
    public void ZoomAt_ClampsToMinAndMax()
    {
        var transform = Make();
        transform.ZoomAt(new SKPoint(0, 0), 1000f);
        Assert.Equal(ViewTransform.MaxScale, transform.Scale);
        transform.ZoomAt(new SKPoint(0, 0), 0.000001f);
        Assert.Equal(ViewTransform.MinScale, transform.Scale);
    }

    [Fact]
    public void Rotate_SwapsEffectiveContentSize()
    {
        var transform = Make(contentW: 200, contentH: 100);
        transform.RotateClockwise();
        Assert.Equal(90, transform.RotationDegrees);
        Assert.Equal(100, transform.RotatedContentSize.Width);
        Assert.Equal(200, transform.RotatedContentSize.Height);

        transform.RotateClockwise();
        transform.RotateClockwise();
        transform.RotateClockwise();
        Assert.Equal(0, transform.RotationDegrees);
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Rotate_MatrixMapsContentCornersInsideRotatedBounds(int turns90)
    {
        var transform = Make(contentW: 200, contentH: 100);
        for (var i = 0; i < turns90 / 90; i++)
            transform.RotateClockwise();
        transform.FitToViewport();

        var matrix = transform.ToViewMatrix();
        SKPoint[] corners =
        [
            matrix.MapPoint(new SKPoint(0, 0)),
            matrix.MapPoint(new SKPoint(200, 0)),
            matrix.MapPoint(new SKPoint(0, 100)),
            matrix.MapPoint(new SKPoint(200, 100)),
        ];

        foreach (var corner in corners)
        {
            Assert.InRange(corner.X, -0.5f, 100.5f);
            Assert.InRange(corner.Y, -0.5f, 100.5f);
        }
    }

    [Fact]
    public void ViewToContent_RoundTripsThroughMatrix()
    {
        var transform = Make();
        transform.FitToViewport();
        transform.RotateClockwise();
        transform.ZoomAt(new SKPoint(50, 50), 1.4f);

        var contentPoint = new SKPoint(120, 60);
        var viewPoint = transform.ToViewMatrix().MapPoint(contentPoint);
        var roundTripped = transform.ViewToContent(viewPoint);

        Assert.Equal(contentPoint.X, roundTripped.X, 1);
        Assert.Equal(contentPoint.Y, roundTripped.Y, 1);
    }

    [Fact]
    public void ActualSize_IsOnePhysicalPixelPerImagePixel()
    {
        var transform = Make();
        transform.ActualSize();

        // Adjacent image pixels must land exactly 1 device pixel apart at any DPI.
        var matrix = transform.ToViewMatrix();
        var p0 = matrix.MapPoint(new SKPoint(10, 10));
        var p1 = matrix.MapPoint(new SKPoint(11, 10));
        Assert.Equal(1f, p1.X - p0.X, 3);
        Assert.Equal(1f, transform.Scale, 3);
        Assert.Equal(ViewMode.ActualSize, transform.Mode);
    }

    [Fact]
    public void SetViewport_InFitMode_Refits()
    {
        var transform = Make();
        transform.FitToViewport();
        Assert.Equal(0.5f, transform.Scale, 3);

        transform.SetViewport(400, 400);
        Assert.Equal(ViewMode.Fit, transform.Mode);
        Assert.Equal(2f, transform.Scale, 3);
    }

    [Fact]
    public void SetViewport_InActualSizeMode_KeepsScaleAndRecenters()
    {
        var transform = Make();
        transform.ActualSize();
        transform.SetViewport(400, 300);

        Assert.Equal(1f, transform.Scale, 3);
        Assert.Equal((400 - 200) / 2f, transform.Offset.X, 2);
        Assert.Equal((300 - 100) / 2f, transform.Offset.Y, 2);
    }

    [Fact]
    public void SetViewport_InCustomMode_KeepsCenterContentPointStationary()
    {
        var transform = Make();
        transform.FitToViewport();
        transform.ZoomAt(new SKPoint(50, 50), 2f);
        Assert.Equal(ViewMode.Custom, transform.Mode);

        var centerBefore = transform.ViewToContent(new SKPoint(50, 50));
        transform.SetViewport(300, 200);
        var centerAfter = transform.ViewToContent(new SKPoint(150, 100));

        Assert.Equal(centerBefore.X, centerAfter.X, 1);
        Assert.Equal(centerBefore.Y, centerAfter.Y, 1);
        Assert.Equal(ViewMode.Custom, transform.Mode);
    }
}
