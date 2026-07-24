using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using SkiaSharp;

namespace EzyImageViewer.Rendering;

/// <summary>공유 합성 경로로 편집 문서를 변환 출력 크기 래스터에 평면화.</summary>
public static class DocumentFlattener
{
    /// <summary>출력 표면 바이트 상한(BGRA, 픽셀당 4바이트). 실제 할당 지점의 예산.</summary>
    public const long MaxOutputBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>변환 출력 크기 사전 검사. 상한 초과면 할당 전에 거절.</summary>
    public static PixelSize PreflightOutputSize(DocumentState state, PixelSize nativeSize)
    {
        ArgumentNullException.ThrowIfNull(state);
        var output = TransformEvaluator.Evaluate(state.Transform, nativeSize).OutputSize;
        ValidateOutputBudget(output);
        return output;
    }

    private static void ValidateOutputBudget(PixelSize output)
    {
        long bytes;
        try
        {
            bytes = checked((long)output.Width * output.Height * 4);
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException(
                $"Export output {output.Width}x{output.Height} overflows the byte budget.");
        }
        if (bytes > MaxOutputBytes)
            throw new InvalidOperationException(
                $"Export output {output.Width}x{output.Height} ({bytes:N0} bytes) exceeds the {MaxOutputBytes:N0} byte budget.");
    }

    /// <summary>임시 자르기 작업을 붙여 지정 출력 영역만 평면화. 호출자 상태·기록은 불변.</summary>
    public static SKImage FlattenRegion(
        SKImage frame,
        PixelSize nativeSize,
        DocumentState state,
        RectF region,
        RasterAssetImageCache? assetCache = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        var scoped = state.WithTransform(state.Transform.Append(new CropOp(region)));
        return Flatten(frame, nativeSize, scoped, assetCache);
    }

    public static SKImage Flatten(
        SKImage frame,
        PixelSize nativeSize,
        DocumentState state,
        RasterAssetImageCache? assetCache = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(state);
        var evaluation = TransformEvaluator.Evaluate(state.Transform, nativeSize);
        ValidateOutputBudget(evaluation.OutputSize);
        var info = new SKImageInfo(
            evaluation.OutputSize.Width, evaluation.OutputSize.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info)
            ?? throw new InvalidOperationException(
                $"Could not allocate a {info.Width}x{info.Height} flatten surface.");
        surface.Canvas.Clear(SKColors.Transparent);
        DocumentComposite.Render(
            surface.Canvas, frame, nativeSize, state, evaluation, SKMatrix.Identity,
            assetCache: assetCache);
        return surface.Snapshot();
    }
}
