using EzyImageViewer.Imaging.Skia;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Imaging;

/// <summary>
/// Pixel-corner verification of all 8 EXIF origins. Source is 2x1: [red, green].
/// Expected positions follow the EXIF orientation definitions (row0/col0 placement).
/// </summary>
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
        // origin, expected size, expected red position
        { SKEncodedOrigin.TopLeft, 2, 1, "0,0" },
        { SKEncodedOrigin.TopRight, 2, 1, "1,0" },      // mirrored horizontally
        { SKEncodedOrigin.BottomRight, 2, 1, "1,0" },   // 180°
        { SKEncodedOrigin.BottomLeft, 2, 1, "0,0" },    // mirrored vertically
        { SKEncodedOrigin.LeftTop, 1, 2, "0,0" },       // 90° CW + mirror
        { SKEncodedOrigin.RightTop, 1, 2, "0,0" },      // 90° CW
        { SKEncodedOrigin.RightBottom, 1, 2, "0,1" },   // 90° CCW + mirror
        { SKEncodedOrigin.LeftBottom, 1, 2, "0,1" },    // 90° CCW
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

        // The green pixel occupies the remaining cell.
        var greenX = width == 2 ? 1 - x : x;
        var greenY = height == 2 ? 1 - y : y;
        AssertColor(Green, oriented.GetPixel(greenX, greenY), origin, "green");
    }

    private static void AssertColor(SKColor expected, SKColor actual, SKEncodedOrigin origin, string label)
        => Assert.True(
            expected.Red == actual.Red && expected.Green == actual.Green,
            $"{origin}: {label} expected {expected} but was {actual}");
}
