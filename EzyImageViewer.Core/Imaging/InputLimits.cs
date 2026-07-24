namespace EzyImageViewer.Core.Imaging;

public enum DecodeAction
{
    DecodeFull,
    DecodeScaled,
    Reject,
}

public readonly record struct DecodePlan(DecodeAction Action, int TargetMaxDimension, string? RejectReason)
{
    public static DecodePlan Full() => new(DecodeAction.DecodeFull, 0, null);
    public static DecodePlan Scaled(int targetMaxDimension) => new(DecodeAction.DecodeScaled, targetMaxDimension, null);
    public static DecodePlan Rejected(string reason) => new(DecodeAction.Reject, 0, reason);
}

/// <summary>픽셀 할당 전 입력 분류. 터무니없으면 거절, 크지만 타당하면 축소 디코드.</summary>
public sealed record InputLimits
{
    /// <summary>표시 픽셀당 보유 바이트. BGRA8 프레임 4 + 렌더 스냅샷 4.</summary>
    public const int DisplayBytesPerPixel = 8;

    public static InputLimits Default { get; } = new();

    public long MaxFileBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>페이지·프레임 순회 전 검사하는 컨테이너 구조 상한.</summary>
    public int MaxFrameCount { get; init; } = 10_000;

    /// <summary>한 변 상식선 상한. stride 계산도 함께 보호.</summary>
    public int MaxDimension { get; init; } = 65_500;

    /// <summary>넘으면 파일 즉시 거절.</summary>
    public long HardMaxPixels { get; init; } = 500_000_000;

    /// <summary>문서 하나의 표시 메모리 상한(프레임 + 스냅샷). 창마다 적용.</summary>
    public long DisplayByteBudget { get; init; } = 384L * 1024 * 1024;

    /// <summary>프레임·스냅샷이 표시 예산 안에 드는 최대 픽셀 수.</summary>
    public long FullDecodePixelBudget => DisplayByteBudget / DisplayBytesPerPixel;

    public DecodePlan PlanFileSize(long fileBytes) =>
        fileBytes > MaxFileBytes
            ? DecodePlan.Rejected($"File size {fileBytes:N0} exceeds the {MaxFileBytes:N0} byte limit.")
            : DecodePlan.Full();

    public DecodePlan PlanDimensions(int width, int height)
    {
        if (width <= 0 || height <= 0)
            return DecodePlan.Rejected($"Invalid dimensions {width}x{height}.");
        if (width > MaxDimension || height > MaxDimension)
            return DecodePlan.Rejected($"Dimensions {width}x{height} exceed the {MaxDimension}px side limit.");

        long pixels, displayBytes;
        try
        {
            pixels = checked((long)width * height);
            displayBytes = checked(pixels * DisplayBytesPerPixel);
        }
        catch (OverflowException)
        {
            return DecodePlan.Rejected($"Dimensions {width}x{height} overflow the pixel budget.");
        }

        if (pixels > HardMaxPixels)
            return DecodePlan.Rejected($"{pixels:N0} pixels exceed the hard limit of {HardMaxPixels:N0}.");
        if (displayBytes <= DisplayByteBudget)
            return DecodePlan.Full();

        // 비율을 지키며 프레임·스냅샷이 예산에 드는 최대 한 변 계산.
        var scale = Math.Sqrt((double)DisplayByteBudget / displayBytes);
        var target = (int)Math.Max(1, Math.Floor(Math.Max(width, height) * scale));
        return DecodePlan.Scaled(target);
    }
}
