using SkiaSharp;

namespace EzyImageViewer.Rendering;

public enum ViewMode
{
    Fit,
    ActualSize,
    Custom,
}

/// <summary>
/// 캔버스 맞춤·실제 크기·기준점 줌·90° 보기 회전 계산.
/// 좌표는 물리 픽셀이며 실제 크기 1.0은 이미지 한 픽셀당 화면 한 픽셀.
/// 뷰포트 변경 시 현재 모드의 불변 조건을 다시 적용.
/// </summary>
public sealed class ViewTransform
{
    public const float MinScale = 0.05f;
    public const float MaxScale = 32f;

    public float Scale { get; private set; } = 1f;
    public SKPoint Offset { get; private set; }
    /// <summary>0/90/180/270도 시계 방향.</summary>
    public int RotationDegrees { get; private set; }
    public ViewMode Mode { get; private set; } = ViewMode.Fit;

    public SKSize Viewport { get; private set; }
    public SKSize ContentSize { get; private set; }

    /// <summary>뷰포트에서 본 콘텐츠 크기. 90/270도면 축 교환.</summary>
    public SKSize RotatedContentSize =>
        RotationDegrees is 90 or 270
            ? new SKSize(ContentSize.Height, ContentSize.Width)
            : ContentSize;

    /// <summary>새 원본은 이전 이미지의 보기 회전을 버리고 0도로 시작.</summary>
    public void SetContent(float width, float height)
    {
        ContentSize = new SKSize(width, height);
        RotationDegrees = 0;
    }

    /// <summary>같은 문서의 출력 크기 변경. 보기 회전·모드는 유지하고 새 캔버스에 재정렬.</summary>
    public void UpdateContentSize(float width, float height)
    {
        if (ContentSize.Width == width && ContentSize.Height == height)
            return;
        ContentSize = new SKSize(width, height);
        if (Mode == ViewMode.Fit)
            FitToViewport();
        else
            CenterInViewport();
    }

    /// <summary>뷰포트 변경 적용 후 현재 모드 불변 조건 복원.</summary>
    public void SetViewport(float width, float height)
    {
        var previous = Viewport;
        Viewport = new SKSize(width, height);
        if (previous == Viewport)
            return;

        switch (Mode)
        {
            case ViewMode.Fit:
                FitToViewport();
                break;
            case ViewMode.ActualSize:
                CenterInViewport();
                break;
            case ViewMode.Custom when previous.Width > 0 && previous.Height > 0:
                // 이전 뷰포트 중심의 콘텐츠 점을 새 중심에도 유지.
                Offset = new SKPoint(
                    Offset.X + (Viewport.Width - previous.Width) / 2f,
                    Offset.Y + (Viewport.Height - previous.Height) / 2f);
                break;
        }
    }

    public void FitToViewport()
    {
        Mode = ViewMode.Fit;
        var rotated = RotatedContentSize;
        if (rotated.Width <= 0 || rotated.Height <= 0 || Viewport.Width <= 0 || Viewport.Height <= 0)
        {
            Scale = 1f;
            Offset = SKPoint.Empty;
            return;
        }
        // 최소 배율은 수동 줌만 제한. 맞춤은 큰 캔버스를 위해 더 작아질 수 있음.
        Scale = Math.Min(Math.Min(Viewport.Width / rotated.Width, Viewport.Height / rotated.Height), MaxScale);
        CenterInViewport();
    }

    /// <summary>지정 배율로 중앙 열기. 1배면 실제 크기 모드로 표시.</summary>
    public void OpenAtScale(float scale)
    {
        // 맞춤과 같은 이유로 최소 배율 없음. 큰 이미지는 화면에 앉으려면 더 작아져야 함.
        Scale = Math.Clamp(scale, float.Epsilon, MaxScale);
        Mode = Scale == 1f ? ViewMode.ActualSize : ViewMode.Custom;
        CenterInViewport();
    }

    /// <summary>이미지 한 픽셀 = 물리 화면 한 픽셀.</summary>
    public void ActualSize()
    {
        Mode = ViewMode.ActualSize;
        Scale = 1f;
        CenterInViewport();
    }

    /// <summary>기준점 아래 콘텐츠를 고정한 채 줌.</summary>
    public void ZoomAt(SKPoint viewAnchor, float factor)
    {
        Mode = ViewMode.Custom;
        var newScale = Math.Clamp(Scale * factor, MinScale, MaxScale);
        factor = newScale / Scale;
        if (Math.Abs(factor - 1f) < float.Epsilon)
            return;
        Offset = new SKPoint(
            viewAnchor.X - factor * (viewAnchor.X - Offset.X),
            viewAnchor.Y - factor * (viewAnchor.Y - Offset.Y));
        Scale = newScale;
    }

    public void Pan(float dx, float dy)
    {
        Mode = ViewMode.Custom;
        Offset = new SKPoint(Offset.X + dx, Offset.Y + dy);
    }

    /// <summary>보기 전용 시계 방향 회전. 맞춤은 재맞춤, 나머지는 재중앙.</summary>
    public void RotateClockwise()
    {
        RotationDegrees = (RotationDegrees + 90) % 360;
        if (Mode == ViewMode.Fit)
            FitToViewport();
        else
            CenterInViewport();
    }

    private void CenterInViewport()
    {
        var rotated = RotatedContentSize;
        Offset = new SKPoint(
            (Viewport.Width - rotated.Width * Scale) / 2f,
            (Viewport.Height - rotated.Height * Scale) / 2f);
    }

    /// <summary>콘텐츠 픽셀을 보기 좌표로 옮기는 중심 회전 행렬.</summary>
    public SKMatrix ToViewMatrix()
    {
        var rotated = RotatedContentSize;
        var matrix = SKMatrix.CreateTranslation(Offset.X, Offset.Y);
        matrix = matrix.PreConcat(SKMatrix.CreateScale(Scale, Scale));
        matrix = matrix.PreConcat(RotationDegrees switch
        {
            90 => SKMatrix.CreateRotationDegrees(90).PostConcat(SKMatrix.CreateTranslation(rotated.Width, 0)),
            180 => SKMatrix.CreateRotationDegrees(180).PostConcat(SKMatrix.CreateTranslation(ContentSize.Width, ContentSize.Height)),
            270 => SKMatrix.CreateRotationDegrees(270).PostConcat(SKMatrix.CreateTranslation(0, rotated.Height)),
            _ => SKMatrix.Identity,
        });
        return matrix;
    }

    public SKPoint ViewToContent(SKPoint viewPoint) =>
        ToViewMatrix().TryInvert(out var inverse) ? inverse.MapPoint(viewPoint) : viewPoint;
}
