using System.Xml.Linq;
using Xunit;

namespace EzyImageViewer.Tests.Contracts;

public sealed class SettingsUiContractTests
{
    [Fact]
    public void UpdateFlow_IsManualBrowserOnlyAndHasNoAutomaticChecker()
    {
        var settings = File.ReadAllText(RepoFile(
            "EzyImageViewer.Infrastructure", "AppSettings.cs"));
        var services = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "AppServices.cs"));
        var windowManager = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "WindowManager.cs"));
        var settingsUi = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "SettingsDialogContent.cs"));
        var viewer = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));
        var appProject = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "EzyImageViewer.App.csproj"));

        var currentSettingsModel = settings[..settings.IndexOf(
            "internal sealed record LegacyAppSettingsV2",
            StringComparison.Ordinal)];
        Assert.DoesNotContain(
            "UpdateChecksEnabled",
            currentSettingsModel,
            StringComparison.Ordinal);
        Assert.DoesNotContain("TryStartUpdateCheck", services, StringComparison.Ordinal);
        Assert.DoesNotContain("ShutdownUpdateCheck", windowManager, StringComparison.Ordinal);
        Assert.DoesNotContain("EzyImageViewerUpdateEndpoint", appProject, StringComparison.Ordinal);
        Assert.Contains("CheckForUpdatesRequested", settingsUi, StringComparison.Ordinal);
        Assert.Contains(
            "Launcher.LaunchUriAsync(ReleaseDistributionPolicy.LatestReleasePage)",
            viewer,
            StringComparison.Ordinal);
        Assert.False(File.Exists(RepoFile(
            "EzyImageViewer.Infrastructure", "GitHubReleaseUpdateChecker.cs")));
    }

    [Fact]
    public void UpdateFlow_HasNoNetworkVersionDownloadInstallOrRestartImplementation()
    {
        string[] productDirectories =
        [
            "EzyImageViewer.App",
            "EzyImageViewer.Capture",
            "EzyImageViewer.CodecHost",
            "EzyImageViewer.CodecProtocol",
            "EzyImageViewer.Core",
            "EzyImageViewer.Imaging",
            "EzyImageViewer.Infrastructure",
            "EzyImageViewer.Rendering",
        ];
        var sources = productDirectories
            .SelectMany(directory => Directory.EnumerateFiles(
                RepoFile(directory),
                "*.cs",
                SearchOption.AllDirectories))
            .Where(path => !path.Split(Path.DirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .Select(File.ReadAllText)
            .ToArray();
        string[] forbiddenTokens =
        [
            "System.Net.Http",
            "HttpClient",
            "HttpRequestMessage",
            "WebClient",
            "api.github.com",
            "GitHubReleaseUpdateChecker",
            "UpdateCheckStatus",
            "UpdateCheckResult",
            "TryParseStableVersion",
            "LatestVersion",
            "Windows.Management.Deployment",
            "PackageManager",
            "DownloadFile",
            "DownloadData",
            "Process.Start",
            "AppInstance.Restart",
        ];

        Assert.All(forbiddenTokens, token => Assert.DoesNotContain(
            sources,
            source => source.Contains(token, StringComparison.Ordinal)));
    }

    [Fact]
    public void FileAssociations_AutomaticPathsAreOpenWithCandidatesOnlyAndNeverTouchTheUserChoice()
    {
        var registrar = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "FileAssociationRegistrar.cs"));
        var settingsUi = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "SettingsDialogContent.cs"));
        var viewer = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));

        // FR-APP-001: the registrar and every non-explicit path register per-user Open With
        // candidates only — never the default-app UserChoice, and never a writer call.
        Assert.Contains("Registry.CurrentUser", registrar, StringComparison.Ordinal);
        Assert.DoesNotContain("Registry.LocalMachine", registrar, StringComparison.Ordinal);
        // The registrar may READ UserChoice (read-only ProgId lifetime guard) but must never write
        // it: no CreateSubKey/DeleteSubKey on UserChoice and no Hash value are authored here.
        Assert.DoesNotContain("CreateSubKey(\"UserChoice\")", registrar, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteSubKey(\"UserChoice\"", registrar, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Hash\"", registrar, StringComparison.Ordinal);
        // The only UserChoice touch is the read-only guard opening the key.
        var guard = MethodBody(registrar, "private static bool AnyExtensionUsesProgIdAsDefault()");
        Assert.Contains("OpenSubKey", guard, StringComparison.Ordinal);
        Assert.Contains("UserChoice", guard, StringComparison.Ordinal);
        Assert.Contains(
            "FileAssociationPolicy.OpenWithProgidsKeyPath", registrar, StringComparison.Ordinal);
        Assert.Contains("SHChangeNotify", registrar, StringComparison.Ordinal);
        Assert.Contains(
            "FileAssociationRegistrar.Apply", settingsUi, StringComparison.Ordinal);
        Assert.Contains(
            "FileAssociationPolicy.GetDefaultAppsSettingsUri()", settingsUi, StringComparison.Ordinal);
        Assert.Contains("Launcher.LaunchUriAsync(target)", viewer, StringComparison.Ordinal);

        // The general Save path (ApplyPendingAssociations) and the page Apply button both funnel
        // through ApplyAssociations, which must call the candidate registrar and never the writer.
        Assert.DoesNotContain(
            "UserChoiceDefaultWriter", MethodBody(settingsUi, "public void ApplyPendingAssociations()"));
        Assert.DoesNotContain(
            "UserChoiceDefaultWriter", MethodBody(settingsUi, "private void ApplyAssociations()"));
        // Exactly one writer call in the whole file, and it is inside the explicit SetDefaultApp.
        Assert.Equal(1, CountOccurrences(settingsUi, "UserChoiceDefaultWriter.SetDefaults"));
        Assert.Contains(
            "UserChoiceDefaultWriter.SetDefaults",
            MethodBody(settingsUi, "private void SetDefaultApp()"),
            StringComparison.Ordinal);

        // The shared ProgId/command survives clearing all candidates while it is still a default,
        // so UserChoice-based double-clicks never dangle.
        Assert.Contains("AnyExtensionUsesProgIdAsDefault", registrar, StringComparison.Ordinal);
        Assert.Contains(
            "desired.Count == 0 && !AnyExtensionUsesProgIdAsDefault()",
            registrar, StringComparison.Ordinal);
    }

    [Fact]
    public void UserChoiceDefaultWriter_IsInstallerOnlyVerifiesEffectiveDefaultAndRestoresOnFailure()
    {
        var writer = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "UserChoiceDefaultWriter.cs"));
        var settingsUi = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "SettingsDialogContent.cs"));
        var appProject = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "EzyImageViewer.App.csproj"));

        // Experimental writer compiles only into the installer flavor: not Store/packaged, and not
        // the registry-free Portable build.
        Assert.StartsWith("#if EZY_UNPACKAGED", writer.TrimStart(), StringComparison.Ordinal);
        Assert.Contains(
            "<DefineConstants Condition=\"'$(Packaged)' != 'true' and '$(Portable)' != 'true'\">$(DefineConstants);EZY_UNPACKAGED",
            appProject, StringComparison.Ordinal);
        Assert.Contains("#if EZY_UNPACKAGED", settingsUi, StringComparison.Ordinal);

        // Effective (not machine) default check: AL_EFFECTIVE must be 1.
        Assert.Contains("Effective = 1", writer, StringComparison.Ordinal);
        Assert.Contains("AssociationLevel.Effective", writer, StringComparison.Ordinal);
        Assert.Contains("QueryCurrentDefault", writer, StringComparison.Ordinal);
        // Honest fail-closed detection and per-extension restore, not silent success.
        Assert.Contains("HashProtectionState.DetectionFailed", writer, StringComparison.Ordinal);
        Assert.Contains("UserChoiceStatus.Restored", writer, StringComparison.Ordinal);
        Assert.Contains("UserChoiceStatus.RestoreFailed", writer, StringComparison.Ordinal);
        Assert.Contains("private static bool Restore(", writer, StringComparison.Ordinal);
        // Timestamp binds to the key's own last-write minute, retried across the boundary.
        Assert.Contains("RegQueryInfoKeyW", writer, StringComparison.Ordinal);
        Assert.Contains("SameMinute", writer, StringComparison.Ordinal);
        Assert.DoesNotContain("DateTime.Now", writer, StringComparison.Ordinal);
        Assert.Contains("COMException", writer, StringComparison.Ordinal);
    }

    [Fact]
    public void UserChoiceHash_IsMarkedAsAnMplDerivativeWithProvenance()
    {
        var hash = File.ReadAllText(RepoFile(
            "EzyImageViewer.Infrastructure", "UserChoiceHash.cs"));
        var notices = File.ReadAllText(RepoFile("THIRD-PARTY-NOTICES.md"));

        Assert.StartsWith("// SPDX-License-Identifier: MPL-2.0", hash.TrimStart(), StringComparison.Ordinal);
        Assert.Contains("WindowsUserChoice.cpp", hash, StringComparison.Ordinal);
        Assert.Contains("MPL-2.0", notices, StringComparison.Ordinal);
        Assert.DoesNotContain("독립적으로 재구현", notices, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string source, string needle)
    {
        var count = 0;
        for (var i = source.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = source.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static string MethodBody(string source, string signature)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Signature not found: {signature}");
        var brace = source.IndexOf('{', start);
        var depth = 0;
        for (var i = brace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[start..(i + 1)];
        }
        throw new InvalidOperationException($"Unbalanced braces after {signature}.");
    }

    [Fact]
    public void HotkeyPolicy_IsSharedByUiPersistenceAndRuntime()
    {
        var settingsUi = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "SettingsDialogContent.cs"));
        var settingsStore = File.ReadAllText(RepoFile(
            "EzyImageViewer.Infrastructure", "AppSettings.cs"));
        var capture = File.ReadAllText(RepoFile(
            "EzyImageViewer.Capture", "Snipping", "CaptureCoordinator.cs"));

        Assert.Contains(
            "CaptureHotkeyPolicy.SupportedVirtualKeys",
            settingsUi,
            StringComparison.Ordinal);
        Assert.Contains(
            "CaptureHotkeyPolicy.IsSupportedChord",
            settingsStore,
            StringComparison.Ordinal);
        Assert.Contains(
            "CaptureHotkeyPolicy.IsSupportedChord",
            capture,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "VirtualKey is > 0 and < 0x100",
            settingsStore,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "HotkeyVirtualKey is > 0 and < 0x100",
            capture,
            StringComparison.Ordinal);
    }

    [Fact]
    public void UnavailableHotkey_UsesTheLocalizedActionableMessage()
    {
        var services = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "AppServices.cs"));
        var viewer = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));
        var resources = XDocument.Load(RepoFile(
            "EzyImageViewer.App", "Strings", "ko-KR", "Resources.resw"));
        var message = resources.Root!.Elements("data")
            .Single(element => string.Equals(
                (string?)element.Attribute("name"),
                "CaptureHotkeyUnavailable",
                StringComparison.Ordinal))
            .Element("value")!
            .Value;

        Assert.Contains(
            "throw new CaptureHotkeyUnavailableException(updated.CaptureHotkey);",
            services,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            """throw new InvalidOperationException("The requested global capture hotkey is unavailable.");""",
            services,
            StringComparison.Ordinal);
        Assert.Contains(
            "catch (CaptureHotkeyUnavailableException ex)",
            viewer,
            StringComparison.Ordinal);
        Assert.Contains(
            "FormatCaptureHotkey(ex.RequestedHotkey)",
            viewer,
            StringComparison.Ordinal);
        Assert.Contains("{0}", message, StringComparison.Ordinal);
        Assert.Contains("다른 앱", message, StringComparison.Ordinal);
        Assert.DoesNotContain("unavailable", message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RecoveryCompletenessAndAvailability_AreVisiblePersistentUiContracts()
    {
        var services = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "AppServices.cs"));
        var viewer = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));
        var xaml = XDocument.Load(RepoFile(
            "EzyImageViewer.App", "Views", "ViewerWindow.xaml"));
        var resources = XDocument.Load(RepoFile(
            "EzyImageViewer.App", "Strings", "ko-KR", "Resources.resw"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x =
            "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Contains("PendingRecoveryState = recoveryEnumeration", services);
        Assert.Contains("ResolvePreviousRecoveriesAsync(PendingRecoveryState)", services);
        Assert.Contains("RecoverySummaryEnumeration recoveryState", viewer);
        Assert.Contains("RecoveryStore.DiscardCandidates(candidates)", viewer);
        Assert.Contains("RecoveryAvailabilityChanged", services);
        Assert.Contains("RecoveryAvailabilityChanged += OnRecoveryAvailabilityChanged", viewer);
        Assert.Contains("RecoveryIncompleteWarning", ResourceNames(resources));

        var recoveryBar = xaml.Descendants(presentation + "InfoBar")
            .Single(element => (string?)element.Attribute(x + "Name")
                == "RecoveryAvailabilityBar");
        Assert.Equal("False", (string?)recoveryBar.Attribute("IsClosable"));
    }

    [Fact]
    public void RecentPreferenceTransitions_ReportClearFailureBeforeDurableEnable()
    {
        var services = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "AppServices.cs"));
        var coordinator = File.ReadAllText(RepoFile(
            "EzyImageViewer.Infrastructure", "RecentFileCoordinator.cs"));
        var viewer = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "Views", "ViewerWindow.xaml.cs"));

        var enableIndex = services.IndexOf(
            "await RecentFiles.SetEnabledAsync(true);",
            StringComparison.Ordinal);
        var saveIndex = services.IndexOf(
            "SettingsStore.Save(updated)",
            StringComparison.Ordinal);
        Assert.True(enableIndex >= 0 && enableIndex < saveIndex);
        Assert.Contains("deferredRecentClearFailure", services);
        Assert.Contains("updated = updated with { RecentFilesEnabled = false };", services);
        Assert.DoesNotContain("updated = current with { RecentFilesEnabled = false };", services);
        Assert.Contains("RecentFileHistoryClearException", coordinator);
        Assert.Contains("catch (RecentFileHistoryClearException)", viewer);
    }

    [Fact]
    public void LastWindowCloseFailure_ReopensSessionRouting()
    {
        var windowManager = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "WindowManager.cs"));

        Assert.Contains("_sessionCompletionStarted = false;", windowManager);
        var logDrain = windowManager.IndexOf(
            "await AppServices.Logs.DrainAsync();",
            StringComparison.Ordinal);
        var captureShutdown = windowManager.LastIndexOf(
            "AppServices.ShutdownCapture();",
            StringComparison.Ordinal);
        var releaseKey = windowManager.IndexOf(
            "Program.ReleaseInstanceKey();",
            StringComparison.Ordinal);
        Assert.True(logDrain >= 0 && captureShutdown > logDrain && releaseKey > captureShutdown);
    }

    [Fact]
    public void AppDataProtectionFailure_IsPersistentAndLocalized()
    {
        var services = File.ReadAllText(RepoFile(
            "EzyImageViewer.App", "AppServices.cs"));
        var xaml = XDocument.Load(RepoFile(
            "EzyImageViewer.App", "Views", "ViewerWindow.xaml"));
        var resources = XDocument.Load(RepoFile(
            "EzyImageViewer.App", "Strings", "ko-KR", "Resources.resw"));
        XNamespace presentation =
            "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x =
            "http://schemas.microsoft.com/winfx/2006/xaml";

        Assert.Contains("AppDataSecurity.EnsureProtected(requestedPaths)", services);
        Assert.Contains("AppDataProtectionFailure", services);
        Assert.Contains("AppDataProtectionPersistent", ResourceNames(resources));
        var bar = xaml.Descendants(presentation + "InfoBar")
            .Single(element => (string?)element.Attribute(x + "Name")
                == "DataProtectionBar");
        Assert.Equal("False", (string?)bar.Attribute("IsClosable"));
    }

    private static IReadOnlyList<string> ResourceNames(XDocument document) =>
        document.Root!.Elements("data")
            .Select(element => (string?)element.Attribute("name"))
            .OfType<string>()
            .ToArray();

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
