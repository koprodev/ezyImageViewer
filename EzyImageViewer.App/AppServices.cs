using EzyImageViewer.Capture.Clipboard;
using EzyImageViewer.Capture.Snipping;
using EzyImageViewer.Core.Activation;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging;
using EzyImageViewer.Imaging.Codecs;
using EzyImageViewer.Infrastructure;

namespace EzyImageViewer.App;

public enum RecoveryAvailability
{
    NotStarted,
    Available,
    Degraded,
    Unavailable,
}

/// <summary>
/// Composition root. The router exists before any UI so activations posted from Program.Main
/// buffer until <see cref="App.OnLaunched"/> attaches the window manager.
/// </summary>
public static class AppServices
{
    private static readonly object SettingsSync = new();
    private static readonly SemaphoreSlim SettingsUpdateGate = new(1, 1);
    private static AppSettings _settings;
    private static ToolDefaults _runtimeToolDefaults;
    private static StartupBenchmarkRequest? _startupBenchmark;
    private static int _recoverySmokeConfigured;
    private static int _recoveryAvailability;
    private static int _recoveryFailureVersion;

    private sealed record StartupBenchmarkRequest(string ResultPath, long ProcessStartTimestamp);

    static AppServices()
    {
        IsSafeMode = Environment.GetCommandLineArgs().Contains(
            "--safe-mode", StringComparer.Ordinal);
        var requestedPaths = Program.DiagnosticDataRoot is { } diagnosticRoot
            ? new AppDataPaths(diagnosticRoot)
            : new AppDataPaths(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ezyImageViewer"));
        AppDataProtectionException? protectionFailure = null;
        try
        {
            AppDataSecurity.EnsureProtected(requestedPaths);
            DataPaths = requestedPaths;
        }
        catch (AppDataProtectionException ex)
        {
            if (Program.DiagnosticDataRoot is not null)
                throw;
            protectionFailure = ex;
            DataPaths = AppDataSecurity.CreateProtectedEphemeral();
        }
        AppDataProtectionFailure = protectionFailure;
        SettingsStore = new AppSettingsStore(DataPaths);
        _settings = protectionFailure is null
            ? SettingsStore.Load()
            : new AppSettings
            {
                ClipboardWatchEnabled = false,
                RecentFilesEnabled = false,
            };
        _runtimeToolDefaults = _settings.ToolDefaults;
        ApplicationVersion = GetApplicationVersion();
        Logs = new StructuredLogService(new StructuredLocalLogger(
            DataPaths,
            new StructuredLocalLoggerOptions { ApplicationVersion = ApplicationVersion }));
        if (protectionFailure is not null)
        {
            _ = Logs.TryEnqueue(
                LocalLogLevel.Error,
                new StructuredLogEvent
                {
                    Name = StructuredLogEventNames.AppDataProtectionFailed,
                    ErrorCode = "dacl_migration_failed",
                },
                protectionFailure);
        }
        StartupHealth = new StartupHealthTracker(
            DataPaths,
            reportError: ex => _ = Logs.TryEnqueue(
                LocalLogLevel.Error,
                new StructuredLogEvent
                {
                    Name = StructuredLogEventNames.StartupFailureRecorded,
                    ErrorCode = "state_access_failed",
                },
                ex));
        StartupHealthStatus = StartupHealth.GetStatus();
        SafeModeSuggested = !IsSafeMode && StartupHealthStatus.ShouldOfferSafeMode;
        RecentFiles = new RecentFileCoordinator(
            new RecentFileStore(DataPaths),
            _settings.RecentFilesEnabled,
            reportError: ex => _ = Logs.TryEnqueue(
                LocalLogLevel.Warning,
                new StructuredLogEvent
                {
                    Name = StructuredLogEventNames.RecentFileOperationFailed,
                    ErrorCode = "store_operation_failed",
                },
                ex));
        if (IsSafeMode)
            RecentFiles.PauseForSession();
        RecoveryStore = new RecoveryStore(
            DataPaths,
            reportError: ex => ReportRecoveryFailure(
                ex,
                StructuredLogEventNames.RecoveryCleanupFailed,
                "store_access_failed"));
        Recovery = new RecoverySessionCoordinator(
            RecoveryStore,
            reportError: ex => ReportRecoveryFailure(
                ex,
                StructuredLogEventNames.RecoveryOperationFailed,
                "operation_failed"),
            reportSaved: record => _ = Logs.TryEnqueue(
                LocalLogLevel.Information,
                new StructuredLogEvent
                {
                    Name = StructuredLogEventNames.RecoverySaved,
                }),
            reportAvailable: ReportRecoveryAvailable);
    }

    public static ActivationRouter Router { get; } = new();
    public static InputLimits Limits { get; } = InputLimits.Default;
    public static DocumentLoader Loader { get; } = new(Limits);
    public static IAppDataPaths DataPaths { get; }
    public static AppDataProtectionException? AppDataProtectionFailure { get; }
    public static AppSettingsStore SettingsStore { get; }
    public static RecentFileCoordinator RecentFiles { get; }
    public static StructuredLogService Logs { get; }
    public static RecoveryStore RecoveryStore { get; }
    public static RecoverySessionCoordinator Recovery { get; }
    public static StartupHealthTracker StartupHealth { get; }
    public static StartupHealthStatus StartupHealthStatus { get; }
    public static string ApplicationVersion { get; }
    public static bool IsSafeMode { get; private set; }
    public static bool SafeModeSuggested { get; private set; }
    public static Guid RecoverySessionId { get; private set; }
    public static bool RecoveryEnabled { get; private set; }
    public static RecoverySummaryEnumeration PendingRecoveryState { get; private set; } =
        new([], IsComplete: true);
    public static IReadOnlyList<RecoveryRecordSummary> PendingRecoveries =>
        PendingRecoveryState.Summaries;
    public static RecoveryAvailability RecoveryAvailability =>
        (RecoveryAvailability)Volatile.Read(ref _recoveryAvailability);
    public static WindowManager? Windows { get; private set; }

    public static AppSettings Settings
    {
        get
        {
            lock (SettingsSync)
                return _settings;
        }
    }

    public static AppSettings RuntimeSettings
    {
        get
        {
            var settings = Settings;
            return IsSafeMode
                ? settings with
                {
                    ClipboardWatchEnabled = false,
                    RecentFilesEnabled = false,
                    IncludeSubfoldersInNavigation = false,
                }
                : settings;
        }
    }

    public static ToolDefaults RuntimeToolDefaults
    {
        get
        {
            lock (SettingsSync)
                return _runtimeToolDefaults;
        }
    }

    public static event Action<AppSettings>? SettingsChanged;
    public static event Action<RecoveryAvailability>? RecoveryAvailabilityChanged;

    /// <summary>Capture integration (M7); null in unattended runs and after the last window closed.</summary>
    public static CaptureCoordinator? Capture { get; private set; }

    public static void InitializeUi(Microsoft.UI.Dispatching.DispatcherQueue dispatcherQueue)
    {
        Windows = new WindowManager(dispatcherQueue);

        void StartRecovery()
        {
            if (RecoveryEnabled)
                return;
            if (AppDataProtectionFailure is not null)
                throw new AppDataProtectionException(
                    "Recovery is unavailable because local data protection failed.",
                    AppDataProtectionFailure);
            var failureVersion = Volatile.Read(ref _recoveryFailureVersion);
            var crashMarkers = RecoveryStore.EnumerateCrashMarkers();
            // A valid orphan snapshot is still user data: a marker can be lost or quarantined
            // before the authenticated payload. Delete marker-only sessions only after a complete
            // snapshot classification.
            var recoveryEnumeration = RecoveryStore.EnumerateSummaryState();
            PendingRecoveryState = recoveryEnumeration;
            if (recoveryEnumeration.IsComplete)
            {
                foreach (var marker in crashMarkers.Where(marker =>
                    !PendingRecoveries.Any(summary => summary.SessionId == marker.SessionId)))
                    RecoveryStore.CompleteSession(marker.SessionId);
            }
            RecoverySessionId = Guid.NewGuid();
            Recovery.Start(RecoverySessionId);
            RecoveryEnabled = true;
            PublishRecoveryAvailability(
                recoveryEnumeration.IsComplete
                    && failureVersion == Volatile.Read(ref _recoveryFailureVersion)
                    ? RecoveryAvailability.Available
                    : RecoveryAvailability.Degraded);
        }

        bool TryStartRecovery()
        {
            try
            {
                StartRecovery();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RecoveryEnabled = false;
                RecoverySessionId = Guid.Empty;
                PendingRecoveryState = new RecoverySummaryEnumeration([], IsComplete: false);
                PublishRecoveryAvailability(RecoveryAvailability.Unavailable);
                _ = Logs.TryEnqueue(
                    LocalLogLevel.Error,
                    new StructuredLogEvent
                    {
                        Name = StructuredLogEventNames.RecoveryOperationFailed,
                        ErrorCode = "startup_initialization_failed",
                    },
                    ex);
                return false;
            }
        }

        void StartRouter()
        {
            // Handler contract (P0-2): enqueue to the UI thread and return — never await load completion.
            Router.Start(envelope =>
            {
                var request = ActivationRoutingPolicy.Apply(
                    envelope.Request,
                    RuntimeSettings,
                    IsSafeMode);
                dispatcherQueue.TryEnqueue(() => Windows!.Route(request));
                return Task.CompletedTask;
            });
        }

        if (SafeModeSuggested)
        {
            var startupWindow = Windows.EnsurePrimary();
            _ = dispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    IsSafeMode = await startupWindow.OfferSafeModeAsync();
                    SafeModeSuggested = false;
                    if (IsSafeMode)
                    {
                        RecentFiles.PauseForSession();
                        Windows.ApplySettings(RuntimeSettings);
                        _ = Logs.TryEnqueue(
                            LocalLogLevel.Warning,
                            new StructuredLogEvent
                            {
                                Name = StructuredLogEventNames.SafeModeEnabled,
                                ErrorCode = "repeated_startup_failure",
                            });
                    }
                    else
                    {
                        if (!TryStartRecovery())
                        {
                            IsSafeMode = true;
                            RecentFiles.PauseForSession();
                            Windows.ApplySettings(RuntimeSettings);
                            return;
                        }
                        EnsureCapture(RuntimeSettings);
                        Windows.ApplySettings(RuntimeSettings);
                        if (PendingRecoveries.Count > 0)
                            await startupWindow.ResolvePreviousRecoveriesAsync(PendingRecoveryState);
                    }
                }
                catch (Exception ex)
                {
                    IsSafeMode = true;
                    RecentFiles.PauseForSession();
                    Windows.ApplySettings(RuntimeSettings);
                    ReportRecoveryFailure(
                        ex,
                        StructuredLogEventNames.RecoveryCleanupFailed,
                        "startup_recovery_failed");
                }
                finally
                {
                    StartRouter();
                }
            });
        }
        else if (IsSafeMode)
        {
            StartRouter();
        }
        else
        {
            var recoveryStarted = TryStartRecovery();
            if (!recoveryStarted || PendingRecoveries.Count == 0)
            {
                EnsureCapture(RuntimeSettings);
                StartRouter();
            }
            else
            {
                var recoveryWindow = Windows.EnsurePrimary();
                _ = dispatcherQueue.TryEnqueue(async () =>
                {
                    try
                    {
                        EnsureCapture(RuntimeSettings);
                        Windows.ApplySettings(RuntimeSettings);
                        await recoveryWindow.ResolvePreviousRecoveriesAsync(PendingRecoveryState);
                    }
                    catch (Exception ex)
                    {
                        ReportRecoveryFailure(
                            ex,
                            StructuredLogEventNames.RecoveryCleanupFailed,
                            "startup_recovery_failed");
                    }
                    finally
                    {
                        if (!Program.IsRecoverySmokeVerify)
                            StartRouter();
                    }
                });
            }
        }
        _ = Logs.TryEnqueue(
            LocalLogLevel.Information,
            new StructuredLogEvent { Name = StructuredLogEventNames.AppStarted });
    }

    private static void ReportRecoveryFailure(
        Exception exception,
        string eventName,
        string errorCode)
    {
        Interlocked.Increment(ref _recoveryFailureVersion);
        if (RecoveryEnabled)
            PublishRecoveryAvailability(RecoveryAvailability.Degraded);
        _ = Logs.TryEnqueue(
            LocalLogLevel.Error,
            new StructuredLogEvent
            {
                Name = eventName,
                ErrorCode = errorCode,
            },
            exception);
    }

    private static void ReportRecoveryAvailable()
    {
        if (RecoveryEnabled)
            PublishRecoveryAvailability(RecoveryAvailability.Available);
    }

    private static void PublishRecoveryAvailability(RecoveryAvailability availability)
    {
        var previous = (RecoveryAvailability)Interlocked.Exchange(
            ref _recoveryAvailability,
            (int)availability);
        if (previous != availability)
            RecoveryAvailabilityChanged?.Invoke(availability);
    }

    /// <summary>Persists one validated immutable snapshot and publishes it process-wide. A privacy
    /// restriction is applied before disk I/O and remains restrictive if persistence fails.</summary>
    public static async Task<AppSettings> UpdateSettingsAsync(
        Func<AppSettings, AppSettings> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (AppDataProtectionFailure is not null)
        {
            throw new AppDataProtectionException(
                "Settings persistence is unavailable because local data protection failed.",
                AppDataProtectionFailure);
        }
        await SettingsUpdateGate.WaitAsync(cancellationToken);
        try
        {
            var current = Settings;
            var updated = update(current)
                ?? throw new InvalidOperationException("The settings update returned null.");
            updated = updated with { ToolDefaults = RuntimeToolDefaults };
            AppSettingsStore.Validate(updated);
            var previousHotkey = current.CaptureHotkey;
            var hotkeyChanged = previousHotkey != updated.CaptureHotkey;
            var recentEnablePrepared = false;
            var hotkeyApplied = false;
            RecentFileHistoryClearException? deferredRecentClearFailure = null;
            try
            {
                if (hotkeyChanged
                    && Capture is { } capture)
                {
                    if (!capture.TryChangeHotkey(
                        (uint)updated.CaptureHotkey.Modifiers,
                        (uint)updated.CaptureHotkey.VirtualKey))
                    {
                        throw new CaptureHotkeyUnavailableException(updated.CaptureHotkey);
                    }
                    hotkeyApplied = true;
                }
                if (current.ClipboardWatchEnabled && !updated.ClipboardWatchEnabled)
                    Capture?.SetWatchEnabled(false);
                if (current.RecentFilesEnabled && !updated.RecentFilesEnabled)
                {
                    try
                    {
                        await RecentFiles.SetEnabledAsync(false);
                    }
                    catch (RecentFileHistoryClearException ex)
                    {
                        // The restrictive preference must still be persisted; the caller is
                        // notified after the disabled state is durably published.
                        deferredRecentClearFailure = ex;
                        updated = updated with { RecentFilesEnabled = false };
                    }
                }
                if (deferredRecentClearFailure is null
                    && !current.RecentFilesEnabled
                    && updated.RecentFilesEnabled)
                {
                    await RecentFiles.SetEnabledAsync(true);
                    recentEnablePrepared = true;
                }
                var saveCancellation = deferredRecentClearFailure is null
                    ? cancellationToken
                    : CancellationToken.None;
                await Task.Run(() => SettingsStore.Save(updated), saveCancellation);
            }
            catch
            {
                if (hotkeyApplied && Capture is { } rollbackCapture)
                {
                    _ = rollbackCapture.TryChangeHotkey(
                        (uint)previousHotkey.Modifiers,
                        (uint)previousHotkey.VirtualKey);
                }
                if (recentEnablePrepared)
                {
                    try
                    {
                        await RecentFiles.SetEnabledAsync(false);
                    }
                    catch (RecentFileHistoryClearException)
                    {
                        // Admission closes before the delete attempt, preserving fail-closed state.
                    }
                }
                throw;
            }
            lock (SettingsSync)
                _settings = updated;
            Capture?.SetWatchEnabled(RuntimeSettings.ClipboardWatchEnabled);
            Windows?.ApplySettings(RuntimeSettings);
            SettingsChanged?.Invoke(updated);
            _ = Logs.TryEnqueue(
                LocalLogLevel.Information,
                new StructuredLogEvent { Name = StructuredLogEventNames.SettingsSaved });
            if (deferredRecentClearFailure is not null)
                throw deferredRecentClearFailure;
            return updated;
        }
        finally
        {
            SettingsUpdateGate.Release();
        }
    }

    internal static void PublishToolDefaults(ToolDefaults baseline, ToolDefaults edited)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(edited);
        lock (SettingsSync)
        {
            var merged = AppSettingsMerger.MergeToolDefaultChanges(
                baseline,
                edited,
                _runtimeToolDefaults);
            AppSettingsStore.Validate(_settings with { ToolDefaults = merged });
            _runtimeToolDefaults = merged;
        }
    }

    internal static async Task PersistToolDefaultsAsync()
    {
        await SettingsUpdateGate.WaitAsync();
        try
        {
            var updated = Settings with { ToolDefaults = RuntimeToolDefaults };
            AppSettingsStore.Validate(updated);
            await Task.Run(() => SettingsStore.Save(updated));
            lock (SettingsSync)
                _settings = updated;
            _ = Logs.TryEnqueue(
                LocalLogLevel.Information,
                new StructuredLogEvent { Name = StructuredLogEventNames.SettingsSaved });
        }
        finally
        {
            SettingsUpdateGate.Release();
        }
    }

    public static void ShutdownCapture()
    {
        Capture?.Dispose();
        Capture = null;
    }

    internal static void ConfigureStartupBenchmark(
        string resultPath,
        long processStartTimestamp)
    {
        if (!Program.IsStartupBenchmark)
            throw new InvalidOperationException("Startup benchmark configuration is diagnostic-only.");
        ArgumentException.ThrowIfNullOrWhiteSpace(resultPath);
        if (processStartTimestamp <= 0)
            throw new ArgumentOutOfRangeException(nameof(processStartTimestamp));
        _startupBenchmark = new StartupBenchmarkRequest(resultPath, processStartTimestamp);
    }

    internal static void TryConfigureStartupBenchmark(Views.ViewerWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var request = Interlocked.Exchange(ref _startupBenchmark, null);
        if (request is null)
            return;
        window.ConfigureStartupBench(
            request.ResultPath,
            request.ProcessStartTimestamp,
            CompleteStartupBenchmarkAsync);
    }

    internal static void TryConfigureRecoverySmoke(Views.ViewerWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (!Program.IsRecoverySmoke
            || Interlocked.Exchange(ref _recoverySmokeConfigured, 1) != 0)
        {
            return;
        }

        var resultPath = Program.RecoverySmokeResultPath
            ?? throw new InvalidOperationException("The recovery smoke output is unavailable.");
        if (Program.IsRecoverySmokeVerify)
            window.ConfigureRecoverySmokeVerification(resultPath);
        else
            window.ConfigureRecoverySmokeSeed(
                Program.RecoverySmokeInputPath
                    ?? throw new InvalidOperationException("The recovery smoke input is unavailable."),
                resultPath);
    }

    private static async Task CompleteStartupBenchmarkAsync()
    {
        ShutdownCapture();
        try
        {
            if (RecoveryEnabled)
                await Recovery.CompleteAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        try
        {
            await RecentFiles.DrainAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        await Logs.DrainAsync();
        try
        {
            Program.ReleaseInstanceKey();
        }
        catch
        {
        }

        var benchmarkRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(), "ezyImageViewer-startup-bench"));
        var dataRoot = Path.GetFullPath(DataPaths.RootDirectory);
        var relative = Path.GetRelativePath(benchmarkRoot, dataRoot);
        if (relative != "."
            && !relative.StartsWith("..", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative)
            && Directory.Exists(dataRoot))
        {
            try
            {
                Directory.Delete(dataRoot, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void EnsureCapture(AppSettings settings)
    {
        if (IsSafeMode || Capture is not null)
            return;
        var clipboard = new WinRtClipboardBackend();
        Capture = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => Windows?.Peek(),
            IsTargetLive = target =>
                target is Views.ViewerWindow window && Windows?.Contains(window) == true,
            ReadClipboardAsync = ct => clipboard.TryGetImageAsync(
                CaptureCoordinator.CaptureReadLimit, ct),
            HasInternalMarker = clipboard.CurrentContentHasInternalMarker,
            // Official path only with package identity (Q7=b); the dev loop stays legacy.
            LaunchOfficialCaptureAsync = PackageIdentity.HasIdentity
                ? CaptureLauncher.LaunchOfficialAsync
                : null,
            RedeemTokenAsync = (token, ct) => CaptureTokenReader.RedeemAsync(
                token, CaptureCoordinator.CaptureReadLimit, ct),
            LaunchFallbackMessage = AppStrings.CaptureLaunchFailed,
            CaptureFailedMessage = AppStrings.CaptureFailed,
            Tray = new TrayIconStrings(
                "ezy Image Viewer", AppStrings.TrayWatchToggle,
                AppStrings.TrayCapture, AppStrings.TrayOpenWindow),
            TrayIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ezyImageViewer.ico"),
            InitialWatchEnabled = settings.ClipboardWatchEnabled,
            RegisterHotkey = true,
            HotkeyModifiers = (uint)settings.CaptureHotkey.Modifiers,
            HotkeyVirtualKey = (uint)settings.CaptureHotkey.VirtualKey,
        }, listen: true);
    }

    private static string GetApplicationVersion()
    {
        return GetApplicationVersionNumber().ToString();
    }

    private static Version GetApplicationVersionNumber()
    {
        if (PackageIdentity.HasIdentity)
        {
            var version = global::Windows.ApplicationModel.Package.Current.Id.Version;
            return new Version(version.Major, version.Minor, version.Build, version.Revision);
        }
        return typeof(AppServices).Assembly.GetName().Version
            ?? new Version(0, 0, 0, 0);
    }

    internal static DocumentLoader CreateDocumentLoader(InputLimits? limits = null) =>
        new(limits ?? Limits);

    /// <summary>Explicit packaged-smoke seam. Normal product loaders remain disabled until the
    /// ADR-0006 corpus and security activation gates pass.</summary>
    internal static DocumentLoader CreateIsolatedCodecSmokeLoader(InputLimits? limits = null)
    {
        var codecHost = CodecHostDependencyResolver.TryResolve()
            ?? throw new CodecUnavailableException("The packaged CodecHost dependency is unavailable.");
        return new DocumentLoader(limits ?? Limits, codecHost);
    }
}
