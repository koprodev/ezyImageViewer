using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Infrastructure;

/// <summary>Bounds the one safe recovery fallback when a file source disappears or changes.</summary>
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
