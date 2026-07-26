using System.Xml.Linq;
using Xunit;

namespace EzyImageViewer.Tests.Packaging;

public sealed class PackagingContractTests
{
    private static readonly XNamespace Foundation =
        "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Uap =
        "http://schemas.microsoft.com/appx/manifest/uap/windows10";

    /// <summary>PDF/PSD와 외부 프로세스 호스트는 제거됨(ADR-0005/0006).
    /// 앱 패키지는 홀로 서야 함.</summary>
    [Fact]
    public void MainManifest_DeclaresNoPackageDependency()
    {
        var root = LoadManifest("AppxManifest.template.xml").Root!;

        Assert.Empty(root.Element(Foundation + "Dependencies")!
            .Elements(Foundation + "PackageDependency"));
    }

    /// <summary>배율 한정자 파일이 빠지면 고DPI에서 44px 원본이 늘어나 흐려진다.
    /// Content로 들어가야 PRI에 색인되므로 생성기와 프로젝트 양쪽을 함께 고정한다.</summary>
    [Fact]
    public void TileLogos_ShipEveryScaleQualifierThroughTheBuildOutput()
    {
        var root = FindRepositoryRoot();

        var generator = File.ReadAllText(Path.Combine(
            root, "packaging", "generate-brand-assets.ps1"));
        Assert.Contains("Scales = { 100, 125, 150, 200, 400 }", generator, StringComparison.Ordinal);
        Assert.Contains("TargetSizes = { 16, 24, 32, 48, 256 }", generator, StringComparison.Ordinal);
        Assert.Contains("_altform-unplated.png", generator, StringComparison.Ordinal);

        // 실제 파일이 있어야 함. 생성기만 고쳐 두고 돌리지 않은 상태를 잡는다.
        var assets = Directory
            .GetFiles(Path.Combine(root, "packaging", "Assets"), "*.png")
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var baseName in new[] { "Square44x44Logo", "Square150x150Logo", "StoreLogo" })
        {
            Assert.Contains($"{baseName}.png", assets);
            foreach (var scale in new[] { 100, 125, 150, 200, 400 })
                Assert.Contains($"{baseName}.scale-{scale}.png", assets);
        }
        foreach (var target in new[] { 16, 24, 32, 48, 256 })
        {
            Assert.Contains($"Square44x44Logo.targetsize-{target}.png", assets);
            Assert.Contains($"Square44x44Logo.targetsize-{target}_altform-unplated.png", assets);
        }

        // 패키징 스크립트가 다시 베끼면 PRI 색인본과 어긋난 사본이 섞인다.
        var pack = File.ReadAllText(Path.Combine(root, "packaging", "pack-msix.ps1"));
        Assert.DoesNotContain(
            "Copy-Item (Join-Path $PSScriptRoot \"Assets\\$assetName\")",
            pack,
            StringComparison.Ordinal);
        Assert.Contains(
            "Packaged build output is missing the tile logo",
            pack,
            StringComparison.Ordinal);
    }

    /// <summary>Store 등록 정보의 언어 목록이 여기서 나온다. 앱이 번역을 들고 있어도
    /// 매니페스트에 선언이 없으면 그 언어권 스토어에는 노출되지 않는다.</summary>
    [Fact]
    public void MainManifest_DeclaresEverySupportedLanguageInPolicyOrder()
    {
        var declared = LoadManifest("AppxManifest.template.xml")
            .Root!
            .Element(Foundation + "Resources")!
            .Elements(Foundation + "Resource")
            .Select(element => element.Attribute("Language")!.Value)
            .ToArray();

        // 순서까지 맞춘다. 첫 항목이 Store 기본 언어이자 리소스 최종 폴백이다.
        Assert.Equal(
            EzyImageViewer.Infrastructure.LanguagePolicy.SupportedTags.ToArray(),
            declared);
        Assert.Equal(
            EzyImageViewer.Infrastructure.LanguagePolicy.FallbackTag,
            declared[0]);
    }

    /// <summary>Store 매니페스트와 설정 안내가 같은 확장자를 표시해야 함.</summary>
    [Fact]
    public void MainManifest_RegistersTheFileAssociationPolicyImageTypes()
    {
        var association = LoadManifest("AppxManifest.template.xml")
            .Descendants(Uap + "FileTypeAssociation")
            .Single();

        Assert.Equal("ezyimageviewer.image", association.Attribute("Name")!.Value);

        var manifestTypes = association
            .Element(Uap + "SupportedFileTypes")!
            .Elements(Uap + "FileType")
            .Select(element => element.Value)
            .ToArray();

        Assert.Equal(
            EzyImageViewer.Infrastructure.FileAssociationPolicy.EssentialExtensions,
            manifestTypes);
    }

    /// <summary>Store 단일 배포에서는 비Store 업데이트 코드와 외부 다운로드 링크가 제품에 없어야 함.</summary>
    [Fact]
    public void StoreDistribution_HasNoSelfUpdateOrOutsideDownloadChannel()
    {
        var root = FindRepositoryRoot();

        var project = File.ReadAllText(Path.Combine(
            root, "EzyImageViewer.App", "EzyImageViewer.App.csproj"));
        Assert.Contains(
            "StoreChannel requires the Packaged build flavor.",
            project,
            StringComparison.Ordinal);
        // 제목 표시줄이 InformationalVersion의 +sha를 읽어 붙이므로 Store 빌드에서는 꺼 둔다.
        Assert.Contains("IncludeSourceRevisionInInformationalVersion", project, StringComparison.Ordinal);
        Assert.Contains(
            "Condition=\"'$(StoreChannel)' == 'true'\">false</IncludeSourceRevisionInInformationalVersion>",
            project,
            StringComparison.Ordinal);

        var services = File.ReadAllText(Path.Combine(
            root, "EzyImageViewer.App", "AppServices.cs"));
        Assert.DoesNotContain("GitHubReleaseUpdateChecker", services, StringComparison.Ordinal);
        Assert.DoesNotContain("TryStartUpdateCheck", services, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(
            root, "EzyImageViewer.Infrastructure", "GitHubReleaseUpdateChecker.cs")));
        Assert.False(File.Exists(Path.Combine(
            root, "EzyImageViewer.App", "ReleaseChannel.cs")));

        var settings = File.ReadAllText(Path.Combine(
            root, "EzyImageViewer.App", "SettingsDialogContent.cs"));
        Assert.Contains("AppStrings.UpdateStoreManagedNote", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckForUpdates", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("AboutProjectPage", settings, StringComparison.Ordinal);

        var pack = File.ReadAllText(Path.Combine(root, "packaging", "pack-msix.ps1"));
        Assert.Contains("-p:StoreChannel=", pack, StringComparison.Ordinal);
        // Store는 네 번째 자리를 예약한다. 0이 아닌 채로 제출하면 거절.
        Assert.Contains(
            "Store submissions require a zero revision",
            pack,
            StringComparison.Ordinal);
    }

    /// <summary>Store 제출은 Partner Center가 준 ID 세 값을 그대로 써야 한다.
    /// 매니페스트에 박아 두면 개발 ID로 제출하는 사고가 난다.</summary>
    [Fact]
    public void MainManifest_TakesItsStoreIdentityFromThePackagingScript()
    {
        var root = FindRepositoryRoot();

        var manifest = File.ReadAllText(Path.Combine(
            root, "packaging", "AppxManifest.template.xml"));
        foreach (var placeholder in new[]
                 {
                     "{{IDENTITY_NAME}}", "{{PUBLISHER}}", "{{VERSION}}", "{{PUBLISHER_DISPLAY_NAME}}",
                 })
        {
            Assert.Contains(placeholder, manifest, StringComparison.Ordinal);
        }

        var pack = File.ReadAllText(Path.Combine(root, "packaging", "pack-msix.ps1"));
        Assert.Contains("$IdentityName", pack, StringComparison.Ordinal);
        Assert.Contains("$PublisherDisplayName", pack, StringComparison.Ordinal);
        Assert.Contains("Assert-IdentityName", pack, StringComparison.Ordinal);
        Assert.Contains("Assert-PublisherDisplayName", pack, StringComparison.Ordinal);
        Assert.Contains("$identity.SetAttribute('Name', $IdentityName)", pack, StringComparison.Ordinal);
        Assert.Contains("$publisherDisplay.InnerText = $PublisherDisplayName", pack,
            StringComparison.Ordinal);
        // 검증기에 넘기지 않으면 ID 불일치를 아무도 못 잡는다.
        Assert.Contains("IdentityName = $IdentityName", pack, StringComparison.Ordinal);
        Assert.Contains("PublisherDisplayName = $PublisherDisplayName", pack, StringComparison.Ordinal);

        var verify = File.ReadAllText(Path.Combine(root, "packaging", "verify-msix-release.ps1"));
        Assert.Contains("$IdentityName 'main identity name'", verify, StringComparison.Ordinal);
        Assert.Contains("$PublisherDisplayName 'main publisher display name'", verify,
            StringComparison.Ordinal);
        Assert.DoesNotContain("'GRTech.ezyImageViewer' 'main identity name'", verify,
            StringComparison.Ordinal);
        // 요약 출력이 박힌 이름을 찍으면 릴리스 로그만 보고 잘못된 ID로 제출했다고 오독한다.
        Assert.DoesNotContain("identity: GRTech.ezyImageViewer", verify, StringComparison.Ordinal);
    }

    [Fact]
    public void MainAppProject_DoesNotReferenceTheRemovedCodecHost()
    {
        var project = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "EzyImageViewer.App",
            "EzyImageViewer.App.csproj"));

        Assert.DoesNotContain("CodecHost", project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("CodecProtocol", project, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("-p:InformationalVersion=$ReleaseVersion", script,
            StringComparison.Ordinal);
        Assert.Contains(
            @"bin\packaged\x64\Release",
            script,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PackagingScripts_ValidateReleaseInputsAndUseDeterministicTools()
    {
        var root = FindRepositoryRoot();
        {
            var script = File.ReadAllText(Path.Combine(root, "packaging", "pack-msix.ps1"));

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
    public void MainPackaging_PublishesOnlyAnAlreadyVerifiedStagedPackage()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "packaging",
            "pack-msix.ps1"));
        var verifyIndex = script.IndexOf("verify-msix-release.ps1", StringComparison.Ordinal);
        var publishIndex = script.IndexOf("Publish-ArtifactSet", verifyIndex, StringComparison.Ordinal);

        Assert.Contains("'.staging-'", script, StringComparison.Ordinal);
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
    public void ReleaseVerifier_InspectsTheActualPackageWithoutInstallingOrTrustingIt()
    {
        var script = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(),
            "packaging",
            "verify-msix-release.ps1"));

        Assert.Contains("makeappx unpack", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("$RequireSignature", script, StringComparison.Ordinal);
        Assert.Contains("signtool.exe", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("THIRD-PARTY-NOTICES.md", script, StringComparison.Ordinal);
        Assert.Contains("Assert-EzyPackageContentsManifest", script, StringComparison.Ordinal);
        Assert.Contains("Assert-EzyPackageMatchesBuildOutput", script, StringComparison.Ordinal);
        Assert.Contains("Verification requires either -RequireSignature or -RequireBuildOutputMatch",
            script,
            StringComparison.Ordinal);
        Assert.Contains("PACKAGE-CONTENTS.sha256", script, StringComparison.Ordinal);
        // Magick.NET은 테스트 픽스처 생성 전용. 패키지에 슬쩍 타면 검증기가 막아야 함.
        Assert.Contains("PDFtoImage|Magick|PDFium", script, StringComparison.Ordinal);
        Assert.Contains("Assets/Fonts/MaterialSymbolsOutlined.ttf", script,
            StringComparison.Ordinal);
        Assert.Contains(
            "6EB4B0BA0D788B9CFB4F22D68A768276142CBC3698177AC2803A0F1F1EB3207F",
            script,
            StringComparison.Ordinal);
        Assert.DoesNotContain("AppInstallerFile", script, StringComparison.Ordinal);
        Assert.Contains("exactly two extensions", script, StringComparison.Ordinal);
        Assert.Contains("exactly one protocol extension", script, StringComparison.Ordinal);
        Assert.Contains("exactly one file type association extension", script, StringComparison.Ordinal);
        Assert.Contains("runFullTrust", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Add-AppxPackage", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("certutil", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReleasePackage_IncludesProjectLicenseAndThirdPartyNotices()
    {
        var project = File.ReadAllText(Path.Combine(
            FindRepositoryRoot(), "EzyImageViewer.App", "EzyImageViewer.App.csproj"));

        Assert.Contains("..\\LICENSE", project, StringComparison.Ordinal);
        Assert.Contains("Link=", project, StringComparison.Ordinal);
        Assert.Contains("LICENSE.txt", project, StringComparison.Ordinal);
        Assert.Contains("..\\THIRD-PARTY-NOTICES.md", project, StringComparison.Ordinal);
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
