using System.Text.RegularExpressions;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class FileAssociationPolicyTests
{
    [Fact]
    public void SelectableExtensions_TrackTheProductViewableFormats()
    {
        var selectable = new HashSet<string>(
            FileAssociationPolicy.SelectableExtensions, StringComparer.OrdinalIgnoreCase);

        Assert.True(selectable.SetEquals(ImageFormatCatalog.ViewableExtensions));
        Assert.Equal(
            FileAssociationPolicy.SelectableExtensions.Count,
            selectable.Count);
    }

/// <summary>PDF/PSD는 제품에서 제외됨(ADR-0005).
/// 열지 못하는 형식의 기본 앱으로 이 앱을 고를 수 없어야 함.</summary>
    [Theory]
    [InlineData(".pdf")]
    [InlineData(".psd")]
    public void RemovedDocumentFormats_AreNotSelectable(string extension)
    {
        Assert.False(FileAssociationPolicy.IsSelectable(extension));
        Assert.DoesNotContain(
            extension,
            FileAssociationPolicy.Groups.SelectMany(group => group.Extensions),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Groups_AreDisjointNonEmptyAndCoverEverySelectableExtension()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in FileAssociationPolicy.Groups)
        {
            Assert.NotEmpty(group.Extensions);
            foreach (var extension in group.Extensions)
            {
                Assert.StartsWith(".", extension, StringComparison.Ordinal);
                Assert.True(seen.Add(extension), $"'{extension}' appears in two groups.");
            }
        }
        Assert.Equal(FileAssociationPolicy.SelectableExtensions.Count, seen.Count);
    }

    [Fact]
    public void EssentialExtensions_MatchTheSetupDefaultRegistrationExactly()
    {
        // FR-APP-001: "필수 파일" 버튼은 설치 프로그램이 등록하는 묶음과 같아야 함.
        var wxs = File.ReadAllText(RepoFile("installer", "common", "Product.wxs"));
        var match = Regex.Match(wxs, @"<\?foreach extension in ([a-z0-9;]+)\?>");
        Assert.True(match.Success, "Product.wxs no longer declares the association loop.");
        var setupExtensions = match.Groups[1].Value
            .Split(';')
            .Select(extension => $".{extension}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.True(setupExtensions.SetEquals(FileAssociationPolicy.EssentialExtensions));
        Assert.True(FileAssociationPolicy.EssentialExtensions.All(
            FileAssociationPolicy.IsSelectable));
    }

    [Fact]
    public void RegistryShape_StaysInParityWithTheSetupComponent()
    {
        var wxs = File.ReadAllText(RepoFile("installer", "common", "Product.wxs"));

        Assert.Contains(
            $@"Software\Classes\{FileAssociationPolicy.ProgId}",
            wxs,
            StringComparison.Ordinal);
        Assert.Contains(FileAssociationPolicy.ProgIdDisplayName, wxs, StringComparison.Ordinal);
        Assert.Contains(
            FileAssociationPolicy.CapabilitiesKeyPath, wxs, StringComparison.Ordinal);
        Assert.Contains(
            $"Name=\"{FileAssociationPolicy.RegisteredApplicationName}\"",
            wxs,
            StringComparison.Ordinal);
        Assert.Contains(
            FileAssociationPolicy.ApplicationDescription, wxs, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenWithProgidsKeyPath_BuildsPerExtensionPathsAndRejectsUnknownExtensions()
    {
        Assert.Equal(
            @"Software\Classes\.png\OpenWithProgids",
            FileAssociationPolicy.OpenWithProgidsKeyPath(".png"));
        Assert.Throws<ArgumentException>(() =>
            FileAssociationPolicy.OpenWithProgidsKeyPath(".exe"));
        Assert.Throws<ArgumentException>(() =>
            FileAssociationPolicy.OpenWithProgidsKeyPath("png"));
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
