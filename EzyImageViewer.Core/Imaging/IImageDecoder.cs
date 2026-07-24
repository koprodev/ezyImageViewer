using EzyImageViewer.Core.Documents;

namespace EzyImageViewer.Core.Imaging;

public sealed record DecodeRequest(InputLimits Limits, int? PreferredMaxDimension = null)
{
    public static DecodeRequest Default { get; } = new(InputLimits.Default);
}

/// <summary>사용자에게 보여 주는 안정적인 실패 분류.</summary>
public enum ImageLoadFailureKind
{
    CorruptFile,
    CredentialsOrPermissionRequired,
    UnsupportedFeature,
    SystemCodecUnavailable,
    ResourceOrSecurityLimitExceeded,
}

/// <summary>디코더 버그가 아니라 입력 정책 거절일 때 발생.</summary>
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

/// <summary>디코드 프레임, 축소 여부, EXIF 적용 뒤 원본 크기 묶음.</summary>
public readonly record struct DecodeResult(
    DecodedFrame Frame,
    bool IsReduced,
    PixelSize NativeSize,
    int FrameCount = 1,
    IReadOnlyList<DocumentDiagnostic>? Diagnostics = null);

/// <summary>디코더 계약: UI 밖 실행, 취소 준수, EXIF 방향 한 번 적용, BGRA8 premul 출력.</summary>
public interface IImageDecoder
{
    Task<DecodeResult> DecodeAsync(Stream stream, DecodeRequest request, CancellationToken cancellationToken);
}
