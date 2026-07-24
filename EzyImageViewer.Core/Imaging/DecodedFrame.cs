namespace EzyImageViewer.Core.Imaging;

/// <summary>
/// 플랫폼 중립 해석 픽셀.
/// 계약: BGRA8·미리 곱한 알파·EXIF 방향 1회 적용 완료. 크기는 방향 적용 후 기준.
/// 프레임이 버퍼를 소유하며 넘길 때 소유권도 이동. 마지막 주인이 반드시 해제.
/// </summary>
public sealed class DecodedFrame : IDisposable
{
    private byte[]? _pixels;
    private readonly int _pixelLength;

    public DecodedFrame(byte[] pixels, int width, int height, int strideBytes, bool hasAlpha)
        : this(pixels, logicalPixelLength: null, width, height, strideBytes, hasAlpha)
    {
    }

    public DecodedFrame(
        byte[] pixels,
        int pixelLength,
        int width,
        int height,
        int strideBytes,
        bool hasAlpha)
        : this(pixels, (int?)pixelLength, width, height, strideBytes, hasAlpha)
    {
    }

    private DecodedFrame(
        byte[] pixels,
        int? logicalPixelLength,
        int width,
        int height,
        int strideBytes,
        bool hasAlpha)
    {
        ArgumentNullException.ThrowIfNull(pixels);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(strideBytes, width * 4);
        var requiredPixelLength = checked(strideBytes * height);
        if (logicalPixelLength is { } suppliedLength && suppliedLength != requiredPixelLength)
        {
            throw new ArgumentException(
                "Logical pixel length must equal stride * height.",
                "pixelLength");
        }
        if (pixels.LongLength < requiredPixelLength)
            throw new ArgumentException("Pixel buffer smaller than stride * height.", nameof(pixels));

        _pixels = pixels;
        _pixelLength = requiredPixelLength;
        Width = width;
        Height = height;
        StrideBytes = strideBytes;
        HasAlpha = hasAlpha;
    }

    public int Width { get; }
    public int Height { get; }
    public int StrideBytes { get; }
    public bool HasAlpha { get; }
    public bool IsDisposed => _pixels is null;

    public ReadOnlySpan<byte> Pixels
    {
        get
        {
            var pixels = _pixels ?? throw new ObjectDisposedException(nameof(DecodedFrame));
            return pixels.AsSpan(0, _pixelLength);
        }
    }

    /// <summary>상호 운용 복사용(예: SKImage.FromPixelCopy). 프레임 수명 밖으로 캐시 금지.</summary>
    public byte[] DangerousGetBuffer() =>
        _pixels ?? throw new ObjectDisposedException(nameof(DecodedFrame));

    public void Dispose() => _pixels = null;
}
