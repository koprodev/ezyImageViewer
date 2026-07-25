using EzyImageViewer.App.Views;
using EzyImageViewer.Core.Activation;
using EzyImageViewer.Infrastructure;
using Microsoft.UI.Dispatching;

namespace EzyImageViewer.App;

/// <summary>단일 프로세스의 여러 보기 창 소유. 라우팅은 UI 스레드에서 시작만 하고 즉시 반환.</summary>
public sealed class WindowManager(DispatcherQueue dispatcherQueue)
{
    /// <summary>파일 창이 이미지 크기를 기다리는 최대 시간. 넘으면 기본 크기로 표시.</summary>
    private static readonly TimeSpan FirstPresentationDeadline = TimeSpan.FromMilliseconds(400);

    private readonly List<ViewerWindow> _windows = [];
    private readonly HashSet<ViewerWindow> _closingWindows = [];
    private readonly SemaphoreSlim _closeGate = new(1, 1);
    private ViewerWindow? _lastActive;
    private bool _sessionCompletionStarted;

    public DispatcherQueue DispatcherQueue { get; } = dispatcherQueue;

    /// <summary>캡처 대상은 마지막으로 사용한 표시 창. 수동 알림이 포커스를 훔치지 않게 활성화 안 함.</summary>
    public ViewerWindow? Peek() =>
        (_lastActive is not null && !_closingWindows.Contains(_lastActive)
            && !_lastActive.IsPresentationDeferred
            ? _lastActive
            : null)
        ?? _windows.FirstOrDefault(window =>
            !_closingWindows.Contains(window) && !window.IsPresentationDeferred);

    /// <summary>캡처 라우팅용 생존 창 확인. 닫힌 창은 대상 아님.</summary>
    public bool Contains(ViewerWindow window) =>
        _windows.Contains(window) && !_closingWindows.Contains(window);

    public void Route(ActivationRequest request)
    {
        if (_sessionCompletionStarted)
            return;
        switch (request)
        {
            // 파일용 새 창은 이미지 크기 확정 전까지 숨김. 이미 보인 창은 크기 유지.
            case FileActivation { Target: OpenTarget.NewWindow } file:
            {
                var window = OpenNewWindow(deferPresentation: true);
                if (file.IsInitial)
                    window.TrackStartupHealthUntilSessionSettles();
                window.OpenFiles(file.Paths);
                break;
            }
            case FileActivation file:
            {
                var window = FindLiveWindow();
                if (window is null)
                {
                    window = OpenNewWindow(deferPresentation: true);
                }
                else
                {
                    window.PresentNow();
                }
                if (file.IsInitial)
                    window.TrackStartupHealthUntilSessionSettles();
                window.OpenFiles(file.Paths);
                break;
            }
            case ClipboardImageActivation clipboard:
                EnsurePrimary().OpenClipboardBytes(clipboard.ImageBytes, clipboard.SourceFormat);
                break;
            case ProtocolActivation protocol
                when EzyImageViewer.Capture.Snipping.SnipProtocol.IsResponse(protocol.Uri):
                // 콜드 스타트 캡처 콜백은 조정자가 대상을 찾기 전에 창부터 생성.
                EnsurePrimary().MarkStartupHealthyAfterFirstFrame();
                AppServices.Capture?.OnProtocolResponse(protocol.Uri, protocol.IsInitial);
                break;
            default:
                EnsurePrimary().MarkStartupHealthyAfterFirstFrame();
                break;
        }
    }

    private ViewerWindow? FindLiveWindow() =>
        _windows.FirstOrDefault(candidate => !_closingWindows.Contains(candidate));

    /// <summary>즉시 창이 필요하면 대기 중 창도 현재 크기로 표시하고 자동 크기 포기.</summary>
    public ViewerWindow EnsurePrimary()
    {
        var window = FindLiveWindow();
        if (window is null)
        {
            if (_sessionCompletionStarted)
                throw new InvalidOperationException("Application shutdown has started.");
            window = OpenNewWindow();
        }
        window.PresentNow();
        return window;
    }

    public ViewerWindow OpenNewWindow(bool deferPresentation = false)
    {
        if (_sessionCompletionStarted)
            throw new InvalidOperationException("Application shutdown has started.");
        // 생성자가 같은 디스패처 회차에 런타임 설정 적용 완료.
        var window = new ViewerWindow();
        _windows.Add(window);
        window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated)
            {
                _lastActive = window;
                AppServices.TryStartUpdateCheck(window);
            }
        };
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            _closingWindows.Remove(window);
            if (_lastActive == window)
                _lastActive = null;
            // 마지막 창이 닫히면 클립보드 감시도 함께 퇴근한다.
            if (_windows.Count == 0)
                AppServices.ShutdownCapture();
        };
        AppServices.TryConfigureRecoverySmoke(window);
        AppServices.TryConfigureStartupBenchmark(window);
        if (deferPresentation)
            window.DeferFirstPresentation(FirstPresentationDeadline);
        else
        {
            window.SizeForEmptyPresentation();
            window.Activate();
        }
        return window;
    }

    /// <summary>창 종료 전 복구·민감 저장소 비우기. 동시 닫기는 직렬화.</summary>
    public async Task PrepareCloseAsync(ViewerWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        await _closeGate.WaitAsync();
        try
        {
            if (!_windows.Contains(window) || !_closingWindows.Add(window))
                return;

            var isLastWindow = false;
            try
            {
                isLastWindow = _windows.All(_closingWindows.Contains);
                if (isLastWindow)
                {
                    _sessionCompletionStarted = true;
                    try
                    {
                        await AppServices.PersistToolDefaultsAsync();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        _ = AppServices.Logs.TryEnqueue(
                            LocalLogLevel.Error,
                            new StructuredLogEvent
                            {
                                Name = StructuredLogEventNames.SettingsSaved,
                                ErrorCode = "tool_defaults_write_failed",
                            },
                            ex);
                    }
                    try
                    {
                        if (AppServices.RecoveryEnabled)
                            await AppServices.Recovery.CompleteAsync();
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 실패는 이미 기록했고 충돌 표식도 남음. 종료 불가 세션을 살려 두는 것보다 닫는 편이 안전.
                    }
                    await AppServices.RecentFiles.DrainAsync();
                    Program.MarkStartupHealthy();
                    _ = AppServices.Logs.TryEnqueue(
                        LocalLogLevel.Information,
                        new StructuredLogEvent { Name = StructuredLogEventNames.AppStopped });
                    await AppServices.Logs.DrainAsync();
                    AppServices.ShutdownUpdateCheck();
                    AppServices.ShutdownCapture();
                    Program.ReleaseInstanceKey();
                }
                else
                {
                    try
                    {
                        if (AppServices.RecoveryEnabled)
                            await AppServices.Recovery.StopWindowAsync(window.RecoveryWindowId);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // 삭제 실패면 다음 실행을 위해 표식·체크포인트 보존.
                    }
                }
            }
            catch
            {
                _closingWindows.Remove(window);
                if (isLastWindow)
                    _sessionCompletionStarted = false;
                throw;
            }
        }
        finally
        {
            _closeGate.Release();
        }
    }

    public void ApplySettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!DispatcherQueue.HasThreadAccess)
        {
            _ = DispatcherQueue.TryEnqueue(() => ApplySettings(settings));
            return;
        }
        foreach (var window in _windows)
            window.ApplySettings(settings);
    }
}
