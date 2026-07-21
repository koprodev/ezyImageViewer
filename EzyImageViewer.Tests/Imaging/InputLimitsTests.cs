using EzyImageViewer.Core.Imaging;
using Xunit;

namespace EzyImageViewer.Tests.Imaging;

public class InputLimitsTests
{
    private static readonly InputLimits Limits = new()
    {
        MaxFileBytes = 1000,
        MaxDimension = 10_000,
        HardMaxPixels = 50_000_000,
        DisplayByteBudget = 1_000_000 * InputLimits.DisplayBytesPerPixel,
    };

    [Fact]
    public void FileSize_OverLimit_IsRejected()
    {
        Assert.Equal(DecodeAction.Reject, Limits.PlanFileSize(1001).Action);
        Assert.Equal(DecodeAction.DecodeFull, Limits.PlanFileSize(1000).Action);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(10_001, 10)]
    [InlineData(10, 10_001)]
    public void Dimensions_InvalidOrOversized_AreRejected(int width, int height)
        => Assert.Equal(DecodeAction.Reject, Limits.PlanDimensions(width, height).Action);

    [Fact]
    public void Dimensions_OverflowingArithmetic_IsRejected()
    {
        var extreme = new InputLimits { MaxDimension = int.MaxValue, HardMaxPixels = long.MaxValue };
        Assert.Equal(DecodeAction.Reject, extreme.PlanDimensions(int.MaxValue, int.MaxValue).Action);
    }

    [Fact]
    public void Dimensions_OverHardPixelLimit_AreRejected()
    {
        // 8000 x 7000 = 56MP > 50MP hard limit (both sides within MaxDimension)
        Assert.Equal(DecodeAction.Reject, Limits.PlanDimensions(8000, 7000).Action);
    }

    [Fact]
    public void Dimensions_ExactBudgetBoundary_DecodesFull()
    {
        Assert.Equal(DecodeAction.DecodeFull, Limits.PlanDimensions(1000, 1000).Action);
    }

    [Fact]
    public void Dimensions_OverBudget_GetScaledPlanWithinBudget()
    {
        var plan = Limits.PlanDimensions(2000, 2000);
        Assert.Equal(DecodeAction.DecodeScaled, plan.Action);
        Assert.True(plan.TargetMaxDimension is > 0 and < 2000);
        Assert.True((long)plan.TargetMaxDimension * plan.TargetMaxDimension <= Limits.FullDecodePixelBudget);
    }

    [Fact]
    public void Dimensions_AspectRatio_IsPreservedByUniformTarget()
    {
        var plan = Limits.PlanDimensions(4000, 1000);
        Assert.Equal(DecodeAction.DecodeScaled, plan.Action);
        // Target applies to the longer side; scaled pixel count stays within budget.
        var scale = plan.TargetMaxDimension / 4000.0;
        var pixels = (long)(4000 * scale) * (long)(1000 * scale);
        Assert.True(pixels <= Limits.FullDecodePixelBudget);
    }
}
