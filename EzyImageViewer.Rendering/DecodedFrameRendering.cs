using EzyImageViewer.Core.Imaging;
using SkiaSharp;

namespace EzyImageViewer.Rendering;

public static class DecodedFrameRendering
{
    /// <summary>
    /// Copies the frame into an SKImage (BGRA8 premul). Copy semantics keep the render snapshot
    /// independent of the frame's lifetime — the caller may dispose the frame afterwards.
    /// </summary>
    public static SKImage ToSKImage(this DecodedFrame frame)
    {
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        return SKImage.FromPixelCopy(info, frame.DangerousGetBuffer(), frame.StrideBytes)
            ?? throw new InvalidOperationException("Pixel copy failed.");
    }
}
