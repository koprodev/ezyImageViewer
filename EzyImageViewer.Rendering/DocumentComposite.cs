using System.Numerics;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using SkiaSharp;

namespace EzyImageViewer.Rendering;

/// <summary>
/// 화면·골든 테스트·내보내기가 공유하는 단일 문서 합성 경로.
/// 프레임 → 원본 → 변환 출력 → 대상 순이며 배경·주석이 같은 원본 클립 사용.
/// </summary>
public static class DocumentComposite
{
    /// <summary>행 벡터 Matrix3x2를 Skia 열 벡터 형식으로 변환.</summary>
    public static SKMatrix ToSKMatrix(in Matrix3x2 m) =>
        new(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, 0f, 0f, 1f);

    /// <summary>합성 문서 그리기. 출력→대상 행렬은 캔버스 기본 행렬까지 포함.</summary>
    public static void Render(
        SKCanvas canvas,
        SKImage frame,
        PixelSize nativeSize,
        DocumentState state,
        TransformEvaluation evaluation,
        SKMatrix outputToDestination,
        Guid selectedId = default,
        RasterAssetImageCache? assetCache = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(evaluation);

        if (evaluation.SourceClip.Count < 3)
            return; // 자르기가 투명 여백만 남겼으면 그릴 것도 없음.

        var nativeToDestination = outputToDestination.PreConcat(ToSKMatrix(evaluation.NativeToOutput));

        canvas.Save();
        // 미리보기와 내보내기 범위를 맞추려고 논리 캔버스부터 자름.
        using (var outputRect = BuildOutputPath(evaluation.OutputSize, outputToDestination))
        {
            canvas.ClipPath(outputRect, SKClipOperation.Intersect, antialias: true);
        }
        using (var clip = BuildClipPath(evaluation.SourceClip, nativeToDestination))
        {
            canvas.ClipPath(clip, SKClipOperation.Intersect, antialias: true);
        }

        // 배경은 프레임 → 원본 변환을 더해 축소 디코드 배율 복원.
        var frameToNative = SKMatrix.CreateScale(
            nativeSize.Width / (float)frame.Width, nativeSize.Height / (float)frame.Height);
        canvas.Save();
        // 지운 영역은 배경만 뚫음. 위 주석은 남고 체크보드가 비침.
        if (evaluation.ErasedNative.Count > 0)
        {
            using var punched = BuildErasePath(evaluation.ErasedNative, nativeToDestination);
            canvas.ClipPath(punched, SKClipOperation.Difference, antialias: true);
        }
        canvas.SetMatrix(nativeToDestination.PreConcat(frameToNative));
        using (var paint = new SKPaint { IsAntialias = false })
        {
            canvas.DrawImage(frame, 0f, 0f, new SKSamplingOptions(SKFilterMode.Linear), paint);
        }
        canvas.Restore();

        // 같은 클립을 유지해 잘린 원본 밖 주석도 숨김. 보호 효과용 실제 배경 프레임은 전달.
        AnnotationRendering.DrawAnnotations(
            canvas, state, nativeToDestination, assetCache: assetCache,
            backgroundFrame: frame, frameToNative: frameToNative);
        canvas.Restore();

        // 선택 손잡이는 문서 픽셀이 아닌 UI라 원본 클립 밖에서도 보여야 조작 가능.
        if (selectedId != default && state.IsEffectivelyVisible(selectedId)
            && state.Find(selectedId) is { } selected)
            AnnotationRendering.DrawSelection(canvas, selected, nativeToDestination);
    }

    private static SKPath BuildOutputPath(PixelSize outputSize, SKMatrix outputToDestination)
    {
        using var path = new SKPathBuilder();
        path.MoveTo(outputToDestination.MapPoint(0f, 0f));
        path.LineTo(outputToDestination.MapPoint(outputSize.Width, 0f));
        path.LineTo(outputToDestination.MapPoint(outputSize.Width, outputSize.Height));
        path.LineTo(outputToDestination.MapPoint(0f, outputSize.Height));
        path.Close();
        return path.Detach();
    }

    private static SKPath BuildErasePath(
        IReadOnlyList<IReadOnlyList<Vector2>> erasedNative, SKMatrix nativeToDestination)
    {
        using var path = new SKPathBuilder();
        foreach (var quad in erasedNative)
        {
            for (var i = 0; i < quad.Count; i++)
            {
                var point = nativeToDestination.MapPoint(quad[i].X, quad[i].Y);
                if (i == 0)
                    path.MoveTo(point);
                else
                    path.LineTo(point);
            }
            path.Close();
        }
        return path.Detach();
    }

    private static SKPath BuildClipPath(IReadOnlyList<Vector2> sourceClip, SKMatrix nativeToDestination)
    {
        using var path = new SKPathBuilder();
        for (var i = 0; i < sourceClip.Count; i++)
        {
            var point = nativeToDestination.MapPoint(sourceClip[i].X, sourceClip[i].Y);
            if (i == 0)
                path.MoveTo(point);
            else
                path.LineTo(point);
        }
        path.Close();
        return path.Detach();
    }
}
