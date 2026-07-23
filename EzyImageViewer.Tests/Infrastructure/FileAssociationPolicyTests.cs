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
        // FR-APP-001: the "필수 파일" button must select the same set the Setup registers.
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
