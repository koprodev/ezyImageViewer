using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Core.Documents.Layers;

/// <summary>
/// 불변 배경 변환 하나. 앞선 작업의 출력 좌표로 정의.
/// 행렬·출력 크기·클립은 저장하지 않고 <see cref="TransformEvaluator"/>가 계산.
/// </summary>
public abstract record TransformOp
{
    private protected TransformOp() { }

    /// <summary>기록 항목 안에서 작업 하나가 보유하는 바이트.</summary>
    public const long EstimatedRetainedBytes = 40;
}

/// <summary>앞선 작업의 출력 좌표에서 <see cref="Bounds"/>만 남김.</summary>
public sealed record CropOp : TransformOp
{
    public CropOp(RectF bounds)
    {
        if (!float.IsFinite(bounds.X) || !float.IsFinite(bounds.Y)
            || !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height))
            throw new ArgumentOutOfRangeException(nameof(bounds), "Crop bounds must be finite.");
        if (bounds.Width <= 0f || bounds.Height <= 0f)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Crop bounds must have positive extent.");
        Bounds = bounds;
    }

    public RectF Bounds { get; }
}

/// <summary>앞선 출력 좌표의 영역을 투명하게 비움. 캔버스 크기는 유지.</summary>
public sealed record EraseOp : TransformOp
{
    public EraseOp(RectF bounds)
    {
        if (!float.IsFinite(bounds.X) || !float.IsFinite(bounds.Y)
            || !float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height))
            throw new ArgumentOutOfRangeException(nameof(bounds), "Erase bounds must be finite.");
        if (bounds.Width <= 0f || bounds.Height <= 0f)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Erase bounds must have positive extent.");
        Bounds = bounds;
    }

    public RectF Bounds { get; }
}

/// <summary>현재 캔버스 중심을 기준으로 시계 방향 회전.</summary>
public sealed record RotateOp : TransformOp
{
    /// <summary>사용자 입력은 double에서 먼저 정규화해 float 변환 오버플로 방지.</summary>
    public static RotateOp FromDegrees(double degrees)
    {
        if (!double.IsFinite(degrees))
            throw new ArgumentOutOfRangeException(nameof(degrees), "Rotation must be finite.");
        return new RotateOp((float)(degrees % 360d));
    }

    public RotateOp(float degrees)
    {
        if (!float.IsFinite(degrees))
            throw new ArgumentOutOfRangeException(nameof(degrees), "Rotation must be finite.");
        var normalized = degrees % 360f;
        if (normalized < 0f)
            normalized += 360f;
        // 아주 작은 음수 나머지가 360f로 반올림되면 직각 회전 행세를 하니 0으로 되감음.
        if (normalized >= 360f)
            normalized -= 360f;
        Degrees = normalized;
    }

    /// <summary>[0, 360) 범위로 정규화된 각도.</summary>
    public float Degrees { get; }

    /// <summary>직각 회전은 삼각함수 없이 정확한 정수 경로 사용.</summary>
    public bool IsQuarterTurn => Degrees % 90f == 0f;
}

/// <summary>캔버스 중심축 대칭. 가로면 좌우, 아니면 상하.</summary>
public sealed record FlipOp(bool Horizontal) : TransformOp;

/// <summary>캔버스를 지정 출력 크기로 조정. 배율보다 사용자가 정한 크기를 저장.</summary>
public sealed record ResizeOp : TransformOp
{
    public ResizeOp(PixelSize target)
    {
        if (target.Width < 1 || target.Height < 1)
            throw new ArgumentOutOfRangeException(nameof(target), "Resize target must be at least 1×1.");
        if (target.Width > TransformEvaluator.MaxOutputDimension || target.Height > TransformEvaluator.MaxOutputDimension)
            throw new ArgumentOutOfRangeException(nameof(target),
                $"Resize target exceeds the {TransformEvaluator.MaxOutputDimension}px side limit.");
        Target = target;
    }

    public PixelSize Target { get; }
}
