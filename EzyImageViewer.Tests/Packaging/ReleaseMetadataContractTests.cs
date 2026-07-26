using System.Text.RegularExpressions;
using EzyImageViewer.Core.Imaging;
using Xunit;

namespace EzyImageViewer.Tests.Packaging;

public sealed class ReleaseMetadataContractTests
{
    [Fact]
    public void UserGuide_CoversEveryProductFormatTierAndStoreDistribution()
    {
        var guide = File.ReadAllText(RepoFile("docs", "user-guide.md"));
        var expected = ImageFormatCatalog.ViewableExtensions
            .Concat([".ezyimg"])
            .Distinct(StringComparer.OrdinalIgnoreCase);

        Assert.All(expected, extension =>
            Assert.Contains($"`{extension}`", guide, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("`.pdf`", guide, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("`.psd`", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Microsoft Store", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("Basic Portable", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("Portable EXE", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("GitHub 공개 Releases", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("update-check-state.txt", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void ThirdPartyNotice_TracksCurrentStoreRuntimeWithoutRetiredTools()
    {
        var notice = File.ReadAllText(RepoFile("THIRD-PARTY-NOTICES.md"));

        Assert.Contains("Svg.Custom", notice, StringComparison.Ordinal);
        Assert.Contains("MS-PL", notice, StringComparison.Ordinal);
        Assert.Contains("Microsoft.WindowsAppSDK.WinUI", notice, StringComparison.Ordinal);
        Assert.Contains("1.8.260709004", notice, StringComparison.Ordinal);
        Assert.Contains("법무 검토가 끝났음을 의미하지 않습니다", notice,
            StringComparison.Ordinal);
        Assert.DoesNotContain("UserChoice", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("WiX", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("SignPath", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("PDFium", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("PDFtoImage", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void CiWorkflow_PinsActionsAndRunsOnlyStorePackageGate()
    {
        var workflow = File.ReadAllText(RepoFile(".github", "workflows", "ci.yml"));
        var actionReferences = Regex.Matches(
                workflow,
                @"uses:\s+actions/[^@\s]+@([^\s#]+)")
            .Select(match => match.Groups[1].Value)
            .ToArray();

        Assert.NotEmpty(actionReferences);
        Assert.All(actionReferences, reference =>
            Assert.Matches("^[a-f0-9]{40}$", reference));
        Assert.Contains("persist-credentials: false", workflow, StringComparison.Ordinal);
        Assert.Contains("timeout-minutes:", workflow, StringComparison.Ordinal);
        Assert.Contains("-p:Packaged=true -p:StoreChannel=true", workflow,
            StringComparison.Ordinal);
        Assert.Contains("pack-msix.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("verify-msix-release.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("-Version 1.0.0.0", workflow, StringComparison.Ordinal);
        Assert.Contains("-RequireBuildOutputMatch", workflow, StringComparison.Ordinal);
        Assert.Contains("test-public-source-snapshot-contract.ps1", workflow,
            StringComparison.Ordinal);
        Assert.Contains("-p:NuGetAuditMode=all", workflow, StringComparison.Ordinal);
        Assert.Contains("NU1903%3BNU1904", workflow, StringComparison.Ordinal);
        Assert.Contains("if-no-files-found: error", workflow, StringComparison.Ordinal);
        Assert.Contains("retention-days:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("portable", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("WiX", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AppInstaller", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("generate-release-metadata", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void RetiredBinaryDistributionPaths_AreAbsent()
    {
        string[] retiredPaths =
        [
            "installer",
            "tools",
            Path.Combine(".github", "workflows", "release-portable.yml"),
            Path.Combine(".github", "workflows", "release-preview.yml"),
            Path.Combine("packaging", "build-portable-release.ps1"),
            Path.Combine("packaging", "build-wix-installer.ps1"),
            Path.Combine("packaging", "generate-appinstaller.ps1"),
            Path.Combine("docs", "signpath-readiness.md"),
            Path.Combine("docs", "code-signing-policy.md"),
        ];

        Assert.All(retiredPaths, path =>
        {
            var fullPath = RepoFile(path);
            Assert.False(File.Exists(fullPath), $"Retired file still exists: {path}");
            Assert.False(Directory.Exists(fullPath), $"Retired directory still exists: {path}");
        });
    }

    private static string RepoFile(params string[] parts) =>
        Path.Combine([FindRepositoryRoot(), .. parts]);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EzyImageViewer.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException(
            "Repository root was not found from the test output directory.");
    }
}
