using EzyImageViewer.Core.Documents;

namespace EzyImageViewer.Core.Imaging;

public sealed record DecodeRequest(InputLimits Limits, int? PreferredMaxDimension = null)
{
    public static DecodeRequest Default { get; } = new(InputLimits.Default);
}

/// <summary>Stable user-facing failure categories from requirements §8.5.</summary>
public enum ImageLoadFailureKind
{
    CorruptFile,
    CredentialsOrPermissionRequired,
    UnsupportedFeature,
    SystemCodecUnavailable,
    ResourceOrSecurityLimitExceeded,
}

/// <summary>Raised when the input is refused by policy — not a decoder bug.</summary>
public class ImageRejectedException : Exception
{
    public ImageRejectedException(string message)
        : this(ImageLoadFailureKind.ResourceOrSecurityLimitExceeded, message)
    {
    }

    public ImageRejectedException(
        ImageLoadFailureKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public ImageLoadFailureKind Kind { get; }
}

public sealed class UnsupportedFormatException(string message)
    : ImageRejectedException(ImageLoadFailureKind.UnsupportedFeature, message);

public sealed class CorruptImageException(string message, Exception? innerException = null)
    : ImageRejectedException(ImageLoadFailureKind.CorruptFile, message, innerException);

public sealed class ProtectedDocumentException(string message, Exception? innerException = null)
    : ImageRejectedException(ImageLoadFailureKind.CredentialsOrPermissionRequired, message, innerException);

public sealed class CodecUnavailableException(string message, Exception? innerException = null)
    : ImageRejectedException(ImageLoadFailureKind.SystemCodecUnavailable, message, innerException);

public sealed class SecurityLimitExceededException(string message, Exception? innerException = null)
    : ImageRejectedException(ImageLoadFailureKind.ResourceOrSecurityLimitExceeded, message, innerException);

/// <summary>
/// The frame, whether it was decoded at reduced size under the pixel budget, and the source's own
/// post-EXIF size. <paramref name="NativeSize"/> is what annotation geometry is expressed in, so a
/// reduced preview and a later full-resolution decode of the same file address identical
/// coordinates (ADR-0008); it equals the frame size unless <paramref name="IsReduced"/>.
/// </summary>
public readonly record struct DecodeResult(
    DecodedFrame Frame,
    bool IsReduced,
    PixelSize NativeSize,
    int FrameCount = 1,
    IReadOnlyList<DocumentDiagnostic>? Diagnostics = null);

/// <summary>
/// Decode contract (ADR-0006 abstraction point): implementations must run off the UI thread,
/// honor cancellation, apply EXIF orientation exactly once, and emit BGRA8 premultiplied pixels.
/// </summary>
public interface IImageDecoder
{
    Task<DecodeResult> DecodeAsync(Stream stream, DecodeRequest request, CancellationToken cancellationToken);
}
