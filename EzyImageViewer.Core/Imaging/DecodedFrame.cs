namespace EzyImageViewer.Core.Imaging;

/// <summary>
/// Platform-neutral decoded pixels. Contract: BGRA8, premultiplied alpha, EXIF orientation already
/// applied exactly once (dimensions are post-orientation). The frame owns its buffer; ownership is
/// transferred wherever the frame is handed off, and the final owner must dispose.
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

    /// <summary>For interop copies (e.g. SKImage.FromPixelCopy). Do not cache beyond the frame's lifetime.</summary>
    public byte[] DangerousGetBuffer() =>
        _pixels ?? throw new ObjectDisposedException(nameof(DecodedFrame));

    public void Dispose() => _pixels = null;
}
