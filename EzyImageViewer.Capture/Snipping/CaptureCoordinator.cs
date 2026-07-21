using EzyImageViewer.Capture.Clipboard;
using EzyImageViewer.Core.Input;

namespace EzyImageViewer.Capture.Snipping;

/// <summary>What the coordinator needs from a viewer window; the app adapts its window type.</summary>
public interface ICaptureTarget
{
    void OpenClipboardBytes(ReadOnlyMemory<byte> bytes, string format);
    void Activate();
    void ShowCaptureNotice(ClipboardImagePayload payload);
    void ShowTransientStatus(string text);

    /// <summary>Get out of the shot before the overlay opens (minimize); every completion path
    /// restores via <see cref="Activate"/>.</summary>
    void PrepareForCapture();
}

/// <summary>Injection seams ([21차] 보완 2): the real app wires the WinRT clipboard and launcher;
/// tests drive races with controlled fakes; unattended runs go policy-only.</summary>
public sealed record CaptureCoordinatorOptions
{
    public required Func<ICaptureTarget?> ResolveTarget { get; init; }

    /// <summary>A resolved or armed target that is no longer live (window closed) is skipped.</summary>
    public Func<ICaptureTarget, bool> IsTargetLive { get; init; } = _ => true;

    public Func<CancellationToken, Task<ClipboardImagePayload?>>? ReadClipboardAsync { get; init; }
    public Func<bool> HasInternalMarker { get; init; } = () => false;
    public Func<Task<bool>> LaunchCaptureAsync { get; init; } = CaptureLauncher.LaunchSnippingAsync;

    /// <summary>Official Snipping Tool path (packaged identity only, Q7=b): launches with a
    /// redirect-uri carrying the given correlation id. Null = legacy clipboard-armed path.</summary>
    public Func<string, Task<bool>>? LaunchOfficialCaptureAsync { get; init; }

    /// <summary>Redeems a callback's file-access-token into payload bytes (≤ CaptureReadLimit).</summary>
    public Func<string, CancellationToken, Task<ClipboardImagePayload?>>? RedeemTokenAsync { get; init; }

    /// <summary>Shown when the overlay cannot launch — points at Win+Shift+S, whose result the
    /// armed pipeline still auto-opens (the fallback is a path, not a dead end).</summary>
    public string LaunchFallbackMessage { get; init; } = "";

    /// <summary>Shown when an official capture ends in an error code (400/408/500) or the token
    /// cannot be redeemed; a user cancel (499) stays silent.</summary>
    public string CaptureFailedMessage { get; init; } = "";

    /// <summary>Injectable time source so deadline/settle boundaries are testable ([25차] 보완 3).</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;

    /// <summary>Awaitable timer seam: the callback grace and the restore watchdogs are testable
    /// and cancel with the coordinator.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public TrayIconStrings? Tray { get; init; }
    public string TrayIconPath { get; init; } = "";
    public bool InitialWatchEnabled { get; init; } = true;
    public bool RegisterHotkey { get; init; }
    public uint HotkeyModifiers { get; init; } =
        ClipboardWatcher.ModControl | ClipboardWatcher.ModShift;
    public uint HotkeyVirtualKey { get; init; } = 0x45;

    /// <summary>Invalid persisted values must never reach RegisterHotKey or silently replace
    /// the user's requested binding with a different one.</summary>
    public bool IsHotkeyValid => CaptureHotkeyPolicy.IsSupportedChord(
        HotkeyModifiers,
        HotkeyVirtualKey);
}

/// <summary>
/// Capture integration hub (M7, FR-CAP-001~006): owns the clipboard watcher, the global hotkey,
/// the duplicate gate and the tray icon; decides through <see cref="CaptureFlow"/>; routes
/// results into capture targets. UI-thread-affine — watcher events arrive on the UI message
/// loop, and pump/dispose must stay on that thread.
/// </summary>
public sealed class CaptureCoordinator : IDisposable
{
    /// <summary>Capture-domain read budget ([21차] 보완 4): screenshots are megabytes, not the
    /// loader's 512MiB file ceiling — a passive watcher must never clone a huge clipboard.</summary>
    public const long CaptureReadLimit = 64L * 1024 * 1024;

    private readonly CaptureCoordinatorOptions _options;
    private readonly ClipboardWatcher? _watcher;
    private readonly TrayIcon? _tray;
    private readonly CaptureFlow _flow = new();
    private readonly ClipboardDuplicateGate _gate = new();
    private readonly CancellationTokenSource _cts = new();
    private WeakReference<ICaptureTarget>? _armedTarget;
    private long _armGeneration;
    private OfficialRequest? _officialRequest;
    private byte[]? _lastCaptureBytes;
    private DateTimeOffset _settleUntil = DateTimeOffset.MinValue;
    private bool _handlingUpdate;
    private bool _updatePending;
    private bool _disposed;

    /// <summary>Byte-identical re-posts of a capture that just opened are muted briefly: one
    /// copy can raise several WM_CLIPBOARDUPDATEs, and the token path's overlay may also copy
    /// the same capture to the clipboard. Only exact bytes are muted, only in this window.</summary>
    public static readonly TimeSpan PassiveSettleWindow = TimeSpan.FromSeconds(5);

    /// <summary>Watchdog for a request that produced neither a callback nor a clipboard image
    /// (e.g. Esc on a legacy host): ends it and restores the minimized window.</summary>
    public static readonly TimeSpan RequestWatchdog = CaptureFlow.ArmWindow + TimeSpan.FromSeconds(1);

    /// <summary>Immutable per-request state ([25차] 보완 1): the matching callback claims this
    /// context atomically, so a slow token redemption can never consume a newer request's
    /// target or arm, and a superseded request's callback finds no match.</summary>
    private sealed class OfficialRequest
    {
        public required string CorrelationId { get; init; }
        public required DateTimeOffset Deadline { get; init; }
        public WeakReference<ICaptureTarget>? Origin { get; init; }
    }

    private DateTimeOffset Now => _options.Clock();

    public bool WatchEnabled => _flow.WatchEnabled;

    /// <summary>FR-CAP-004: false when the configured binding is invalid or another app owns it.</summary>
    public bool HotkeyRegistered { get; private set; }

    /// <summary>listen=false builds a policy-only coordinator (no system listener, hotkey or
    /// tray) for tests and unattended runs; payloads then arrive through the public entries.</summary>
    public CaptureCoordinator(CaptureCoordinatorOptions options, bool listen)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _flow.WatchEnabled = options.InitialWatchEnabled;
        if (!listen)
            return;
        _watcher = new ClipboardWatcher();
        _watcher.ClipboardUpdated += () => _ = GuardAsync(PumpClipboardUpdateAsync);
        _watcher.HotkeyPressed += () => _ = GuardAsync(() => RequestCaptureAsync(null));
        if (options.RegisterHotkey && options.IsHotkeyValid)
            HotkeyRegistered = _watcher.TryRegisterHotkey(
                options.HotkeyModifiers, options.HotkeyVirtualKey);
        if (options.Tray is { } trayStrings)
        {
            _tray = new TrayIcon(trayStrings, options.TrayIconPath, _flow.WatchEnabled);
            _tray.WatchToggleRequested += () => ToggleWatchEnabled();
            _tray.CaptureRequested += () => _ = GuardAsync(() => RequestCaptureAsync(null));
            _tray.OpenRequested += () => _options.ResolveTarget()?.Activate();
        }
    }

    /// <summary>Watcher callbacks are fire-and-forget by shape; their faults must be observed
    /// (§5-1), never crash the app, and never surface a cancellation as an error.</summary>
    private async Task GuardAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LiveResolve()?.ShowTransientStatus(ex.Message);
        }
    }

    /// <summary>FR-CAP-001/002: open the snipping overlay. Official path (packaged, Q7=b): the
    /// result returns through the redirect-uri callback, so the clipboard flow is NOT armed —
    /// an in-flight correlation id tracks the request instead (and mutes passive notices for the
    /// overlay's own clipboard copy). Legacy path: arm the clipboard auto-open window. Either
    /// launch failure degrades to the armed-clipboard contract and points at Win+Shift+S.
    /// Only the newest request's failure may speak — a stale one stays silent.</summary>
    public async Task RequestCaptureAsync(ICaptureTarget? origin)
    {
        var target = origin ?? LiveResolve();
        var generation = ++_armGeneration;
        target?.PrepareForCapture(); // out of the shot before the overlay opens
        bool launched;
        if (_options.LaunchOfficialCaptureAsync is { } official)
        {
            var request = new OfficialRequest
            {
                CorrelationId = Guid.NewGuid().ToString(),
                Deadline = Now + CaptureFlow.ArmWindow,
                Origin = target is null ? null : new WeakReference<ICaptureTarget>(target),
            };
            _officialRequest = request; // supersedes any older request: its callback won't match
            launched = await official(request.CorrelationId).ConfigureAwait(true);
            if (launched)
                _ = GuardAsync(() => ExpireOfficialAsync(request));
            else if (ReferenceEquals(_officialRequest, request) && !_disposed)
                _officialRequest = null; // degrade to the clipboard contract below
        }
        else
        {
            _armedTarget = target is null ? null : new WeakReference<ICaptureTarget>(target);
            _flow.Arm(Now);
            launched = await _options.LaunchCaptureAsync().ConfigureAwait(true);
            if (launched)
                _ = GuardAsync(() => RestoreExpiredArmAsync(generation));
        }
        if (!launched && generation == _armGeneration && !_disposed)
        {
            // Win+Shift+S guidance stays an auto-open path for the newest request only; the
            // window comes back so the guidance is actually visible.
            _armedTarget = target is null ? null : new WeakReference<ICaptureTarget>(target);
            _flow.Arm(Now);
            target?.Activate();
            target?.ShowTransientStatus(_options.LaunchFallbackMessage);
        }
    }

    /// <summary>Ends an official request that produced neither a callback nor a clipboard image
    /// and restores the minimized origin. A claimed/superseded request is left alone.</summary>
    private async Task ExpireOfficialAsync(OfficialRequest request)
    {
        await _options.Delay(RequestWatchdog, _cts.Token).ConfigureAwait(true);
        if (_disposed || !ReferenceEquals(_officialRequest, request))
            return;
        _officialRequest = null;
        (TakeLive(request.Origin) ?? LiveResolve())?.Activate();
    }

    /// <summary>Restores the window when a legacy-path arm times out unconsumed; a consumed or
    /// superseded arm must not re-activate (focus steal).</summary>
    private async Task RestoreExpiredArmAsync(long generation)
    {
        await _options.Delay(RequestWatchdog, _cts.Token).ConfigureAwait(true);
        if (_disposed || generation != _armGeneration || !_flow.ArmExpiredUnconsumed(Now))
            return;
        _flow.Disarm();
        var armed = _armedTarget;
        _armedTarget = null;
        (TakeLive(armed) ?? LiveResolve())?.Activate();
    }

    /// <summary>FR-CAP-005: the copy path reports what it just published.</summary>
    public void NoteInternalCopy(ReadOnlySpan<byte> pngBytes) =>
        _gate.NoteInternalCopy(pngBytes, Now);

    public bool ToggleWatchEnabled()
        => SetWatchEnabled(!_flow.WatchEnabled);

    public bool SetWatchEnabled(bool enabled)
    {
        if (_flow.WatchEnabled == enabled)
            return enabled;
        _flow.WatchEnabled = enabled;
        _tray?.SetWatchEnabled(enabled);
        return enabled;
    }

    /// <summary>Applies a persisted hotkey at runtime. Invalid or unavailable chords leave the
    /// previous registration intact and return false.</summary>
    public bool TryChangeHotkey(uint modifiers, uint virtualKey)
    {
        if (_disposed || _watcher is null)
            return false;
        var candidate = _options with
        {
            HotkeyModifiers = modifiers,
            HotkeyVirtualKey = virtualKey,
        };
        if (!candidate.IsHotkeyValid)
            return false;
        var changed = _watcher.TryChangeHotkey(modifiers, virtualKey);
        HotkeyRegistered = _watcher.HotkeyRegistered;
        return changed;
    }

    /// <summary>
    /// Reads the clipboard once per turn of a coalescing loop: an update arriving while a read
    /// is in flight marks the pump pending and is served by the next turn — a second, different
    /// image is delayed, never lost ([21차] 보완 2). Cancellation (dispose) drops the turn.
    /// </summary>
    public async Task PumpClipboardUpdateAsync()
    {
        if (_disposed || _options.ReadClipboardAsync is null || !ShouldReadClipboard())
            return;
        if (_handlingUpdate)
        {
            _updatePending = true;
            return;
        }
        _handlingUpdate = true;
        try
        {
            do
            {
                _updatePending = false;
                if (!ShouldReadClipboard())
                    break;
                var hasMarker = _options.HasInternalMarker();
                ClipboardImagePayload? payload;
                try
                {
                    payload = await _options.ReadClipboardAsync(_cts.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                    or InvalidDataException or IOException)
                {
                    continue; // contended/hostile/oversized clipboard content is not a capture
                }
                if (_disposed)
                    return; // a result completing after dispose must not touch any target
                if (payload is not null)
                    HandlePayload(payload, hasMarker);
            } while (_updatePending && !_disposed);
        }
        finally
        {
            _handlingUpdate = false;
        }
    }

    /// <summary>Watching off is a privacy boundary, not only a post-read routing choice. The
    /// clipboard is probed only for passive watching or a live user-requested capture.</summary>
    private bool ShouldReadClipboard()
    {
        var now = Now;
        return _flow.WatchEnabled
            || _flow.IsArmed(now)
            || _officialRequest is { } request && now <= request.Deadline;
    }

    /// <summary>Policy entry — the pump above and unattended runs both land here.</summary>
    public void HandlePayload(ClipboardImagePayload payload, bool hasMarker)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (_disposed)
            return;
        var now = Now;
        var echo = _gate.IsInternalEcho(payload.Bytes, hasMarker, now);
        switch (_flow.OnClipboardImage(echo, now))
        {
            case CaptureDecision.AutoOpen:
            {
                var armed = _armedTarget;
                _armedTarget = null;
                var target = TakeLive(armed) ?? LiveResolve();
                if (target is null)
                    return;
                _lastCaptureBytes = payload.Bytes;
                _settleUntil = now + PassiveSettleWindow;
                target.OpenClipboardBytes(payload.Bytes, payload.Format);
                target.Activate(); // the user asked for this capture; bring the result forward
                break;
            }
            case CaptureDecision.Notify:
            {
                // The user asked for this capture (official request in flight): the first image
                // to arrive IS the result and opens instantly — legacy hosts never send the
                // callback, and on modern hosts the late callback finds no context, so there is
                // no double open. Same one-shot semantics as the legacy arm.
                if (_officialRequest is { } official)
                {
                    _officialRequest = null;
                    if (now <= official.Deadline)
                    {
                        var captureTarget = TakeLive(official.Origin) ?? LiveResolve();
                        if (captureTarget is null)
                            return;
                        _lastCaptureBytes = payload.Bytes;
                        _settleUntil = now + PassiveSettleWindow;
                        captureTarget.OpenClipboardBytes(payload.Bytes, payload.Format);
                        captureTarget.Activate();
                        break;
                    }
                    // Expired without any result; this payload is passive again.
                }
                // Settle: only a byte-identical copy of the just-opened capture is muted.
                if (now < _settleUntil && IsSettleEcho(payload.Bytes))
                    break;
                // Unsolicited (Win+Shift+S): offer, never hijack the session (FR-CAP-003).
                LiveResolve()?.ShowCaptureNotice(payload);
                break;
            }
        }
    }

    private bool IsSettleEcho(byte[] bytes) =>
        _lastCaptureBytes is not null && bytes.AsSpan().SequenceEqual(_lastCaptureBytes);

    /// <summary>Entry for redirect-uri protocol activations (fire-and-forget from the activation
    /// router); faults surface on the status bar, never crash (§5-1).</summary>
    public void OnProtocolResponse(Uri uri, bool coldStart) =>
        _ = GuardAsync(() => HandleProtocolResponseAsync(uri, coldStart));

    /// <summary>
    /// Official-path completion (FR-CAP-002, Q7=b). The callback must claim the in-flight
    /// request context (correlation match within the deadline) — warm unmatched, expired,
    /// superseded or duplicate callbacks never redeem a token or disturb newer state ([25차]
    /// 보완 1·2). Only a genuine cold start (this activation launched the process) may accept
    /// a success without a context: the user made that capture for this app. Tokens are
    /// one-shot, so redemption failures surface instead of retrying.
    /// </summary>
    public async Task HandleProtocolResponseAsync(Uri uri, bool coldStart = false)
    {
        if (_disposed || !SnipProtocol.TryParseResponse(uri, out var response))
            return;
        var request = _officialRequest;
        var matches = request is not null && Now <= request.Deadline
            && string.Equals(response.CorrelationId, request.CorrelationId, StringComparison.OrdinalIgnoreCase);
        if (matches)
            _officialRequest = null; // claimed: a duplicate delivery of the same URI won't match
        else if (coldStart)
            request = null;
        else
            return;

        if (response.Code != SnipProtocol.CodeSuccess)
        {
            // The request ended: bring back the window minimized for the shot.
            var ended = TakeLive(request?.Origin) ?? LiveResolve();
            ended?.Activate();
            if (response.Code != SnipProtocol.CodeUserCancelled)
                ended?.ShowTransientStatus(_options.CaptureFailedMessage);
            return;
        }
        if (_options.RedeemTokenAsync is null || string.IsNullOrEmpty(response.FileAccessToken))
        {
            var broken = TakeLive(request?.Origin) ?? LiveResolve();
            broken?.Activate();
            broken?.ShowTransientStatus(_options.CaptureFailedMessage);
            return;
        }

        ClipboardImagePayload? payload;
        try
        {
            payload = await _options.RedeemTokenAsync(response.FileAccessToken, _cts.Token)
                .ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
            or InvalidDataException or IOException)
        {
            if (!_disposed)
            {
                var failed = TakeLive(request?.Origin) ?? LiveResolve();
                failed?.Activate();
                failed?.ShowTransientStatus(_options.CaptureFailedMessage);
            }
            return;
        }
        if (_disposed)
            return;
        if (payload is null)
        {
            var over = TakeLive(request?.Origin) ?? LiveResolve();
            over?.Activate();
            over?.ShowTransientStatus(_options.CaptureFailedMessage);
            return;
        }
        _lastCaptureBytes = payload.Bytes;
        _settleUntil = Now + PassiveSettleWindow;
        // The claimed context's own origin — never the global armed target, which belongs to
        // whatever request is newest ([25차] 보완 1).
        var target = TakeLive(request?.Origin) ?? LiveResolve();
        if (target is null)
            return;
        target.OpenClipboardBytes(payload.Bytes, payload.Format);
        target.Activate(); // the user asked for this capture; bring the result forward
    }

    /// <summary>Arms without launching the real overlay — unattended runs only.</summary>
    public void ArmWithoutLaunch() => _flow.Arm(Now);

    private ICaptureTarget? LiveResolve()
    {
        var target = _options.ResolveTarget();
        return target is not null && _options.IsTargetLive(target) ? target : null;
    }

    private ICaptureTarget? TakeLive(WeakReference<ICaptureTarget>? reference) =>
        reference is not null && reference.TryGetTarget(out var target)
            && _options.IsTargetLive(target) ? target : null;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Cancel only (no CTS dispose): a WinRT read may still hold the token; captured tokens
        // observe the cancellation and the pump's post-await disposed check drops the result.
        _cts.Cancel();
        _tray?.Dispose();
        _watcher?.Dispose();
    }
}
