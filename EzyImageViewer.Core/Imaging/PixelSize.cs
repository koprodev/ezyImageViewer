namespace EzyImageViewer.Core.Imaging;

/// <summary>Pixel dimensions of a raster surface. Always post-EXIF (§6.3 content space).</summary>
public readonly record struct PixelSize(int Width, int Height)
{
    public long PixelCount => (long)Width * Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}
