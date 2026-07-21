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

/// <summary>
/// Input hardening policy (requirements §6.4/§11). Classification is decided before any pixel
/// allocation: absurd inputs are rejected, oversized-but-plausible inputs get a scaled decode.
/// The budget is expressed in bytes; the pixel ceiling is derived from it, not chosen.
/// </summary>
public sealed record InputLimits
{
    /// <summary>
    /// Bytes one displayed pixel retains: the BGRA8 frame (4) plus the render snapshot copy it is
    /// uploaded through (4, ADR-0007). A document swap transiently doubles this while the
    /// predecessor is released; the annotation history holds command payloads, not pixels (ADR-0008).
    /// </summary>
    public const int DisplayBytesPerPixel = 8;

    public static InputLimits Default { get; } = new();

    public long MaxFileBytes { get; init; } = 512L * 1024 * 1024;

    /// <summary>Container structure cap checked before page/frame traversal.</summary>
    public int MaxFrameCount { get; init; } = 10_000;

    /// <summary>Per-side sanity bound (also protects stride math).</summary>
    public int MaxDimension { get; init; } = 65_500;

    /// <summary>Above this the file is refused outright.</summary>
    public long HardMaxPixels { get; init; } = 500_000_000;

    /// <summary>
    /// Steady-state ceiling for one document's display memory (frame + snapshot). 384MB is the
    /// per-window budget; N windows cost N times this (process-wide arbitration is M9).
    /// </summary>
    public long DisplayByteBudget { get; init; } = 384L * 1024 * 1024;

    /// <summary>Largest pixel count whose frame + snapshot fit <see cref="DisplayByteBudget"/>.</summary>
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

        // Largest side length whose frame + snapshot stay inside the budget, preserving aspect ratio.
        var scale = Math.Sqrt((double)DisplayByteBudget / displayBytes);
        var target = (int)Math.Max(1, Math.Floor(Math.Max(width, height) * scale));
        return DecodePlan.Scaled(target);
    }
}
