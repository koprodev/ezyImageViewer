using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Infrastructure;

/// <summary>파일 원본이 사라지거나 바뀔 때 허용할 안전한 복구 대체 경로 하나를 제한.</summary>
public static class RecoverySourceFallbackPolicy
{
    public const long MaximumDecodedBytes = 128L * 1024 * 1024;

    public static bool CanEmbedRenderedBackground(
        DocumentSequenceKind sequenceKind,
        bool isReducedPreview,
        PixelSize nativeSize,
        PixelSize decodedSize)
    {
        if (sequenceKind != DocumentSequenceKind.SingleFrame
            || isReducedPreview
            || nativeSize.IsEmpty
            || nativeSize != decodedSize)
        {
            return false;
        }

        return nativeSize.PixelCount <= MaximumDecodedBytes / InputLimits.DisplayBytesPerPixel;
    }
}
