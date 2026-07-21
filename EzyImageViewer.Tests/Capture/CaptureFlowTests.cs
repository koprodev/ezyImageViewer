using EzyImageViewer.Capture.Snipping;
using Xunit;

namespace EzyImageViewer.Tests.Capture;

/// <summary>Capture ingestion policy (FR-CAP-003/005/006): armed auto-open, passive notify,
/// internal-echo suppression and the watch toggle.</summary>
public sealed class CaptureFlowTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void InternalEcho_IsAlwaysIgnored_EvenWhileArmed()
    {
        var flow = new CaptureFlow();
        flow.Arm(T0);

        Assert.Equal(CaptureDecision.Ignore, flow.OnClipboardImage(isInternalEcho: true, T0));
        // The echo must not consume the armed window — the real capture is still coming.
        Assert.True(flow.IsArmed(T0));
    }

    [Fact]
    public void ArmedImage_AutoOpensOnce_ThenTheNextOneOnlyNotifies()
    {
        var flow = new CaptureFlow();
        flow.Arm(T0);

        Assert.Equal(CaptureDecision.AutoOpen, flow.OnClipboardImage(false, T0));
        Assert.Equal(CaptureDecision.Notify, flow.OnClipboardImage(false, T0));
    }

    [Fact]
    public void ArmExpires_AfterTheWindow()
    {
        var flow = new CaptureFlow();
        flow.Arm(T0);

        var late = T0 + CaptureFlow.ArmWindow + TimeSpan.FromSeconds(1);
        Assert.Equal(CaptureDecision.Notify, flow.OnClipboardImage(false, late));
    }

    [Fact]
    public void WatchDisabled_IgnoresPassiveImages_ButArmedStillOpens()
    {
        var flow = new CaptureFlow { WatchEnabled = false };

        Assert.Equal(CaptureDecision.Ignore, flow.OnClipboardImage(false, T0));

        flow.Arm(T0);
        Assert.Equal(CaptureDecision.AutoOpen, flow.OnClipboardImage(false, T0));
    }
}
