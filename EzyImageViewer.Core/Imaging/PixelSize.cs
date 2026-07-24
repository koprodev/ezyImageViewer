namespace EzyImageViewer.Core.Imaging;

/// <summary>래스터 표면의 픽셀 크기. 항상 EXIF 방향 적용 후 기준(§6.3 콘텐츠 공간).</summary>
public readonly record struct PixelSize(int Width, int Height)
{
    public long PixelCount => (long)Width * Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}
