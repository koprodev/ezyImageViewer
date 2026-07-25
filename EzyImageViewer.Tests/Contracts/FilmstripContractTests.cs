using Xunit;

namespace EzyImageViewer.Tests.Contracts;

public sealed class FilmstripContractTests
{
    [Fact]
    public void Filmstrip_CentersShortRowsAndArrowFocusOpensTheTargetImage()
    {
        var source = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "Views", "ViewerWindow.Filmstrip.cs"));

        Assert.Contains(
            "HorizontalContentAlignment = HorizontalAlignment.Center",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "HorizontalAlignment = HorizontalAlignment.Center",
            source,
            StringComparison.Ordinal);
        Assert.Contains("card.KeyDown += OnFilmstripCardKeyDown", source);
        Assert.Contains("e.Key is not (VirtualKey.Left or VirtualKey.Right)", source);
        Assert.Contains("? card.Index - 1", source);
        Assert.Contains(": card.Index + 1", source);
        Assert.Contains("_viewModel.OpenAt(targetIndex)", source);
        Assert.Contains("targetCard?.Focus(FocusState.Keyboard)", source);
        Assert.Contains("card.KeyDown -= OnFilmstripCardKeyDown", source);
    }

    [Fact]
    public void DocumentSwitch_CrossfadesFramesWhileFirstOpenOnlyFadesIn()
    {
        var source = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));

        // 전환은 짧게 유지. 1초로 되돌아가면 넘김마다 화면이 붙잡힘.
        Assert.Contains(
            "DocumentReplacementCrossfadeMilliseconds = 180d",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "FirstDocumentFadeInMilliseconds = 220d",
            source,
            StringComparison.Ordinal);

        var paint = Body(source, "private void OnPaintSurface(");

        // 교체 순간의 CPU 전면 합성은 로드 시작 때 떠 두는 GPU 사본이 대신함.
        Assert.Contains(
            "_documentTransitionCaptureRequested = true;",
            Body(source, "private void OnDocumentLoadStarted()"),
            StringComparison.Ordinal);
        Assert.Contains(
            "ReplaceDocumentTransitionPrecapture(e.Surface.Snapshot());",
            paint,
            StringComparison.Ordinal);
        Assert.Contains(
            "TakeDocumentTransitionPrecapture() ?? CaptureCurrentPresentation());",
            Body(source, "private void PrepareDocumentCrossfade("),
            StringComparison.Ordinal);
        // 예약 사본은 Prepare가 꺼내 쓰기 전이라 Complete가 건드리면 안 됨.
        Assert.DoesNotContain(
            "ReplaceDocumentTransitionPrecapture",
            Body(source, "private void CompleteDocumentTransition()"),
            StringComparison.Ordinal);
        Assert.Contains(
            "ReplaceDocumentTransitionPrecapture(null);",
            Body(source, "private void StopDocumentTransition()"),
            StringComparison.Ordinal);

        // 새 장면을 먼저 불투명하게 그린 뒤 옛 장면을 알파로 덮음. 프레임마다 오프스크린 레이어를 뜨지 않음.
        Assert.Contains(
            "canvas, previousFrame, viewport.Width, viewport.Height, ToAlpha(1f - progress));",
            paint,
            StringComparison.Ordinal);
        Assert.DoesNotContain("canvas.SaveLayer(blendPaint);", source, StringComparison.Ordinal);
        // SaveLayer는 덮을 옛 장면이 없는 첫 문서 경로에만 남음.
        Assert.Contains(
            "if (previousFrame is null && transitionActive && progress < 1f)",
            paint,
            StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(paint, "canvas.SaveLayer("));
        // 후임 문서가 아직 못 그리는 프레임에 배경만 남기면 번쩍임. 옛 장면을 붙잡고 시계를 미룸.
        Assert.Contains(
            "_documentTransitionStartedTimestamp = Stopwatch.GetTimestamp();",
            paint,
            StringComparison.Ordinal);
        // 붙잡기는 한계 시각이 있어야 함. 안 오는 문서에 화면이 얼어붙으면 안 됨.
        Assert.Contains(
            "Stopwatch.GetTimestamp() < _documentTransitionHoldDeadline",
            paint,
            StringComparison.Ordinal);
        Assert.Contains(
            "_documentTransitionHoldDeadline = 0;",
            Body(source, "private void CompleteDocumentTransition()"),
            StringComparison.Ordinal);
        Assert.Contains(
            "StartDocumentTransition(",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "_documentTransitionTimer.Start();",
            Body(source, "private void StartDocumentTransition("),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "EnableRenderLoop",
            Body(source, "private void StartDocumentTransition("),
            StringComparison.Ordinal);
        Assert.Contains(
            "CompleteDocumentTransition();",
            Body(source, "private void OnDocumentLoadStarted()"),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DocumentTransitionProgress",
            Body(source, "private SKImage? CaptureCurrentPresentation()"),
            StringComparison.Ordinal);
        Assert.Contains(
            "ReplaceDocumentTransitionFrame(null);",
            Body(source, "private void CompleteDocumentTransition()"),
            StringComparison.Ordinal);
        Assert.Contains(
            "DisposeSnapshotOnUi(previous);",
            Body(source, "private void ReplaceDocumentTransitionFrame("),
            StringComparison.Ordinal);
        Assert.Contains("documentTransitionPending", source, StringComparison.Ordinal);
        Assert.Contains("if (!_syncingSession)", source, StringComparison.Ordinal);
        Assert.Contains(
            "PrepareDocumentCrossfade(incomingDocument.Id)",
            source,
            StringComparison.Ordinal);
        Assert.True(
            source.IndexOf("PrepareDocumentCrossfade(incomingDocument.Id)", StringComparison.Ordinal)
            < source.IndexOf("_viewModel.SyncEditor();", StringComparison.Ordinal));
        Assert.DoesNotContain("Canvas.Opacity", source, StringComparison.Ordinal);
        Assert.DoesNotContain("BeginDocumentFade", source, StringComparison.Ordinal);

        Assert.DoesNotContain(
            "_animationsEnabled",
            Body(source, "private bool RebuildSnapshot("),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_animationsEnabled",
            Body(source, "private void StartDocumentTransition("),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DocumentFade",
            Body(source, "private void OnAnimationsEnabledChanged("),
            StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string value)
    {
        var count = 0;
        for (var index = source.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = source.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    /// <summary>메서드 시그니처부터 짝이 맞는 닫는 중괄호까지 추출.</summary>
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
            if (File.Exists(Path.Combine(directory.FullName, "EzyImageViewer.slnx")))
                return Path.Combine([directory.FullName, .. segments]);
        }
        throw new DirectoryNotFoundException(
            "Repository root was not found from the test output directory.");
    }
}
