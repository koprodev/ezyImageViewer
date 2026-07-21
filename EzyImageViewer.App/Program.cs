using System.Diagnostics;
using System.Runtime.InteropServices;
using EzyImageViewer.App.Activation;
using EzyImageViewer.Infrastructure;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace EzyImageViewer.App;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN) so single-instancing is decided before any
/// window exists: capture initial args -> claim key -> subscribe Activated -> buffer into the
/// router -> Application.Start. Redirect waits on a worker thread with a COM-aware wait
/// (never a blocking .Wait() on the STA).
/// </summary>
public static class Program
{
    private const string InstanceKey = "EzyImageViewer.Main";
    private static AppInstance? _registeredInstance;
    private static int _startupTrackingActive;
    private static int _startupFailureRecorded;
    internal static long ProcessStartTimestamp { get; private set; }
    internal static bool IsStandaloneRun { get; private set; }
    internal static bool IsStartupBenchmark { get; private set; }
    internal static string? StartupBenchmarkDataRoot { get; private set; }
    internal static bool IsRecoverySmoke { get; private set; }
    internal static bool IsRecoverySmokeVerify { get; private set; }
    internal static string? RecoverySmokeInputPath { get; private set; }
    internal static string? RecoverySmokeResultPath { get; private set; }
    internal static string? RecoverySmokeDataRoot { get; private set; }
    internal static string? DiagnosticDataRoot { get; private set; }
    private static string? _ownedDiagnosticDataRoot;

    [STAThread]
    private static int Main(string[] args)
    {
        ProcessStartTimestamp = Stopwatch.GetTimestamp();
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (!DiagnosticLaunchArguments.TryParse(args, out var diagnostic))
            return 2;

        var recoverySeedPath = ArgumentValue(args, "--diagnostic-recovery-seed=");
        var recoveryVerify = diagnostic.Mode == DiagnosticLaunchMode.RecoveryVerify;
        if (diagnostic.Mode is DiagnosticLaunchMode.RecoverySeed
            or DiagnosticLaunchMode.RecoveryVerify)
        {
            if (!TryValidateRecoverySmokeRoot(
                    ArgumentValue(args, "--diagnostic-recovery-root="),
                    out var recoveryRoot))
            {
                return 2;
            }
            IsRecoverySmoke = true;
            IsRecoverySmokeVerify = recoveryVerify;
            RecoverySmokeInputPath = recoverySeedPath;
            RecoverySmokeResultPath = ArgumentValue(args, "--diagnostic-recovery-out=");
            RecoverySmokeDataRoot = recoveryRoot;
            DiagnosticDataRoot = recoveryRoot;
        }

        IsStartupBenchmark = diagnostic.Mode == DiagnosticLaunchMode.StartupBenchmark;
        if (IsStartupBenchmark)
        {
            StartupBenchmarkDataRoot = CreateOwnedDiagnosticDataRoot("startup-bench");
            DiagnosticDataRoot = StartupBenchmarkDataRoot;
        }
        else if (diagnostic.IsDiagnostic && DiagnosticDataRoot is null)
            DiagnosticDataRoot = CreateOwnedDiagnosticDataRoot("standalone");

        // Visual spikes and smoke runs are standalone. Startup measurement deliberately uses
        // the normal AppInstance path so it includes the production cold-start pipeline.
        var standalone = diagnostic.IsStandalone;
        IsStandaloneRun = standalone;
        if (!standalone && !IsStartupBenchmark)
        {
            Volatile.Write(ref _startupTrackingActive, 1);
            AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
            {
                if (eventArgs.ExceptionObject is Exception exception)
                    RecordUnhandledStartupFailure(exception);
            };
        }

        try
        {
            if (!standalone)
            {
                var decision = DecideRedirection(args);
                if (decision is not null)
                    return decision.Value; // redirected (0) or redirect failed (non-zero)
            }

            Microsoft.UI.Xaml.Application.Start(p =>
            {
                _ = p;
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
            return 0;
        }
        finally
        {
            CleanupOwnedDiagnosticDataRoot();
        }
    }

    internal static void RecordUnhandledStartupFailure(Exception exception)
    {
        if (Volatile.Read(ref _startupTrackingActive) == 0
            || Interlocked.Exchange(ref _startupFailureRecorded, 1) != 0)
            return;
        try
        {
            _ = new StartupHealthTracker(AppDataPaths.CreateDefault())
                .RecordUnhandledException(exception);
        }
        catch
        {
            // A crash recorder must never replace the original unhandled exception.
        }
    }

    internal static void MarkStartupHealthy()
    {
        if (Interlocked.Exchange(ref _startupTrackingActive, 0) == 0)
            return;
        try
        {
            new StartupHealthTracker(AppDataPaths.CreateDefault()).MarkHealthy();
        }
        catch
        {
            // Startup state is diagnostic and must not block a healthy application.
        }
    }

    /// <summary>Null = this process is primary and must start the UI; otherwise the exit code.</summary>
    private static int? DecideRedirection(string[] commandLineArgs)
    {
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        var keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);

        if (keyInstance.IsCurrent)
        {
            _registeredInstance = keyInstance;
            keyInstance.Activated += OnRedirectedActivation;
            // Prefer our own command line for the unpackaged initial launch; fall back to
            // activation payloads (file/protocol) when they carry data. initial=true marks a
            // genuine cold start — the capture callback gate relies on it ([25차] 보완 2).
            var initial = ActivationArgsConverter.Convert(activationArgs, initial: true);
            if (initial is Core.Activation.LaunchActivation)
                initial = ActivationArgsConverter.FromCommandLine(commandLineArgs, initial: true);
            AppServices.Router.Post(initial);
            return null;
        }

        return RedirectActivationTo(activationArgs, keyInstance) ? 0 : 1;
    }

    /// <summary>Releases the single-instance key after durable session cleanup. A launch racing
    /// the final log drain can then become a new primary instead of targeting terminal services.</summary>
    internal static void ReleaseInstanceKey()
    {
        var instance = Interlocked.Exchange(ref _registeredInstance, null);
        if (instance is null)
            return;
        try
        {
            instance.Activated -= OnRedirectedActivation;
            instance.UnregisterKey();
        }
        catch
        {
            instance.Activated += OnRedirectedActivation;
            _ = Interlocked.CompareExchange(ref _registeredInstance, instance, null);
            throw;
        }
    }

    private static void OnRedirectedActivation(object? sender, AppActivationArguments e)
        => AppServices.Router.Post(ActivationArgsConverter.Convert(e));

    /// <summary>Waits for the redirect on a worker thread with a COM-aware wait; false = redirect failed.</summary>
    private static bool RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance)
    {
        var redirectDone = CreateEvent(nint.Zero, bManualReset: true, bInitialState: false, lpName: null);
        if (redirectDone == nint.Zero)
            return false;

        try
        {
            Exception? redirectError = null;
            Task.Run(async () =>
            {
                try
                {
                    await keyInstance.RedirectActivationToAsync(args);
                }
                catch (Exception ex)
                {
                    redirectError = ex;
                }
                finally
                {
                    SetEvent(redirectDone);
                }
            });

            var hr = CoWaitForMultipleObjects(
                CwmoDefault, Infinite, 1, [redirectDone], out _);
            return hr == 0 && redirectError is null;
        }
        finally
        {
            CloseHandle(redirectDone);
        }
    }

    private static string? ArgumentValue(string[] args, string prefix) =>
        args.FirstOrDefault(value => value.StartsWith(prefix, StringComparison.Ordinal))?
            [prefix.Length..];

    private static string CreateOwnedDiagnosticDataRoot(string category)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ezyImageViewer-diagnostics",
            $"{category}-{Guid.NewGuid():N}");
        _ownedDiagnosticDataRoot = Path.GetFullPath(root);
        return _ownedDiagnosticDataRoot;
    }

    private static void CleanupOwnedDiagnosticDataRoot()
    {
        var root = Interlocked.Exchange(ref _ownedDiagnosticDataRoot, null);
        if (root is null)
            return;
        var baseRoot = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(), "ezyImageViewer-diagnostics"));
        var relative = Path.GetRelativePath(baseRoot, root);
        if (relative == "."
            || relative.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
            || !Directory.Exists(root))
        {
            return;
        }
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The OS can reclaim a diagnostic directory if a late framework handle remains.
        }
    }

    private static bool TryValidateRecoverySmokeRoot(string? value, out string fullPath)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
            return false;
        try
        {
            var allowedRoot = Path.GetFullPath(Path.Combine(
                Path.GetTempPath(),
                "ezyImageViewer-recovery-smoke"));
            var candidate = Path.GetFullPath(value);
            var relative = Path.GetRelativePath(allowedRoot, candidate);
            if (relative == "."
                || relative.StartsWith("..", StringComparison.Ordinal)
                || Path.IsPathRooted(relative))
            {
                return false;
            }
            fullPath = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
            or IOException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            return false;
        }
    }

    private const uint CwmoDefault = 0;
    private const uint Infinite = 0xFFFFFFFF;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CreateEvent(nint lpEventAttributes, bool bManualReset, bool bInitialState, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(nint hEvent);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("ole32.dll")]
    private static extern int CoWaitForMultipleObjects(uint dwFlags, uint dwTimeout, uint cHandles, nint[] pHandles, out uint dwIndex);
}
