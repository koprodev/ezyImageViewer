using EzyImageViewer.App.Views;
using EzyImageViewer.Core.Activation;
using EzyImageViewer.Infrastructure;
using Microsoft.UI.Dispatching;

namespace EzyImageViewer.App;

/// <summary>
/// Owns viewer windows (FR-APP-006/007 groundwork): single process, N windows.
/// Route runs on the UI thread and must stay non-blocking — it kicks session loads and returns.
/// </summary>
public sealed class WindowManager(DispatcherQueue dispatcherQueue)
{
    private readonly List<ViewerWindow> _windows = [];
    private readonly HashSet<ViewerWindow> _closingWindows = [];
    private readonly SemaphoreSlim _closeGate = new(1, 1);
    private ViewerWindow? _lastActive;
    private bool _sessionCompletionStarted;

    public DispatcherQueue DispatcherQueue { get; } = dispatcherQueue;

    /// <summary>Capture routing target: the last window the user touched, without activating
    /// anything (a passive notification must never steal focus). Null when no window exists.</summary>
    public ViewerWindow? Peek() =>
        (_lastActive is not null && !_closingWindows.Contains(_lastActive)
            ? _lastActive
            : null)
        ?? _windows.FirstOrDefault(window => !_closingWindows.Contains(window));

    /// <summary>Live-window check for capture routing — a closed window is never a target.</summary>
    public bool Contains(ViewerWindow window) =>
        _windows.Contains(window) && !_closingWindows.Contains(window);

    public void Route(ActivationRequest request)
    {
        if (_sessionCompletionStarted)
            return;
        switch (request)
        {
            case FileActivation { Target: OpenTarget.NewWindow } file:
            {
                var window = OpenNewWindow();
                if (file.IsInitial)
                    window.TrackStartupHealthUntilSessionSettles();
                window.OpenFiles(file.Paths);
                break;
            }
            case FileActivation file:
            {
                var window = EnsurePrimary();
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
                // Snipping Tool redirect callback (FR-CAP-001/002): a window must exist before
                // the coordinator resolves its open target (cold-start responses).
                EnsurePrimary().MarkStartupHealthyAfterFirstFrame();
                AppServices.Capture?.OnProtocolResponse(protocol.Uri, protocol.IsInitial);
                break;
            default:
                EnsurePrimary().MarkStartupHealthyAfterFirstFrame();
                break;
        }
    }

    public ViewerWindow EnsurePrimary()
    {
        var window = _windows.FirstOrDefault(candidate => !_closingWindows.Contains(candidate));
        if (window is null)
        {
            if (_sessionCompletionStarted)
                throw new InvalidOperationException("Application shutdown has started.");
            window = OpenNewWindow();
        }
        window.Activate();
        return window;
    }

    public ViewerWindow OpenNewWindow()
    {
        if (_sessionCompletionStarted)
            throw new InvalidOperationException("Application shutdown has started.");
        var window = new ViewerWindow();
        window.ApplySettings(AppServices.RuntimeSettings);
        _windows.Add(window);
        window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated)
                _lastActive = window;
        };
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            _closingWindows.Remove(window);
            if (_lastActive == window)
                _lastActive = null;
            // The tray icon and clipboard listener must not outlive the last window.
            if (_windows.Count == 0)
                AppServices.ShutdownCapture();
        };
        AppServices.TryConfigureRecoverySmoke(window);
        AppServices.TryConfigureStartupBenchmark(window);
        window.Activate();
        return window;
    }

    /// <summary>Drains recovery and privacy-sensitive background stores before a window closes.
    /// Concurrent close requests are serialized so exactly one request completes the session.</summary>
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
                        // The coordinator already logged the failure. Its crash marker stays on
                        // disk, so closing is safer than leaving a terminal session editing live.
                    }
                    await AppServices.RecentFiles.DrainAsync();
                    Program.MarkStartupHealthy();
                    _ = AppServices.Logs.TryEnqueue(
                        LocalLogLevel.Information,
                        new StructuredLogEvent { Name = StructuredLogEventNames.AppStopped });
                    await AppServices.Logs.DrainAsync();
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
                        // Preserve the marker/checkpoint for the next launch when deletion fails.
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
