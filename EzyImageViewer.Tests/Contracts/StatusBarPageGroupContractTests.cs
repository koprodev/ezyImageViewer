using System.Xml.Linq;
using Xunit;

namespace EzyImageViewer.Tests.Contracts;

/// <summary>단일 프레임 문서의 페이지 표시는 의미 없음.
/// 상태 막대에 영원한 "1 / 1"로 눌러앉지 말고 접혀야 함.</summary>
public sealed class StatusBarPageGroupContractTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void PageNavigation_LivesInItsOwnCollapsedGroupApartFromAnimationPlayback()
    {
        var view = XDocument.Load(RepoFile("EzyImageViewer.App", "Views", "ViewerWindow.xaml"));

        var group = view.Descendants()
            .Single(element => (string?)element.Attribute(Xaml + "Name") == "StatusPageGroup");
        Assert.Equal("Collapsed", (string?)group.Attribute("Visibility"));

        var grouped = group.Descendants()
            .Select(element => (string?)element.Attribute(Xaml + "Name"))
            .ToArray();
        Assert.Contains("PreviousPageButton", grouped);
        Assert.Contains("StatusPage", grouped);
        Assert.Contains("NextPageButton", grouped);

        // 애니메이션 재생은 별도 표시 규칙이 있어 페이지 그룹이 접혀도 살아 있어야 함.
        Assert.DoesNotContain("AnimationPlaybackButton", grouped);
    }

    [Fact]
    public void StatusBarRefresh_DrivesThePageGroupFromFrameCount()
    {
        var viewModel = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "ViewModels", "ViewerViewModel.cs"));
        var view = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));

        Assert.Contains(
            "public bool HasMultipleFrames => Session.Current is { FrameCount: > 1 };",
            viewModel,
            StringComparison.Ordinal);
        Assert.Contains(
            "StatusPageGroup.Visibility = _viewModel.HasMultipleFrames",
            view,
            StringComparison.Ordinal);
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
