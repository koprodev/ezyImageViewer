using System.Xml.Linq;
using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class FileAssociationPolicyTests
{
    [Fact]
    public void EssentialExtensions_MatchTheStoreManifestExactly()
    {
        XNamespace uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
        var manifest = XDocument.Load(RepoFile(
            "packaging", "AppxManifest.template.xml"));
        var declared = manifest
            .Descendants(uap + "FileType")
            .Select(element => element.Value)
            .ToArray();

        Assert.Equal(FileAssociationPolicy.EssentialExtensions, declared);
        Assert.Equal(
            declared.Length,
            declared.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(declared, extension =>
            Assert.Matches(@"^\.[a-z0-9]+$", extension));
    }

    [Fact]
    public void DefaultAppsUri_UsesTheWindowsSettingsScheme()
    {
        var uri = FileAssociationPolicy.GetDefaultAppsSettingsUri();

        Assert.Equal("ms-settings", uri.Scheme);
        Assert.StartsWith(
            FileAssociationPolicy.DefaultAppsSettingsUri,
            uri.OriginalString,
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
