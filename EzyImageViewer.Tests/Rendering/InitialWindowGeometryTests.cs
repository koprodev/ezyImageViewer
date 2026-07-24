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
        // 여백은 축소 뒤에도 유지. 축소된 이미지 크기에 더함.
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
        // 평탄화 이미지 기준 창 크기가 하한보다 작아 하한이 이김.
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

        // 아직 안 보인 창은 장식 크기를 모름.
        // 클라이언트 영역을 한 번 정하고 비클라이언트 프레임을 읽은 뒤 바깥 창 공간에서 다시 측정.
        // 두 번째 측정이 작업 영역 상한과 최소 창 크기를 책임짐.
    private static readonly PixelSize NonClientFrame = new(16, 47);
    private static readonly PixelSize StatusBar = new(0, 44);

    private static InitialWindowLayout MeasureClientFirst(int width, int height, PixelSize workArea)
    {
        var image = new PixelSize(width, height);
        var client = InitialWindowGeometry.Measure(image, StatusBar, Margin, workArea, MinimumWindow);
        Assert.True(client.WindowSize.Width >= 1 && client.WindowSize.Height >= 1);
        return InitialWindowGeometry.Measure(
            image,
            new PixelSize(NonClientFrame.Width, NonClientFrame.Height + StatusBar.Height),
            Margin,
            workArea,
            MinimumWindow);
    }

    [Fact]
    public void ClientFirstMeasure_LeavesTheImageAndMarginsInsideTheClientArea()
    {
        var layout = MeasureClientFirst(1000, 500, WorkArea);

        Assert.Equal(1f, layout.ContentScale);
        // 캔버스 = 창 - 프레임 - 상태 막대. 이미지와 양쪽 여백은 여전히 들어감.
        Assert.Equal(1000 + (2 * Margin.Width), layout.WindowSize.Width - NonClientFrame.Width);
        Assert.Equal(
            500 + (2 * Margin.Height),
            layout.WindowSize.Height - NonClientFrame.Height - StatusBar.Height);
    }

    [Fact]
    public void ClientFirstMeasure_CapsTheOuterWindowNotTheClientArea()
    {
        var layout = MeasureClientFirst(6000, 4000, WorkArea);

        // 상한은 프레임까지 포함해 사용자가 보는 창 크기에 대한 약속.
        Assert.True(layout.WindowSize.Width
            <= (int)Math.Round(WorkArea.Width * InitialWindowGeometry.WorkAreaFraction));
        Assert.True(layout.WindowSize.Height
            <= (int)Math.Round(WorkArea.Height * InitialWindowGeometry.WorkAreaFraction));
        Assert.True(layout.ContentScale < 1f);
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
