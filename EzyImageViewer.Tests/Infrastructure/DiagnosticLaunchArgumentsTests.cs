using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class DiagnosticLaunchArgumentsTests
{
    [Fact]
    public void TryParse_NoDiagnosticArgumentsUsesTheProductPath()
    {
        Assert.True(DiagnosticLaunchArguments.TryParse(
            ["image.png", "--safe-mode"], out var plan));
        Assert.Equal(DiagnosticLaunchMode.None, plan.Mode);
        Assert.False(plan.IsDiagnostic);
        Assert.False(plan.IsStandalone);
    }

    [Fact]
    public void TryParse_RecognizesEverySupportedPrimaryMode()
    {
        var cases = new (string[] Arguments, DiagnosticLaunchMode Mode, bool Standalone)[]
        {
            (["--bench-zoompan=result.json", "--bench-backend=swapchain"],
                DiagnosticLaunchMode.ZoomPanBenchmark, true),
            (["--spike-zoompan=result.json"], DiagnosticLaunchMode.ZoomPanBenchmark, true),
            (["--bench-startup=result.json"], DiagnosticLaunchMode.StartupBenchmark, false),
            (["--bench-open24mp"], DiagnosticLaunchMode.Open24MegapixelBenchmark, true),
            (["--bench-open24mp=result.json"], DiagnosticLaunchMode.Open24MegapixelBenchmark, true),
            (["--smoke-open=input.png", "--smoke-out=result.json", "--smoke-project=project.ezyimg",
                "--smoke-capture", "--smoke-codec"], DiagnosticLaunchMode.OpenSmoke, true),
            (["--smoke-hold=input.png"], DiagnosticLaunchMode.HoldSmoke, true),
            (["--diagnostic-recovery-seed=input.png", "--diagnostic-recovery-out=result.json",
                "--diagnostic-recovery-root=C:\\Temp\\recovery"], DiagnosticLaunchMode.RecoverySeed, true),
            (["--diagnostic-recovery-verify", "--diagnostic-recovery-out=result.json",
                "--diagnostic-recovery-root=C:\\Temp\\recovery"], DiagnosticLaunchMode.RecoveryVerify, true),
        };

        foreach (var value in cases)
        {
            Assert.True(DiagnosticLaunchArguments.TryParse(value.Arguments, out var plan));
            Assert.Equal(value.Mode, plan.Mode);
            Assert.Equal(value.Standalone, plan.IsStandalone);
        }
    }

    [Theory]
    [InlineData("--smoke-typo")]
    [InlineData("--bench-open24mp=")]
    [InlineData("--smoke-open=")]
    [InlineData("--bench-backend=swapchain")]
    [InlineData("--diagnostic-recovery-verify")]
    public void TryParse_RejectsUnknownEmptyOrOrphanedDiagnosticArguments(string argument)
    {
        Assert.False(DiagnosticLaunchArguments.TryParse([argument], out _));
    }

    [Fact]
    public void TryParse_RejectsConflictingModesAndIncompatibleCompanions()
    {
        Assert.False(DiagnosticLaunchArguments.TryParse(
            ["--smoke-open=input.png", "--smoke-hold=input.png"], out _));
        Assert.False(DiagnosticLaunchArguments.TryParse(
            ["--bench-startup=result.json", "--smoke-capture"], out _));
        Assert.False(DiagnosticLaunchArguments.TryParse(
            ["--smoke-open=input.png", "--smoke-out=a.json", "--smoke-out=b.json"], out _));
    }
}
