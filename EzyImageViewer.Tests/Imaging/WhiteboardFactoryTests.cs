using EzyImageViewer.Imaging;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Imaging;

public sealed class WhiteboardFactoryTests
{
    [Theory]
    [InlineData(WhiteboardStyle.White)]
    [InlineData(WhiteboardStyle.Black)]
    public void CreatePng_ProducesFixed4KOpaqueCanvas(WhiteboardStyle style)
    {
        var png = WhiteboardFactory.CreatePng(style);

        using var bitmap = SKBitmap.Decode(png);
        Assert.NotNull(bitmap);
        Assert.Equal(3840, bitmap.Width);
        Assert.Equal(2160, bitmap.Height);

        var background = style == WhiteboardStyle.Black ? SKColors.Black : SKColors.White;
        // 셀 안쪽은 단색 배경 유지. 격자선은 셀 경계에만 존재.
        Assert.Equal(background, bitmap.GetPixel(10, 10));
        Assert.Equal(background, bitmap.GetPixel(3830, 2150));
        Assert.Equal((byte)255, bitmap.GetPixel(0, 0).Alpha);
    }

    [Theory]
    [InlineData(WhiteboardStyle.White)]
    [InlineData(WhiteboardStyle.Black)]
    public void CreatePng_BakesTheGridAtTheFixedPitch(WhiteboardStyle style)
    {
        var png = WhiteboardFactory.CreatePng(style);
        using var bitmap = SKBitmap.Decode(png);
        var background = style == WhiteboardStyle.Black ? SKColors.Black : SKColors.White;

        for (var cellIndex = 1; cellIndex <= 3; cellIndex++)
        {
            var boundary = cellIndex * WhiteboardFactory.GridCellSize;
            Assert.NotEqual(background, bitmap.GetPixel(boundary, 10));
            Assert.NotEqual(background, bitmap.GetPixel(10 + cellIndex, boundary));
        }
        // 경계가 아닌 행·열에는 선이 없음.
        Assert.Equal(background, bitmap.GetPixel(WhiteboardFactory.GridCellSize + 5, 10));
        Assert.Equal(background, bitmap.GetPixel(10, WhiteboardFactory.GridCellSize + 5));
    }
}
