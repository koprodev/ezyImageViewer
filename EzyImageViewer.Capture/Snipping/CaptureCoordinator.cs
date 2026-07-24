using EzyImageViewer.Capture.Clipboard;
using EzyImageViewer.Core.Input;

namespace EzyImageViewer.Capture.Snipping;

/// <summary>캡처 조정자가 창에 요구하는 최소 계약.</summary>
public interface ICaptureTarget
{
    void OpenClipboardBytes(ReadOnlyMemory<byte> bytes, string format);
    void Activate();
    void ShowCaptureNotice(ClipboardImagePayload payload);
    void ShowTransientStatus(string text);

    /// <summary>캡처 전에 창을 숨김. 모든 완료 경로는 <see cref="Activate"/>로 복원.</summary>
    void PrepareForCapture();
}

/// <summary>실앱 경계와 테스트 대역을 잇는 주입 옵션.</summary>
public sealed record CaptureCoordinatorOptions
{
    public required Func<ICaptureTarget?> ResolveTarget { get; init; }

    /// <summary>이미 닫힌 대상은 미련 없이 건너뜀.</summary>
    public Func<ICaptureTarget, bool> IsTargetLive { get; init; } = _ => true;

    public Func<CancellationToken, Task<ClipboardImagePayload?>>? ReadClipboardAsync { get; init; }
    public Func<bool> HasInternalMarker { get; init; } = () => false;
    public Func<Task<bool>> LaunchCaptureAsync { get; init; } = CaptureLauncher.LaunchSnippingAsync;

    /// <summary>패키지 identity용 공식 캡처 경로. null이면 클립보드 대기 경로 사용.</summary>
    public Func<string, Task<bool>>? LaunchOfficialCaptureAsync { get; init; }

    /// <summary>콜백 토큰을 읽기 상한 안에서 실제 데이터로 교환.</summary>
    public Func<string, CancellationToken, Task<ClipboardImagePayload?>>? RedeemTokenAsync { get; init; }

    /// <summary>오버레이 실행 실패 시 Win+Shift+S 경로를 안내하는 문구.</summary>
    public string LaunchFallbackMessage { get; init; } = "";

    /// <summary>공식 캡처 오류·토큰 교환 실패 문구. 사용자 취소는 조용히 넘김.</summary>
    public string CaptureFailedMessage { get; init; } = "";

    /// <summary>마감·중복 억제 경계를 시험할 수 있는 시계.</summary>
    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;

    /// <summary>콜백 마감과 복원 감시를 시험하는 취소 가능 타이머.</summary>
    public Func<TimeSpan, CancellationToken, Task> Delay { get; init; } = Task.Delay;

    public bool InitialWatchEnabled { get; init; } = true;
    public bool RegisterHotkey { get; init; }
    public uint HotkeyModifiers { get; init; } =
        ClipboardWatcher.ModControl | ClipboardWatcher.ModShift;
    public uint HotkeyVirtualKey { get; init; } = 0x45;

    /// <summary>잘못 저장된 키 값은 등록도, 몰래 바꿔치기도 금지.</summary>
    public bool IsHotkeyValid => CaptureHotkeyPolicy.IsSupportedChord(
        HotkeyModifiers,
        HotkeyVirtualKey);
}

/// <summary>
/// 클립보드 감시·전역 단축키·중복 억제를 묶는 캡처 허브.
/// 메시지 수신부터 해제까지 UI 스레드에서 처리.
/// </summary>
public sealed class CaptureCoordinator : IDisposable
{
    /// <summary>수동 감시가 거대 클립보드를 복제하지 못하게 둔 캡처 전용 상한.</summary>
    public const long CaptureReadLimit = 64L * 1024 * 1024;

    private readonly CaptureCoordinatorOptions _options;
    private readonly ClipboardWatcher? _watcher;
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

    /// <summary>막 연 캡처와 바이트가 같은 재게시만 잠깐 숨김.</summary>
    public static readonly TimeSpan PassiveSettleWindow = TimeSpan.FromSeconds(5);

    /// <summary>결과 없이 끝난 요청을 닫고 최소화된 창을 되살리는 감시 시간.</summary>
    public static readonly TimeSpan RequestWatchdog = CaptureFlow.ArmWindow + TimeSpan.FromSeconds(1);

    /// <summary>콜백이 원자적으로 점유하는 요청 상태. 느린 응답이 새 요청을 먹지 못함.</summary>
    private sealed class OfficialRequest
    {
        public required string CorrelationId { get; init; }
        public required DateTimeOffset Deadline { get; init; }
        public WeakReference<ICaptureTarget>? Origin { get; init; }
    }

    private DateTimeOffset Now => _options.Clock();

    public bool WatchEnabled => _flow.WatchEnabled;

    /// <summary>키 조합이 잘못됐거나 남이 선점했으면 false.</summary>
    public bool HotkeyRegistered { get; private set; }

    /// <summary>listen=false면 시스템 접점 없이 정책만 실행.</summary>
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
    }

    /// <summary>대기하지 않는 감시 콜백의 오류를 회수. 취소는 오류 취급 안 함.</summary>
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

    /// <summary>
    /// 캡처 오버레이 실행. 공식 경로는 상관관계 ID, 구형 경로는 클립보드 대기로 추적.
    /// 실패하면 Win+Shift+S 자동 열기로 강등하고 최신 요청만 안내.
    /// </summary>
    public async Task RequestCaptureAsync(ICaptureTarget? origin)
    {
        var target = origin ?? LiveResolve();
        var generation = ++_armGeneration;
        target?.PrepareForCapture(); // 오버레이에 우리 창이 찍히면 꽤 민망함.
        bool launched;
        if (_options.LaunchOfficialCaptureAsync is { } official)
        {
            var request = new OfficialRequest
            {
                CorrelationId = Guid.NewGuid().ToString(),
                Deadline = Now + CaptureFlow.ArmWindow,
                Origin = target is null ? null : new WeakReference<ICaptureTarget>(target),
            };
            _officialRequest = request; // 이전 콜백은 이제 짝이 없음.
            launched = await official(request.CorrelationId).ConfigureAwait(true);
            if (launched)
                _ = GuardAsync(() => ExpireOfficialAsync(request));
            else if (ReferenceEquals(_officialRequest, request) && !_disposed)
                _officialRequest = null; // 아래 클립보드 경로로 강등.
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
            // 최신 요청만 자동 열기 대기. 안내가 보이도록 창도 복원.
            _armedTarget = target is null ? null : new WeakReference<ICaptureTarget>(target);
            _flow.Arm(Now);
            target?.Activate();
            target?.ShowTransientStatus(_options.LaunchFallbackMessage);
        }
    }

    /// <summary>결과 없는 공식 요청만 끝내고 원래 창을 복원.</summary>
    private async Task ExpireOfficialAsync(OfficialRequest request)
    {
        await _options.Delay(RequestWatchdog, _cts.Token).ConfigureAwait(true);
        if (_disposed || !ReferenceEquals(_officialRequest, request))
            return;
        _officialRequest = null;
        (TakeLive(request.Origin) ?? LiveResolve())?.Activate();
    }

    /// <summary>소비되지 않은 구형 요청만 만료 후 복원. 포커스 도둑질 금지.</summary>
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

    /// <summary>방금 내부 복사한 데이터 기록.</summary>
    public void NoteInternalCopy(ReadOnlySpan<byte> pngBytes) =>
        _gate.NoteInternalCopy(pngBytes, Now);

    public bool ToggleWatchEnabled()
        => SetWatchEnabled(!_flow.WatchEnabled);

    public bool SetWatchEnabled(bool enabled)
    {
        if (_flow.WatchEnabled == enabled)
            return enabled;
        _flow.WatchEnabled = enabled;
        return enabled;
    }

    /// <summary>저장된 단축키 적용. 실패하면 기존 등록을 그대로 둠.</summary>
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
    /// 병합 루프 한 회당 한 번 읽음. 읽는 중 온 갱신은 다음 회차로 넘겨 유실 방지.
    /// 해제 취소가 오면 해당 회차는 버림.
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
                    continue; // 경합·비정상·초과 데이터는 캡처로 보지 않음.
                }
                if (_disposed)
                    return; // 해제 뒤 도착한 결과는 누구도 건드리지 않음.
                if (payload is not null)
                    HandlePayload(payload, hasMarker);
            } while (_updatePending && !_disposed);
        }
        finally
        {
            _handlingUpdate = false;
        }
    }

    /// <summary>감시 끄기는 읽기 전부터 적용되는 개인정보 경계.</summary>
    private bool ShouldReadClipboard()
    {
        var now = Now;
        return _flow.WatchEnabled
            || _flow.IsArmed(now)
            || _officialRequest is { } request && now <= request.Deadline;
    }

    /// <summary>실제 감시와 무인 실행이 함께 쓰는 정책 진입점.</summary>
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
                target.Activate(); // 사용자가 부른 결과니 앞으로 모심.
                break;
            }
            case CaptureDecision.Notify:
            {
                // 공식 요청 중 첫 이미지가 승자. 늦은 콜백은 짝이 없어 이중 열기 불가.
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
                    // 결과 없이 만료됐으니 다시 수동 감시 데이터로 취급.
                }
                // 막 연 캡처와 바이트가 같을 때만 숨김.
                if (now < _settleUntil && IsSettleEcho(payload.Bytes))
                    break;
                // 요청 없는 캡처는 제안만. 세션 납치는 사절.
                LiveResolve()?.ShowCaptureNotice(payload);
                break;
            }
        }
    }

    private bool IsSettleEcho(byte[] bytes) =>
        _lastCaptureBytes is not null && bytes.AsSpan().SequenceEqual(_lastCaptureBytes);

    /// <summary>프로토콜 활성화 진입점. 오류는 상태바로 보내고 앱은 살림.</summary>
    public void OnProtocolResponse(Uri uri, bool coldStart) =>
        _ = GuardAsync(() => HandleProtocolResponseAsync(uri, coldStart));

    /// <summary>
    /// 공식 경로 완료 처리. 기한 안에 상관관계가 맞는 콜백만 요청을 점유.
    /// 실제 최초 실행만 요청 상태 없는 성공을 허용하며 일회용 토큰은 재시도 안 함.
    /// </summary>
    public async Task HandleProtocolResponseAsync(Uri uri, bool coldStart = false)
    {
        if (_disposed || !SnipProtocol.TryParseResponse(uri, out var response))
            return;
        var request = _officialRequest;
        var matches = request is not null && Now <= request.Deadline
            && string.Equals(response.CorrelationId, request.CorrelationId, StringComparison.OrdinalIgnoreCase);
        if (matches)
            _officialRequest = null; // 점유 완료. 같은 URI 재배송은 탈락.
        else if (coldStart)
            request = null;
        else
            return;

        if (response.Code != SnipProtocol.CodeSuccess)
        {
            // 요청이 끝났으니 촬영 전에 숨긴 창 복원.
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
        // 점유한 요청의 원래 창만 사용. 전역 대기 대상은 최신 요청 몫.
        var target = TakeLive(request?.Origin) ?? LiveResolve();
        if (target is null)
            return;
        target.OpenClipboardBytes(payload.Bytes, payload.Format);
        target.Activate(); // 사용자가 부른 결과니 앞으로 모심.
    }

    /// <summary>실제 오버레이 없이 대기 상태만 설정. 무인 실행 전용.</summary>
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
        // 취소만 수행. 진행 중인 읽기는 토큰을 보고 멈추고 늦은 결과는 후검사에서 폐기.
        _cts.Cancel();
        _watcher?.Dispose();
    }
}
