using EzyImageViewer.Imaging.Skia;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Imaging;

/// <summary>2x1 [빨강, 초록] 표본으로 EXIF 방향 8종 모서리 검증.</summary>
public class SkiaOriginTests
{
    private static readonly SKColor Red = new(0xFF, 0x00, 0x00);
    private static readonly SKColor Green = new(0x00, 0xFF, 0x00);

    private static SKBitmap MakeSource()
    {
        var bitmap = new SKBitmap(2, 1, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.SetPixel(0, 0, Red);
        bitmap.SetPixel(1, 0, Green);
        return bitmap;
    }

    public static TheoryData<SKEncodedOrigin, int, int, string> Cases => new()
    {
        // 방향, 예상 크기, 빨강 예상 위치.
        { SKEncodedOrigin.TopLeft, 2, 1, "0,0" },
        { SKEncodedOrigin.TopRight, 2, 1, "1,0" },      // 좌우 대칭.
        { SKEncodedOrigin.BottomRight, 2, 1, "1,0" },   // 180°
        { SKEncodedOrigin.BottomLeft, 2, 1, "0,0" },    // 상하 대칭.
        { SKEncodedOrigin.LeftTop, 1, 2, "0,0" },       // 90° 시계 + 대칭.
        { SKEncodedOrigin.RightTop, 1, 2, "0,0" },      // 90° 시계.
        { SKEncodedOrigin.RightBottom, 1, 2, "0,1" },   // 90° 반시계 + 대칭.
        { SKEncodedOrigin.LeftBottom, 1, 2, "0,1" },    // 90° 반시계.
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ApplyOrigin_PlacesPixelsPerExifDefinition(SKEncodedOrigin origin, int width, int height, string redAt)
    {
        using var oriented = SkiaImageDecoder.ApplyOrigin(MakeSource(), origin);

        Assert.Equal(width, oriented.Width);
        Assert.Equal(height, oriented.Height);

        var parts = redAt.Split(',');
        var (x, y) = (int.Parse(parts[0]), int.Parse(parts[1]));
        AssertColor(Red, oriented.GetPixel(x, y), origin, "red");

        // 초록 픽셀은 남은 칸.
        var greenX = width == 2 ? 1 - x : x;
        var greenY = height == 2 ? 1 - y : y;
        AssertColor(Green, oriented.GetPixel(greenX, greenY), origin, "green");
    }

    private static void AssertColor(SKColor expected, SKColor actual, SKEncodedOrigin origin, string label)
        => Assert.True(
            expected.Red == actual.Red && expected.Green == actual.Green,
            $"{origin}: {label} expected {expected} but was {actual}");
}
