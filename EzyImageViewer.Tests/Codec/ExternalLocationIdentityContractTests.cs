using System.Diagnostics;
using System.Xml.Linq;
using Xunit;

namespace EzyImageViewer.Tests.Codec;

public sealed class ExternalLocationIdentityContractTests
{
    private static readonly XNamespace Foundation =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Uap10 =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    private static readonly XNamespace Restricted =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
    private static readonly XNamespace Assembly =
        "urn:schemas-microsoft-com:asm.v1";
    private static readonly XNamespace Msix =
        "urn:schemas-microsoft-com:msix.v1";

    [Fact]
    public void PackageTemplate_DefinesTheExactExternalIdentitySurface()
    {
        var root = XDocument.Load(RepoFile(
            "packaging", "ExternalLocation.AppxManifest.template.xml")).Root!;

        Assert.Equal("uap uap10", root.Attribute("IgnorableNamespaces")!.Value);
        Assert.Equal(Restricted, root.GetNamespaceOfPrefix("rescap"));

        var identity = root.Element(Foundation + "Identity")!;
        Assert.Equal("GRTech.ezyImageViewer", identity.Attribute("Name")!.Value);
        Assert.Equal("{{PUBLISHER}}", identity.Attribute("Publisher")!.Value);
        Assert.Equal("{{VERSION}}", identity.Attribute("Version")!.Value);
        Assert.Equal("neutral", identity.Attribute("ProcessorArchitecture")!.Value);

        Assert.Equal(
            "true",
            root.Element(Foundation + "Properties")!
                .Element(Uap10 + "AllowExternalContent")!.Value,
            ignoreCase: true);

        var dependencies = root.Element(Foundation + "Dependencies")!;
        var target = dependencies.Element(Foundation + "TargetDeviceFamily")!;
        Assert.Equal("{{MIN_VERSION}}", target.Attribute("MinVersion")!.Value);
        var host = dependencies.Element(Foundation + "PackageDependency")!;
        Assert.Equal("GRTech.ezyImageViewer.CodecHost", host.Attribute("Name")!.Value);
        Assert.Equal("{{PUBLISHER}}", host.Attribute("Publisher")!.Value);
        Assert.Equal("{{CODEC_HOST_VERSION}}", host.Attribute("MinVersion")!.Value);

        var application = Assert.Single(root.Element(Foundation + "Applications")!
            .Elements(Foundation + "Application"));
        Assert.Equal("App", application.Attribute("Id")!.Value);
        Assert.Equal("ezyImageViewer.exe", application.Attribute("Executable")!.Value);
        Assert.Null(application.Attribute("EntryPoint"));
        Assert.Equal("mediumIL", application.Attribute(Uap10 + "TrustLevel")!.Value);
        Assert.Equal("win32App", application.Attribute(Uap10 + "RuntimeBehavior")!.Value);
        var visualElements = application.Element(Uap + "VisualElements")!;
        Assert.Equal("none", visualElements.Attribute("AppListEntry")!.Value);
        Assert.Null(visualElements.Attribute("Wide310x150Logo"));
        Assert.Null(visualElements.Element(Uap + "DefaultTile"));

        var extension = Assert.Single(application
            .Element(Foundation + "Extensions")!
            .Elements(Uap + "Extension"));
        var protocol = Assert.Single(extension.Elements(Uap + "Protocol"));
        Assert.Equal("ezyimageviewer", protocol.Attribute("Name")!.Value);
        Assert.Equal("default", protocol.Attribute("DesiredView")!.Value);

        var capabilities = root.Element(Foundation + "Capabilities")!
            .Elements(Restricted + "Capability")
            .Select(element => element.Attribute("Name")!.Value)
            .ToArray();
        Assert.Equal(
            new[] { "runFullTrust", "unvirtualizedResources" },
            capabilities);
    }

    [Fact]
    public void FusionTemplate_BindsTheExecutableToTheSameIdentity()
    {
        var root = XDocument.Load(RepoFile(
            "packaging", "ExternalLocation.App.manifest.template.xml")).Root!;

        Assert.Equal(Assembly + "assembly", root.Name);
        var identity = root.Element(Assembly + "assemblyIdentity")!;
        Assert.Equal("EzyImageViewer.App", identity.Attribute("name")!.Value);
        Assert.Equal("1.0.0.0", identity.Attribute("version")!.Value);

        var msix = root.Element(Msix + "msix")!;
        Assert.Equal("{{PUBLISHER}}", msix.Attribute("publisher")!.Value);
        Assert.Equal("GRTech.ezyImageViewer", msix.Attribute("packageName")!.Value);
        Assert.Equal("App", msix.Attribute("applicationId")!.Value);
    }

    [Fact]
    public void ExternalBuildFlavor_IsIsolatedAndRequiresTheGeneratedManifest()
    {
        var common = File.ReadAllText(RepoFile("Directory.Build.props"));
        var project = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "EzyImageViewer.App.csproj"));

        Assert.Contains(
            """Condition="'$(ExternalIdentity)' == 'true'">""",
            common,
            StringComparison.Ordinal);
        Assert.Contains(@"<BaseOutputPath>bin\external\</BaseOutputPath>", common);
        Assert.Contains(
            @"<BaseIntermediateOutputPath>obj\external\</BaseIntermediateOutputPath>",
            common);
        Assert.Contains(
            """<ApplicationManifest Condition="'$(ExternalApplicationManifest)' != ''">""",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExternalIdentity and Packaged build flavors are mutually exclusive.",
            project,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExternalIdentity requires a generated ExternalApplicationManifest.",
            project,
            StringComparison.Ordinal);
        Assert.Contains(@"packaging\Assets\StoreLogo.png", project);
        Assert.Contains(@"packaging\Assets\Square44x44Logo.png", project);
        Assert.Contains(@"packaging\Assets\Square150x150Logo.png", project);
    }

    [Fact]
    public void ExternalFoundationScripts_DoNotInstallTrustSignOrAcceptWix()
    {
        string[] scriptNames =
        [
            "external-location-helpers.ps1",
            "generate-external-location-manifests.ps1",
            "test-external-location-contract.ps1",
            "msi-payload-helpers.ps1",
            "stage-msi-foundation.ps1",
            "verify-msi-foundation.ps1",
            "verify-external-location-build.ps1",
            "identity-registration-contract.ps1",
        ];
        string[] forbiddenTokens =
        [
            "Add-AppxPackage",
            "Remove-AppxPackage",
            "Add-AppxProvisionedPackage",
            "Remove-AppxProvisionedPackage",
            "Import-Certificate",
            "New-SelfSignedCertificate",
            "Cert:" + (char)92,
            "AcceptEula",
            "WixToolset.Sdk",
            "Set-AuthenticodeSignature",
            "& $signTool",
            "Start-Process signtool",
        ];

        foreach (var scriptName in scriptNames)
        {
            var script = File.ReadAllText(RepoFile("packaging", scriptName));
            Assert.All(forbiddenTokens, token => Assert.DoesNotContain(
                token,
                script,
                StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void ManifestWriter_UsesAtomicReplacementWithoutOverwriteMove()
    {
        var helper = File.ReadAllText(RepoFile(
            "packaging", "external-location-helpers.ps1"));

        Assert.Contains(
            "[IO.File]::Replace($temporaryPath, $fullPath, $backupPath)",
            helper,
            StringComparison.Ordinal);
        Assert.Contains(
            "[IO.File]::Move($temporaryPath, $fullPath)",
            helper,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "[IO.File]::Move($temporaryPath, $fullPath, $true)",
            helper,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FoundationStageAndVerifier_InspectTheRealExternalBuildWithoutMutation()
    {
        var stage = File.ReadAllText(RepoFile(
            "packaging", "stage-msi-foundation.ps1"));
        var verify = File.ReadAllText(RepoFile(
            "packaging", "verify-msi-foundation.ps1"));
        var sourceGate = File.ReadAllText(RepoFile(
            "packaging", "verify-external-location-build.ps1"));
        var workflow = File.ReadAllText(RepoFile(
            ".github", "workflows", "ci.yml"));

        Assert.Contains("-p:ExternalIdentity=true", stage, StringComparison.Ordinal);
        Assert.Contains("--self-contained", stage, StringComparison.Ordinal);
        Assert.Contains("MsiPublish.targets", stage, StringComparison.Ordinal);
        Assert.Contains("Assert-EzyMsiPayload", stage, StringComparison.Ordinal);
        Assert.Contains("Assert-EzyExternalApplicationManifest", stage,
            StringComparison.Ordinal);
        Assert.Contains("-Embedded", stage, StringComparison.Ordinal);
        Assert.Contains("makeappx pack /o /nv", stage,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("makeappx unpack /nv", verify,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-Embedded", verify, StringComparison.Ordinal);
        Assert.Contains("Compare-Object", verify, StringComparison.Ordinal);
        Assert.Contains("External identity asset hash mismatch", verify,
            StringComparison.Ordinal);
        Assert.Contains("stage-msi-foundation.ps1", sourceGate, StringComparison.Ordinal);
        Assert.Contains("verify-msi-foundation.ps1", sourceGate, StringComparison.Ordinal);
        Assert.Contains("ezy-external-build-verify-", sourceGate, StringComparison.Ordinal);
        Assert.Contains(
            "External-location source-to-artifact verification passed.",
            sourceGate,
            StringComparison.Ordinal);
        Assert.Contains(
            "-File packaging/verify-external-location-build.ps1",
            workflow,
            StringComparison.Ordinal);
        Assert.Contains(
            "-File packaging/test-msi-foundation-contract.ps1",
            workflow,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            @"EzyImageViewer.CodecHost\obj\external",
            stage,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            @"EzyImageViewer.CodecHost\obj\external",
            verify,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PowerShellHarness_ExercisesTheStrictGeneratorContract()
    {
        var repositoryRoot = Path.GetDirectoryName(RepoFile("EzyImageViewer.slnx"))!;
        var startInfo = new ProcessStartInfo
        {
            FileName = Path.Combine(
                Environment.SystemDirectory,
                "WindowsPowerShell",
                "v1.0",
                "powershell.exe"),
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(RepoFile(
            "packaging", "test-external-location-contract.ps1"));

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("PowerShell could not be started.");
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(outputTask, errorTask);
            throw;
        }

        var output = await outputTask;
        var error = await errorTask;
        Assert.True(
            process.ExitCode == 0,
            $"External-location contract harness failed.{Environment.NewLine}{output}{error}");
        Assert.True(
            string.IsNullOrWhiteSpace(error),
            $"External-location contract harness wrote to stderr:{Environment.NewLine}{error}");
        Assert.Contains(
            "External-location contract tests passed: 31",
            output,
            StringComparison.Ordinal);
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
