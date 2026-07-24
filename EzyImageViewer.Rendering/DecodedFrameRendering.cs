using EzyImageViewer.Core.Imaging;
using SkiaSharp;

namespace EzyImageViewer.Rendering;

public static class DecodedFrameRendering
{
    /// <summary>
/// 프레임을 SKImage(BGRA8 미리 곱한 알파)로 복사.
/// 렌더 스냅숏 수명이 프레임과 분리되어 호출자는 이후 프레임을 해제 가능.
    /// </summary>
    public static SKImage ToSKImage(this DecodedFrame frame)
    {
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        return SKImage.FromPixelCopy(info, frame.DangerousGetBuffer(), frame.StrideBytes)
            ?? throw new InvalidOperationException("Pixel copy failed.");
    }
}
