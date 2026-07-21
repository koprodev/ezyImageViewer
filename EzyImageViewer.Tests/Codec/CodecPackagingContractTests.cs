using System.Xml.Linq;
using Xunit;

namespace EzyImageViewer.Tests.Codec;

public sealed class CodecPackagingContractTests
{
    private static readonly XNamespace Foundation =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

    [Fact]
    public void CodecHostManifest_IsFrameworkOnlyWithoutActivationSurface()
    {
        var root = LoadManifest("CodecHost.AppxManifest.template.xml").Root!;

        Assert.Equal(
            "GRTech.ezyImageViewer.CodecHost",
            root.Element(Foundation + "Identity")!.Attribute("Name")!.Value);
        Assert.Equal(
            "true",
            root.Element(Foundation + "Properties")!
                .Element(Foundation + "Framework")!.Value,
            ignoreCase: true);
        Assert.Null(root.Element(Foundation + "Applications"));
        Assert.Null(root.Element(Foundation + "Capabilities"));
    }

    [Fact]
    public void MainManifest_RequiresDedicatedCodecHostFramework()
    {
        var root = LoadManifest("AppxManifest.template.xml").Root!;
        var dependency = root.Element(Foundation + "Dependencies")!
            .Elements(Foundation + "PackageDependency")
            .Single();

        Assert.Equal("GRTech.ezyImageViewer.CodecHost", dependency.Attribute("Name")!.Value);
        Assert.Equal("{{PUBLISHER}}", dependency.Attribute("Publisher")!.Value);
        Assert.Equal("{{CODEC_HOST_VERSION}}", dependency.Attribute("MinVersion")!.Value);
    }

    [Fact]
    public void MainAppProject_DoesNotEmbedCodecHostBundle()
    {
        var project = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "EzyImageViewer.App",
            "EzyImageViewer.App.csproj"));

        Assert.DoesNotContain("EzyImageViewer.CodecHost", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CopyCodecHostBundle", project, StringComparison.Ordinal);
    }

    [Fact]
    public void MainAppProject_DoesNotOverrideWindowsAppSdkTransitiveRuntimePackages()
    {
        var project = XDocument.Load(Path.Combine(
            FindRepositoryRoot(),
            "EzyImageViewer.App",
            "EzyImageViewer.App.csproj"));
        var transitivePackages = new[]
        {
            "Microsoft.WindowsAppSDK.AI",
            "Microsoft.WindowsAppSDK.ML",
            "Microsoft.Windows.AI.MachineLearning",
            "System.Numerics.Tensors",
        };

        foreach (var packageName in transitivePackages)
        {
            Assert.DoesNotContain(
                project.Descendants("PackageReference"),
                element =>
                    string.Equals(
                        element.Attribute("Include")?.Value,
                        packageName,
                        StringComparison.Ordinal));
        }
    }

    [Fact]
    public void MainPackaging_UsesCanonicalPackagedX64Output()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "packaging",
            "pack-msix.ps1"));

        Assert.Contains("-p:Packaged=true -p:Platform=x64", script, StringComparison.Ordinal);
        Assert.Contains(
            @"bin\packaged\x64\Release",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void CodecHostPackaging_ForcesDiagnosticsOffAndRejectsDiagnosticType()
    {
        var root = FindRepositoryRoot();
        var script = File.ReadAllText(Path.Combine(
            root,
            "packaging",
            "pack-codec-host-msix.ps1"));
        var targets = File.ReadAllText(Path.Combine(
            root,
            "EzyImageViewer.CodecHost",
            "Directory.Build.targets"));

        Assert.Contains(
            "dotnet build $hostProj -c Release -p:EnableCodecHostDiagnostics=false",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "Release CodecHost still contains DiagnosticOperationProcessor.",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            """<Compile Remove="DiagnosticOperationProcessor.cs" />""",
            targets,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PackagingScripts_ValidateReleaseInputsAndUseDeterministicTools()
    {
        var root = FindRepositoryRoot();
        foreach (var name in new[] { "pack-msix.ps1", "pack-codec-host-msix.ps1" })
        {
            var script = File.ReadAllText(Path.Combine(root, "packaging", name));

            Assert.Contains("Assert-MsixVersion", script, StringComparison.Ordinal);
            Assert.Contains("canonical four-part numeric version", script, StringComparison.Ordinal);
            Assert.Contains("X500DistinguishedName", script, StringComparison.Ordinal);
            Assert.Contains("$toolBins.Count -ne 1", script, StringComparison.Ordinal);
            Assert.Contains("SelectSingleNode", script, StringComparison.Ordinal);
            Assert.Contains("Manifest contains an unresolved placeholder.", script,
                StringComparison.Ordinal);
            Assert.Contains("-CreateDevCertificate only", script, StringComparison.Ordinal);
            Assert.Contains("release-helpers.ps1", script, StringComparison.Ordinal);
            Assert.Contains("Get-EzyPinnedBuildToolsRoot", script, StringComparison.Ordinal);
            Assert.Contains("Assert-EzyBuildOutputInventory", script, StringComparison.Ordinal);
            Assert.Contains("Write-EzyPackageContentsManifest", script, StringComparison.Ordinal);
            Assert.Contains("/XD ref NativeAotProbe /XF *.pdb", script,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                @"$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools",
                script,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Select-Object -First 1", script, StringComparison.Ordinal);
        }

        var mainScript = File.ReadAllText(Path.Combine(root, "packaging", "pack-msix.ps1"));
        Assert.DoesNotContain(@"Assets\*.png", mainScript, StringComparison.Ordinal);
        Assert.Contains("Square44x44Logo.png", mainScript, StringComparison.Ordinal);
        Assert.Contains("Square150x150Logo.png", mainScript, StringComparison.Ordinal);
        Assert.Contains("StoreLogo.png", mainScript, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseInventory_RejectsStaleBuildFilesAndUnlistedPackageContent()
    {
        var helper = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "packaging",
            "release-helpers.ps1"));

        Assert.Contains("FileListAbsolute", helper, StringComparison.Ordinal);
        Assert.Contains("undeclared or stale file", helper, StringComparison.Ordinal);
        Assert.Contains("PACKAGE-CONTENTS.sha256", helper, StringComparison.Ordinal);
        Assert.Contains("contains an unlisted file", helper, StringComparison.Ordinal);
        Assert.Contains("content hash mismatch", helper, StringComparison.Ordinal);
        Assert.Contains("AppxBlockMap.xml", helper, StringComparison.Ordinal);
        Assert.Contains("[Content_Types].xml", helper, StringComparison.Ordinal);
        Assert.Contains("AppxSignature.p7x", helper, StringComparison.Ordinal);
        Assert.Contains("packageFolders", helper, StringComparison.Ordinal);
        Assert.Contains("NUGET_PACKAGES", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void MainPackaging_PublishesOnlyAnAlreadyVerifiedStagedPair()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "packaging",
            "pack-msix.ps1"));
        var verifyIndex = script.IndexOf("verify-msix-release.ps1", StringComparison.Ordinal);
        var publishIndex = script.IndexOf("Publish-ArtifactSet", verifyIndex, StringComparison.Ordinal);

        Assert.Contains("'.staging-'", script, StringComparison.Ordinal);
        Assert.Contains("OutputDirectory = $staging", script, StringComparison.Ordinal);
        Assert.True(verifyIndex >= 0, "The staged package verifier call is missing.");
        Assert.True(publishIndex > verifyIndex, "Artifacts must be verified before publication.");
        Assert.Contains("Move-Item -LiteralPath $backup.Backup", script, StringComparison.Ordinal);
        Assert.Contains("-ErrorAction Stop", script, StringComparison.Ordinal);
        Assert.Contains("Artifact promotion failed and rollback was incomplete", script,
            StringComparison.Ordinal);
        Assert.Contains("Open-ExclusivePublishLock", script, StringComparison.Ordinal);
        Assert.Contains("[IO.FileShare]::None", script, StringComparison.Ordinal);
        Assert.Contains("finally", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseVerifier_InspectsActualPairWithoutInstallingOrTrustingIt()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "packaging",
            "verify-msix-release.ps1"));

        Assert.Contains("makeappx unpack", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GRTech.ezyImageViewer.CodecHost", script, StringComparison.Ordinal);
        Assert.Contains("SHA256SUMS", script, StringComparison.Ordinal);
        Assert.Contains("$RequireSignature", script, StringComparison.Ordinal);
        Assert.Contains("signtool.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("THIRD-PARTY-NOTICES.md", script, StringComparison.Ordinal);
        Assert.Contains("DiagnosticOperationProcessor", script, StringComparison.Ordinal);
        Assert.Contains("Assert-EzyPackageContentsManifest", script, StringComparison.Ordinal);
        Assert.Contains("Assert-EzyPackageMatchesBuildOutput", script, StringComparison.Ordinal);
        Assert.Contains("Verification requires either -RequireSignature or -RequireBuildOutputMatch",
            script,
            StringComparison.Ordinal);
        Assert.Contains("PACKAGE-CONTENTS.sha256", script, StringComparison.Ordinal);
        Assert.Contains("EzyImageViewer.CodecHost.exe", script, StringComparison.Ordinal);
        Assert.Contains("Magick.Native-Q8-x64.dll", script, StringComparison.Ordinal);
        Assert.Contains("Assets/Fonts/MaterialSymbolsOutlined.ttf", script,
            StringComparison.Ordinal);
        Assert.Contains(
            "6EB4B0BA0D788B9CFB4F22D68A768276142CBC3698177AC2803A0F1F1EB3207F",
            script,
            StringComparison.Ordinal);
        Assert.Contains("ArtifactsByName[$name]", script, StringComparison.Ordinal);
        Assert.Contains("EzyImageViewer\\.CodecHost", script, StringComparison.Ordinal);
        Assert.Contains("exactly one extension", script, StringComparison.Ordinal);
        Assert.Contains("runFullTrust", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Add-AppxPackage", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certutil", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CodecHostPackaging_UsesPrivateStagingAndCannotReplaceTheFixedPair()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "packaging",
            "pack-codec-host-msix.ps1"));

        Assert.Contains("[Parameter(Mandatory = $true)]", script, StringComparison.Ordinal);
        Assert.Contains("OutputDirectory must be a main-package staging directory", script,
            StringComparison.Ordinal);
        Assert.Contains(".codec-host-staging-", script, StringComparison.Ordinal);
        Assert.Contains("[IO.File]::Replace", script, StringComparison.Ordinal);
        Assert.Contains("[IO.FileShare]::None", script, StringComparison.Ordinal);
    }

    [Fact]
    public void BothReleasePackages_IncludeProjectLicenseAndThirdPartyNotices()
    {
        var root = FindRepositoryRoot();
        foreach (var projectPath in new[]
        {
            Path.Combine(root, "EzyImageViewer.App", "EzyImageViewer.App.csproj"),
            Path.Combine(root, "EzyImageViewer.CodecHost", "EzyImageViewer.CodecHost.csproj"),
        })
        {
            var project = File.ReadAllText(projectPath);
            Assert.Contains("..\\LICENSE", project, StringComparison.Ordinal);
            Assert.Contains("Link=", project, StringComparison.Ordinal);
            Assert.Contains("LICENSE.txt", project, StringComparison.Ordinal);
            Assert.Contains("..\\THIRD-PARTY-NOTICES.md", project, StringComparison.Ordinal);
        }
    }

    private static XDocument LoadManifest(string name) => XDocument.Load(Path.Combine(
        FindRepositoryRoot(),
        "packaging",
        name));

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
