using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Rendering;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

public class InitialWindowGeometryTests
{
    private static readonly PixelSize Chrome = new(16, 100);
    private static readonly PixelSize Margin = new(24, 80);
    private static readonly PixelSize WorkArea = new(1920, 1040);
    private static readonly PixelSize MinimumWindow = new(800, 600);

    private static InitialWindowLayout Measure(int width, int height) =>
        InitialWindowGeometry.Measure(
            new PixelSize(width, height), Chrome, Margin, WorkArea, MinimumWindow);

    [Fact]
    public void SmallImage_KeepsFullScaleAndAddsMarginAndChrome()
    {
        var layout = Measure(1000, 500);

        Assert.Equal(1f, layout.ContentScale);
        Assert.Equal(1000 + (2 * 24) + 16, layout.WindowSize.Width);
        Assert.Equal(500 + (2 * 80) + 100, layout.WindowSize.Height);
    }

    [Fact]
    public void TallImage_ScalesDownSoTheWindowStaysInsideTheWorkArea()
    {
        var layout = Measure(1200, 4000);

        Assert.True(layout.ContentScale < 1f);
        Assert.True(layout.WindowSize.Height
            <= (int)Math.Round(WorkArea.Height * InitialWindowGeometry.WorkAreaFraction));
        // The margin survives the downscale: it is added on top of the scaled image.
        Assert.Equal(
            (int)Math.Round(4000 * (double)layout.ContentScale) + (2 * 80) + 100,
            layout.WindowSize.Height);
    }

    [Fact]
    public void WideImage_ScalesOnTheConstrainingAxisAndKeepsAspect()
    {
        var layout = Measure(6000, 1000);

        var available = (1920 * InitialWindowGeometry.WorkAreaFraction) - 16 - (2 * 24);
        Assert.Equal(available / 6000d, layout.ContentScale, 3);
        Assert.Equal(
            (int)Math.Round(1920 * InitialWindowGeometry.WorkAreaFraction),
            layout.WindowSize.Width);
        // The flattened image leaves the window under its floor, which then wins.
        Assert.Equal(MinimumWindow.Height, layout.WindowSize.Height);
    }

    [Fact]
    public void TinyImage_IsNotUpscaledAndTheWindowKeepsItsFloor()
    {
        var layout = Measure(64, 64);

        Assert.Equal(1f, layout.ContentScale);
        Assert.Equal(MinimumWindow.Width, layout.WindowSize.Width);
        Assert.Equal(MinimumWindow.Height, layout.WindowSize.Height);
    }

    [Fact]
    public void SmallWorkArea_WinsOverTheMinimumWindow()
    {
        var layout = InitialWindowGeometry.Measure(
            new PixelSize(64, 64), Chrome, Margin, new PixelSize(640, 480), MinimumWindow);

        Assert.True(layout.WindowSize.Width <= (int)Math.Round(640 * InitialWindowGeometry.WorkAreaFraction));
        Assert.True(layout.WindowSize.Height <= (int)Math.Round(480 * InitialWindowGeometry.WorkAreaFraction));
    }

    [Fact]
    public void DegenerateInput_StaysInsideTheWorkArea()
    {
        var layout = InitialWindowGeometry.Measure(
            new PixelSize(0, 0), new PixelSize(-5, -5), new PixelSize(-5, -5),
            new PixelSize(0, 0), new PixelSize(0, 0));

        Assert.True(layout.WindowSize.Width >= 1);
        Assert.True(layout.WindowSize.Height >= 1);
        Assert.True(layout.ContentScale > 0f);
    }

    [Fact]
    public void Center_PlacesTheWindowInsideTheWorkAreaOrigin()
    {
        var (x, y) = InitialWindowGeometry.Center(
            new PixelSize(800, 600), new PixelSize(1920, 1040), -1920, 200);

        Assert.Equal(-1920 + ((1920 - 800) / 2), x);
        Assert.Equal(200 + ((1040 - 600) / 2), y);
    }

    [Fact]
    public void Center_NeverStartsAboveTheWorkAreaCorner()
    {
        var (x, y) = InitialWindowGeometry.Center(
            new PixelSize(4000, 4000), new PixelSize(1920, 1040), 0, 0);

        Assert.Equal(0, x);
        Assert.Equal(0, y);
    }
}
