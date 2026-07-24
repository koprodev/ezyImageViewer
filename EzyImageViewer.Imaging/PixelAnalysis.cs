namespace EzyImageViewer.Imaging;

internal static class PixelAnalysis
{
/// <summary>BGRA8 행의 알파를 조기 종료 방식으로 검사. 체크무늬 표시 판단에 사용(FR-VIEW-007).</summary>
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
