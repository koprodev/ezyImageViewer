namespace EzyImageViewer.Imaging;

internal static class PixelAnalysis
{
    /// <summary>Early-exit alpha scan on BGRA8 rows (drives checkerboard display, FR-VIEW-007).</summary>
    public static bool HasTransparency(byte[] bgra, int strideBytes, int width, int height)
    {
        for (var y = 0; y < height; y++)
        {
            var row = y * strideBytes;
            for (var x = 0; x < width; x++)
            {
                if (bgra[row + x * 4 + 3] != 0xFF)
                    return true;
            }
        }
        return false;
    }
}
