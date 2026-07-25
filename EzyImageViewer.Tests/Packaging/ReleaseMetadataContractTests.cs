using EzyImageViewer.Core.Imaging;
using System.Text.RegularExpressions;
using Xunit;

namespace EzyImageViewer.Tests.Packaging;

public sealed class ReleaseMetadataContractTests
{
    [Fact]
    public void MetadataGenerator_IsDeterministicAndLimitedToExplicitArtifacts()
    {
        var script = File.ReadAllText(RepoFile(
            "packaging",
            "generate-release-metadata.ps1"));

        Assert.Contains("Assert-FourPartVersion", script, StringComparison.Ordinal);
        Assert.Contains("Release artifact must be inside OutputDirectory", script,
            StringComparison.Ordinal);
        Assert.Contains("Release artifact basenames must be unique", script,
            StringComparison.Ordinal);
        Assert.Contains("Release artifact must not be a reparse point", script,
            StringComparison.Ordinal);
        Assert.Contains("[Array]::Sort", script, StringComparison.Ordinal);
        Assert.Contains("StringComparer]::Ordinal", script, StringComparison.Ordinal);
        Assert.Contains("Get-FileHash", script, StringComparison.Ordinal);
        Assert.Contains("SHA-256 verification failed", script, StringComparison.Ordinal);
        Assert.Contains("Expected exactly one nupkg", script, StringComparison.Ordinal);
        Assert.Contains("NuGet archive SHA-512 does not match its sidecar", script,
            StringComparison.Ordinal);
        Assert.Contains("Get-FileHash -LiteralPath $nupkgs[0].FullName -Algorithm SHA512", script,
            StringComparison.Ordinal);
        Assert.Contains("Assert-MsixEntryMatchesFile", script, StringComparison.Ordinal);
        Assert.Contains("ezyImageViewer.deps.json", script, StringComparison.Ordinal);
        Assert.Contains("does not match the supplied source file", script, StringComparison.Ordinal);
        Assert.Contains(".NETCoreApp,Version=v10.0/win-x64", script, StringComparison.Ordinal);
        Assert.Contains("depsSha256", script, StringComparison.Ordinal);
        Assert.Contains("pkg:generic/google/MaterialSymbolsOutlined@", script,
            StringComparison.Ordinal);
        Assert.Contains(
            "6EB4B0BA0D788B9CFB4F22D68A768276142CBC3698177AC2803A0F1F1EB3207F",
            script,
            StringComparison.Ordinal);
        Assert.Contains("Apache-2.0", script, StringComparison.Ordinal);
        Assert.Contains("https://github.com/google/material-design-icons", script,
            StringComparison.Ordinal);
        Assert.Contains("specVersion = '1.6'", script, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::Replace", script, StringComparison.Ordinal);
        Assert.Contains("$stream.Flush($true)", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-Date", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Microsoft.NET.Test.Sdk", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UserGuide_CoversEveryProductFormatTier()
    {
        var guide = File.ReadAllText(RepoFile("docs", "user-guide.md"));
        var expected = ImageFormatCatalog.ViewableExtensions
            .Concat([".ezyimg"])
            .Distinct(StringComparer.OrdinalIgnoreCase);

        Assert.All(expected, extension =>
            Assert.Contains($"`{extension}`", guide, StringComparison.OrdinalIgnoreCase));
        // PDF/PSD는 제품에서 빠졌으므로 안내서가 광고하면 안 됨.
        Assert.DoesNotContain("`.pdf`", guide, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("`.psd`", guide, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Portable", guide, StringComparison.Ordinal);
        Assert.Contains("24시간", guide, StringComparison.Ordinal);
        Assert.Contains("자동 다운로드", guide, StringComparison.Ordinal);
        Assert.Contains("기본 브라우저", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void ThirdPartyNotice_SeparatesDifferentLicenseExpressions()
    {
        var notice = File.ReadAllText(RepoFile("THIRD-PARTY-NOTICES.md"));

        Assert.Contains("Svg.Custom", notice, StringComparison.Ordinal);
        Assert.Contains("MS-PL", notice, StringComparison.Ordinal);
        Assert.Contains("법무 검토가 끝났음을 의미하지 않습니다", notice,
            StringComparison.Ordinal);
        // PDF/PSD는 제거됨(ADR-0005). 관련 런타임이 목록에 부활하면 안 됨.
        Assert.DoesNotContain("PDFium", notice, StringComparison.Ordinal);
        Assert.DoesNotContain("PDFtoImage", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void CiWorkflow_PinsActionsAndRunsUnsignedPackageGate()
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
        Assert.Contains("dotnet restore EzyImageViewer.App/EzyImageViewer.App.csproj", workflow,
            StringComparison.Ordinal);
        Assert.Contains("-p:Packaged=true -p:Platform=x64 --locked-mode", workflow,
            StringComparison.Ordinal);
        Assert.Contains("pack-msix.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("verify-msix-release.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("generate-release-metadata.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("generate-appinstaller.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("verify-appinstaller-release.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("test-appinstaller-contract.ps1", workflow, StringComparison.Ordinal);
        Assert.Contains("packaging/out/ezyImageViewer.appinstaller", workflow,
            StringComparison.Ordinal);
        Assert.Contains("-AppInstallerFile packaging/out/ezyImageViewer.appinstaller", workflow,
            StringComparison.Ordinal);
        Assert.Contains("-Version 1.0.0.1", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("-Version 0.0.0.1", workflow, StringComparison.Ordinal);
        Assert.Contains("-RequireBuildOutputMatch", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("CodecHost", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-p:NuGetAuditMode=all", workflow, StringComparison.Ordinal);
        Assert.Contains("NU1903%3BNU1904", workflow, StringComparison.Ordinal);
        Assert.Contains("if-no-files-found: error", workflow, StringComparison.Ordinal);
        Assert.Contains("retention-days:", workflow, StringComparison.Ordinal);

        var contractInvocations = Regex.Matches(
            workflow,
            @"(?m)^\s+powershell .*?-File packaging/test-[^\r\n]+\.ps1\s*$");
        var unguardedInvocations = Regex.Matches(
            workflow,
            @"(?m)^\s+powershell .*?-File packaging/test-[^\r\n]+\.ps1\s*\r?\n(?!\s*if \(\$LASTEXITCODE -ne 0\) \{ exit \$LASTEXITCODE \})");
        Assert.Equal(7, contractInvocations.Count);
        Assert.Empty(unguardedInvocations.Cast<Match>());
    }

    [Fact]
    public void AppInstallerScripts_AreOfflineDeterministicAndFailClosed()
    {
        var root = FindRepositoryRoot();
        var helper = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "appinstaller-helpers.ps1"));
        var generator = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "generate-appinstaller.ps1"));
        var verifier = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "verify-appinstaller-release.ps1"));
        var harness = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "test-appinstaller-contract.ps1"));
        var metadata = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "generate-release-metadata.ps1"));

        Assert.Contains("http://schemas.microsoft.com/appx/appinstaller/2017/2", helper,
            StringComparison.Ordinal);
        Assert.Contains("DtdProcessing]::Prohibit", helper, StringComparison.Ordinal);
        Assert.Contains("RequireAsciiWithoutBom", helper, StringComparison.Ordinal);
        Assert.Contains("RequirePositiveMajor", helper, StringComparison.Ordinal);
        Assert.Contains("must be an absolute HTTPS URI", helper, StringComparison.Ordinal);
        Assert.Contains("must not contain userinfo, a query, or a fragment", helper,
            StringComparison.Ordinal);
        Assert.Contains("must not declare a PackageDependency", helper,
            StringComparison.Ordinal);
        Assert.Contains("[string]$UpdateMode = 'None'", generator, StringComparison.Ordinal);
        Assert.Contains("WriteStartElement('UpdateSettings'", generator, StringComparison.Ordinal);
        Assert.DoesNotContain("AutomaticBackgroundTask", generator, StringComparison.Ordinal);
        Assert.DoesNotContain("ForceUpdateFromAnyVersion", generator, StringComparison.Ordinal);
        Assert.Contains("Assert-EzyAppInstallerDocument", verifier, StringComparison.Ordinal);
        Assert.Contains("negative PASS:", harness, StringComparison.Ordinal);
        Assert.Contains("Repeated App Installer generation was not byte-identical", harness,
            StringComparison.Ordinal);
        Assert.Contains("Role = 'app-installer'", metadata, StringComparison.Ordinal);
        Assert.DoesNotContain("Add-AppxPackage", generator, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certutil", verifier, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ms-appinstaller:", generator, StringComparison.OrdinalIgnoreCase);
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
