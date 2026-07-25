using Xunit;

namespace EzyImageViewer.Tests.Contracts;

/// <summary>파일 창이 한 위치·크기로 한 번만 보이게 하는 분산 소스 계약.</summary>
public sealed class FirstWindowPresentationContractTests
{
    [Fact]
    public void FileActivation_CreatesTheWindowUnshownAndReusesVisibleWindowsAsIs()
    {
        var windowManager = File.ReadAllText(RepoFile("EzyImageViewer.App", "WindowManager.cs"));

        Assert.Contains(
            "OpenNewWindow(deferPresentation: true)",
            windowManager,
            StringComparison.Ordinal);
        // 두 파일 활성화 분기 모두 지연 오버로드로 생성.
        Assert.Equal(
            2,
            windowManager.Split("OpenNewWindow(deferPresentation: true)").Length - 1);
        // 기존 창은 그대로 표시, 새 창만 이미지 대기.
        Assert.Contains("window.PresentNow();", windowManager, StringComparison.Ordinal);
        Assert.Contains(
            "window.DeferFirstPresentation(FirstPresentationDeadline)",
            windowManager,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EveryManagerShowPathGoesThroughPresentNow()
    {
        var windowManager = File.ReadAllText(RepoFile("EzyImageViewer.App", "WindowManager.cs"));

        // Activate()는 즉시 표시 분기 한 곳에만 존재.
        Assert.Equal(1, windowManager.Split("window.Activate()").Length - 1);
        var normalized = windowManager.Replace("\r\n", "\n");
        Assert.Contains(
            "if (deferPresentation)\n            window.DeferFirstPresentation(FirstPresentationDeadline);\n"
                + "        else\n        {\n            window.SizeForEmptyPresentation();\n"
                + "            window.Activate();\n        }",
            normalized,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyWindow_UsesHalfWorkAreaBeforeActivation()
    {
        var windowManager = File.ReadAllText(RepoFile("EzyImageViewer.App", "WindowManager.cs"));
        var viewer = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));
        var geometry = File.ReadAllText(RepoFile(
            "EzyImageViewer.Rendering", "InitialWindowGeometry.cs"));

        Assert.Contains("window.SizeForEmptyPresentation();", windowManager, StringComparison.Ordinal);
        Assert.Contains(
            "internal void SizeForEmptyPresentation()",
            viewer,
            StringComparison.Ordinal);
        Assert.Contains(
            "InitialWindowGeometry.MeasureEmptyWindow(workArea)",
            viewer,
            StringComparison.Ordinal);
        Assert.Contains(
            "EmptyWindowWorkAreaFraction = 0.5",
            geometry,
            StringComparison.Ordinal);
    }

    [Fact]
    public void DeferredWindowIsNotAPassiveCaptureTarget()
    {
        var windowManager = File.ReadAllText(RepoFile("EzyImageViewer.App", "WindowManager.cs"));

        // Peek의 기억 창·첫 생존 창 대체 분기 모두 확인.
        Assert.Contains("!_lastActive.IsPresentationDeferred", windowManager, StringComparison.Ordinal);
        Assert.Contains("!window.IsPresentationDeferred", windowManager, StringComparison.Ordinal);
    }

    [Fact]
    public void ShowingADeferredWindowAlwaysBurnsItsAutoSize()
    {
        var viewer = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));

        var presentNow = Body(viewer, "internal void PresentNow()");
        Assert.Contains("_presentationDeferred = false;", presentNow, StringComparison.Ordinal);
        Assert.Contains("_initialSizePending = false;", presentNow, StringComparison.Ordinal);
        Assert.Contains("StopPresentationDeadline();", presentNow, StringComparison.Ordinal);
        Assert.Contains("Activate();", presentNow, StringComparison.Ordinal);

        // 마감이 창 표시를 보장하고 자동 크기는 소진.
        var defer = Body(viewer, "internal void DeferFirstPresentation(TimeSpan deadline)");
        Assert.Contains("Tick += (_, _) => PresentNow();", defer, StringComparison.Ordinal);
        Assert.Contains("_presentationDeadline.Start();", defer, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadyAndFailedBothResolveToASingleAppearance()
    {
        var viewer = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));

        var present = Body(viewer, "private void PresentDeferredWindow()");
        Assert.Contains("TryPresentSizedForFirstDocument()", present, StringComparison.Ordinal);
        Assert.Contains("case SessionState.Failed:", present, StringComparison.Ordinal);
        Assert.Contains("PresentNow();", present, StringComparison.Ordinal);

        // 구형 배치 후 크기 변경은 숨은 창에서 실행 금지.
        var legacy = Body(viewer, "private void MaybeApplyInitialWindowSize()");
        Assert.Contains(
            "if (!_initialSizePending || _presentationDeferred)",
            legacy,
            StringComparison.Ordinal);
    }

    [Fact]
    public void SizedPresentationMeasuresTheFrameInsteadOfEstimatingIt()
    {
        var viewer = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));

        var sized = Body(viewer, "private bool TryPresentSizedForFirstDocument()");
        // 배치 전 XamlRoot 배율이 없어 창 핸들 DPI 사용. 0이면 추측 말고 대체 경로.
        Assert.Contains("GetDpiForWindow(", sized, StringComparison.Ordinal);
        Assert.Contains("if (dpi == 0)", sized, StringComparison.Ordinal);
        Assert.DoesNotContain("XamlRoot", sized, StringComparison.Ordinal);
        Assert.DoesNotContain("Canvas.ActualWidth", sized, StringComparison.Ordinal);
        // 클라이언트 크기부터 재고 작업 영역 상한을 맡는 외곽 크기 측정.
        Assert.Contains("AppWindow.ResizeClient(", sized, StringComparison.Ordinal);
        Assert.Contains("AppWindow.ClientSize", sized, StringComparison.Ordinal);
        Assert.Contains("AppWindow.MoveAndResize(", sized, StringComparison.Ordinal);
        Assert.Contains("_initialSizePending = false;", sized, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusBarHeightConstantMatchesTheXamlRowItStandsFor()
    {
        var viewer = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));
        var xaml = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "Views", "ViewerWindow.xaml"));

        // 숨은 창은 배치 측정이 없어 클라이언트-캔버스 높이를 상태바 행 상수로 사용.
        var match = System.Text.RegularExpressions.Regex.Match(
            viewer, @"StatusBarHeightDip\s*=\s*(\d+(?:\.\d+)?)d;");
        Assert.True(match.Success, "StatusBarHeightDip constant not found.");
        Assert.Contains(
            $"<Grid Grid.Row=\"1\" MinHeight=\"{match.Groups[1].Value}\"",
            xaml,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AClosedWindowCannotBeShownByItsOwnDeadline()
    {
        var viewer = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));

        // 위 한 줄 플라이아웃 처리기가 아니라 창 자체 Closed 처리기.
        var normalized = viewer.Replace("\r\n", "\n");
        var start = normalized.IndexOf("Closed += (_, _) =>\n        {", StringComparison.Ordinal);
        Assert.True(start >= 0, "Window Closed handler not found.");
        var end = normalized.IndexOf("\n        };", start, StringComparison.Ordinal);
        Assert.True(end > start, "Window Closed handler body not found.");
        Assert.Contains(
            "StopPresentationDeadline();",
            normalized[start..end],
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheAbandonedSeedWindowIsGone()
    {
        var viewer = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));

        foreach (var token in new[]
        {
            "SeedInitialWindowSize", "InitialSeedSizeDip", "_initialSizeSeeded", "_firstFramePainted",
        })
        {
            Assert.DoesNotContain(token, viewer, StringComparison.Ordinal);
        }
    }

    /// <summary>메서드 시그니처부터 같은 들여쓰기의 다음 멤버까지 소스 추출.</summary>
    private static string Body(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Method not found: {signature}");
        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"Method body not found: {signature}");
        var depth = 0;
        for (var index = open; index < source.Length; index++)
        {
            depth += source[index] switch { '{' => 1, '}' => -1, _ => 0 };
            if (depth == 0)
                return source[start..(index + 1)];
        }
        throw new InvalidOperationException($"Unbalanced braces after {signature}.");
    }

    private static string RepoFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "EzyImageViewer.slnx")))
                continue;
            return Path.Combine([directory.FullName, .. segments]);
        }
        throw new DirectoryNotFoundException(
            "Repository root was not found from the test output directory.");
    }
}
