using System.Diagnostics;
using System.Runtime.InteropServices;
using EzyImageViewer.App.Activation;
using EzyImageViewer.Infrastructure;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace EzyImageViewer.App;

/// <summary>
/// 창 생성 전 단일 인스턴스를 결정하는 사용자 진입점.
/// 초기 인수 → 키 점유 → 활성화 구독 → 라우터 적재 → 앱 시작 순서.
/// 리디렉션은 작업자에서 COM 인식 대기하며 STA를 동기 차단하지 않음.
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
        StartupTimeline.Mark("mainEntry");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        StartupTimeline.Mark("comWrappers");

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

        // 시각 스파이크·스모크는 독립 실행. 시작 측정은 실제 콜드 스타트 경로 포함.
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
                    return decision.Value; // 리디렉션 성공 0, 실패는 0 아님.
                StartupTimeline.Mark("singleInstance");
            }

            // 무거운 서비스 초기화와 XAML 기동을 겹침. 첫 UI 접근은 남은 초기화를 기다리고
            // 실패는 동기 실행과 같은 형식 초기화 예외로 다시 노출.
            // 줌·팬 벤치는 서비스를 안 쓰므로 괜한 I/O 준비를 생략.
            if (diagnostic.Mode != DiagnosticLaunchMode.ZoomPanBenchmark)
            {
                _ = Task.Run(static () =>
                {
                    try
                    {
                        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
                            typeof(AppServices).TypeHandle);
                    }
                    catch
                    {
                        // CLR이 실패를 보관하고 UI 스레드 첫 접근 때 노출.
                    }
                });
            }

            // XAML이 뜨기 전에 언어를 못 박는다. WinUI는 첫 리소스 조회 때 자기 컨트롤
            // 문자열을 확정해서, 창이 생긴 뒤에 바꾸면 토글의 켬/끔만 OS 언어로 남는다.
            AppStrings.ApplyLanguage(StartupLanguagePreference.Read());

            Microsoft.UI.Xaml.Application.Start(p =>
            {
                _ = p;
                StartupTimeline.Mark("xamlRuntime");
                var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
                StartupTimeline.Mark("appCtor");
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
            // 충돌 기록기가 원래 처리되지 않은 예외를 덮으면 안 됨.
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
            // 시작 상태는 진단용. 멀쩡한 앱 기동을 막지 않음.
        }
    }

    /// <summary>null이면 주 인스턴스로 UI 시작, 아니면 종료 코드.</summary>
    private static int? DecideRedirection(string[] commandLineArgs)
    {
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        var keyInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        StartupTimeline.Mark("appInstance");

        if (keyInstance.IsCurrent)
        {
            _registeredInstance = keyInstance;
            keyInstance.Activated += OnRedirectedActivation;
            // 비패키지 최초 실행은 자체 명령줄 우선. 파일·프로토콜 데이터가 있으면 활성화값 사용.
            // initial=true는 캡처 콜백이 믿는 실제 콜드 스타트 표식.
            var initial = ActivationArgsConverter.Convert(activationArgs, initial: true);
            if (initial is Core.Activation.LaunchActivation)
                initial = ActivationArgsConverter.FromCommandLine(commandLineArgs, initial: true);
            // 이 스레드에서 서비스 초기화를 깨우지 않도록 직접 채널에 게시.
            ActivationChannel.Router.Post(initial);
            return null;
        }

        return RedirectActivationTo(activationArgs, keyInstance) ? 0 : 1;
    }

    /// <summary>세션 영구 정리 뒤 단일 인스턴스 키 해제.</summary>
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
        => ActivationChannel.Router.Post(ActivationArgsConverter.Convert(e));

    /// <summary>작업자에서 COM 인식 리디렉션 대기. false면 실패.</summary>
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
            // 늦은 프레임워크 핸들이 남아도 진단 폴더는 OS가 회수 가능.
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
