using EzyImageViewer.Capture.Clipboard;
using EzyImageViewer.Capture.Snipping;
using Xunit;

namespace EzyImageViewer.Tests.Capture;

/// <summary>가짜 클립보드·실행기로 병합·해제·닫힌 대상·묵은 실패 경합 계약 검증.</summary>
public sealed class CaptureCoordinatorTests
{
    private sealed class FakeTarget : ICaptureTarget
    {
        public readonly List<string> Opened = [];
        public readonly List<ClipboardImagePayload> Notices = [];
        public readonly List<string> Statuses = [];
        public int Activations;
        public int Prepared;

        public void OpenClipboardBytes(ReadOnlyMemory<byte> bytes, string format) =>
            Opened.Add($"{bytes.Length}:{format}");

        public void Activate() => Activations++;

        public void ShowCaptureNotice(ClipboardImagePayload payload) => Notices.Add(payload);

        public void ShowTransientStatus(string text) => Statuses.Add(text);

        public void PrepareForCapture() => Prepared++;
    }

    /// <summary>테스트가 지시할 때만 요청 시간별 타이머를 깨우는 결정적 대역.</summary>
    private sealed class DelayHub
    {
        private readonly List<(TimeSpan Duration, TaskCompletionSource Source)> _pending = [];

        public Task Wait(TimeSpan duration, CancellationToken cancellationToken)
        {
            var source = new TaskCompletionSource();
            cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
            _pending.Add((duration, source));
            return source.Task;
        }

        public void Fire(TimeSpan duration)
        {
            foreach (var (_, source) in _pending.Where(p => p.Duration == duration).ToList())
                source.TrySetResult();
        }
    }

    private static ClipboardImagePayload Payload(byte marker) =>
        new([marker, 1, 2, 3], ClipboardImagePayload.Png);

    [Fact]
    public void InitialWatchDisabled_IsAppliedToThePolicyCoordinator()
    {
        var target = new FakeTarget();
        using var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => target,
            InitialWatchEnabled = false,
        }, listen: false);

        Assert.False(coordinator.WatchEnabled);
        coordinator.HandlePayload(Payload(42), hasMarker: false);
        Assert.Empty(target.Notices);
    }

    [Fact]
    public void SetWatchEnabled_IsExplicitAndIdempotent()
    {
        using var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => null,
        }, listen: false);

        Assert.False(coordinator.SetWatchEnabled(false));
        Assert.False(coordinator.SetWatchEnabled(false));
        Assert.True(coordinator.SetWatchEnabled(true));
        Assert.True(coordinator.SetWatchEnabled(true));
        Assert.False(coordinator.ToggleWatchEnabled());
    }

    [Fact]
    public void HotkeyConfiguration_PreservesTheDefaultAndAcceptsConfiguredValues()
    {
        var defaults = new CaptureCoordinatorOptions { ResolveTarget = () => null };
        Assert.Equal(ClipboardWatcher.ModControl | ClipboardWatcher.ModShift,
            defaults.HotkeyModifiers);
        Assert.Equal(0x45U, defaults.HotkeyVirtualKey);
        Assert.True(defaults.IsHotkeyValid);

        var configured = defaults with
        {
            HotkeyModifiers = ClipboardWatcher.ModAlt | ClipboardWatcher.ModShift,
            HotkeyVirtualKey = 0x7B,
        };
        Assert.Equal(ClipboardWatcher.ModAlt | ClipboardWatcher.ModShift,
            configured.HotkeyModifiers);
        Assert.Equal(0x7BU, configured.HotkeyVirtualKey);
        Assert.True(configured.IsHotkeyValid);
    }

    [Theory]
    [InlineData(0U, 0x45U)]
    [InlineData(0x10U, 0x45U)]
    [InlineData(ClipboardWatcher.ModControl, 0U)]
    [InlineData(ClipboardWatcher.ModControl, 0x01U)]
    [InlineData(ClipboardWatcher.ModControl, 0x20U)]
    [InlineData(ClipboardWatcher.ModControl, 0x3AU)]
    [InlineData(ClipboardWatcher.ModControl, 0x5BU)]
    [InlineData(ClipboardWatcher.ModControl, 0x6FU)]
    [InlineData(ClipboardWatcher.ModControl, 0x88U)]
    [InlineData(ClipboardWatcher.ModControl, 0xFFU)]
    [InlineData(ClipboardWatcher.ModControl, 0x100U)]
    public void HotkeyConfiguration_InvalidValuesFailClosed(uint modifiers, uint virtualKey)
    {
        var options = new CaptureCoordinatorOptions
        {
            ResolveTarget = () => null,
            RegisterHotkey = true,
            HotkeyModifiers = modifiers,
            HotkeyVirtualKey = virtualKey,
        };

        Assert.False(options.IsHotkeyValid);
    }

    [Fact]
    public async Task SecondUpdate_DuringARead_IsCoalescedNotLost()
    {
        var target = new FakeTarget();
        var reads = new Queue<TaskCompletionSource<ClipboardImagePayload?>>();
        var issued = new List<TaskCompletionSource<ClipboardImagePayload?>>();
        using var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => target,
            ReadClipboardAsync = _ =>
            {
                var source = new TaskCompletionSource<ClipboardImagePayload?>();
                issued.Add(source);
                reads.Enqueue(source);
                return source.Task;
            },
        }, listen: false);

        var first = coordinator.PumpClipboardUpdateAsync();
        var second = coordinator.PumpClipboardUpdateAsync(); // 첫 읽기 중 도착.
        await second; // 대기 표시만 하고 즉시 반환.

        Assert.Single(issued);
        reads.Dequeue().SetResult(Payload(1)); // 첫 읽기 완료 후 다음 회차 시작.
        await Task.Yield();
        Assert.Equal(2, issued.Count);
        reads.Dequeue().SetResult(Payload(2));
        await first;

        // 서로 다른 두 이미지 모두 알림. 둘째도 줄에서 안 떨어짐.
        Assert.Equal(2, target.Notices.Count);
    }

    [Fact]
    public async Task ReadCompletingAfterDispose_TouchesNoTarget()
    {
        var target = new FakeTarget();
        var read = new TaskCompletionSource<ClipboardImagePayload?>();
        var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => target,
            ReadClipboardAsync = _ => read.Task,
        }, listen: false);

        var pump = coordinator.PumpClipboardUpdateAsync();
        coordinator.Dispose();
        read.SetResult(Payload(3));
        await pump;

        Assert.Empty(target.Notices);
        Assert.Empty(target.Opened);
    }

    [Fact]
    public async Task WatchDisabledWithoutARequest_DoesNotProbeTheClipboard()
    {
        var markerProbes = 0;
        var reads = 0;
        using var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => null,
            InitialWatchEnabled = false,
            HasInternalMarker = () => { markerProbes++; return false; },
            ReadClipboardAsync = _ =>
            {
                reads++;
                return Task.FromResult<ClipboardImagePayload?>(Payload(40));
            },
        }, listen: false);
        Assert.False(coordinator.WatchEnabled);

        await coordinator.PumpClipboardUpdateAsync();

        Assert.Equal(0, markerProbes);
        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task WatchDisabledStillReadsAnArmedCapture()
    {
        var target = new FakeTarget();
        var reads = 0;
        using var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => target,
            InitialWatchEnabled = false,
            ReadClipboardAsync = _ =>
            {
                reads++;
                return Task.FromResult<ClipboardImagePayload?>(Payload(41));
            },
        }, listen: false);
        Assert.False(coordinator.WatchEnabled);
        coordinator.ArmWithoutLaunch();

        await coordinator.PumpClipboardUpdateAsync();

        Assert.Equal(1, reads);
        Assert.Single(target.Opened);
    }

    [Fact]
    public async Task ClosedArmedTarget_FallsBackToTheLiveResolvedWindow()
    {
        var closed = new FakeTarget();
        var live = new FakeTarget();
        using var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => live,
            IsTargetLive = candidate => !ReferenceEquals(candidate, closed),
            LaunchCaptureAsync = () => Task.FromResult(true),
        }, listen: false);

        await coordinator.RequestCaptureAsync(closed); // 대기 뒤 닫히는 창.
        coordinator.HandlePayload(Payload(4), hasMarker: false);

        Assert.Empty(closed.Opened);
        Assert.Single(live.Opened);
        Assert.Equal(1, live.Activations);
    }

    [Fact]
    public async Task StaleLaunchFailure_StaysSilent_AndTheNewerArmStillOpens()
    {
        var target = new FakeTarget();
        var launches = new Queue<TaskCompletionSource<bool>>();
        using var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => target,
            LaunchCaptureAsync = () =>
            {
                var source = new TaskCompletionSource<bool>();
                launches.Enqueue(source);
                return source.Task;
            },
        }, listen: false);

        var request1 = coordinator.RequestCaptureAsync(target);
        var request2 = coordinator.RequestCaptureAsync(target);
        var launch1 = launches.Dequeue();
        var launch2 = launches.Dequeue();
        launch2.SetResult(true);
        launch1.SetResult(false); // 이전 요청이 교체 뒤 늦게 실패.
        await request1;
        await request2;

        Assert.Empty(target.Statuses); // 묵은 실패는 침묵.
        coordinator.HandlePayload(Payload(5), hasMarker: false);
        Assert.Single(target.Opened); // 새 대기는 그대로 자동 열기.
    }

    [Fact]
    public async Task LaunchFailure_KeepsTheArm_AndPointsAtTheFallback()
    {
        var target = new FakeTarget();
        using var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => target,
            LaunchCaptureAsync = () => Task.FromResult(false),
            LaunchFallbackMessage = "fallback",
        }, listen: false);

        await coordinator.RequestCaptureAsync(target);

        Assert.Equal(["fallback"], target.Statuses);
        // 사용자가 안내대로 Win+Shift+S를 쓰면 자동 열기 유지.
        coordinator.HandlePayload(Payload(6), hasMarker: false);
        Assert.Single(target.Opened);
    }

    private static Uri Response(string? correlationId, int code = 200, string? token = "tok") =>
        new($"{SnipProtocol.RedirectUri}?code={code}&reason=r"
            + (correlationId is null ? "" : $"&x-request-correlation-id={correlationId}")
            + (token is null ? "" : $"&file-access-token={token}"));

    private static (CaptureCoordinator Coordinator, FakeTarget Target, List<string> Launched,
        List<string> Redeemed) OfficialSetup(ClipboardImagePayload? redeemResult)
    {
        var target = new FakeTarget();
        var launched = new List<string>();
        var redeemed = new List<string>();
        var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => target,
            LaunchOfficialCaptureAsync = id =>
            {
                launched.Add(id);
                return Task.FromResult(true);
            },
            RedeemTokenAsync = (token, _) =>
            {
                redeemed.Add(token);
                return Task.FromResult(redeemResult);
            },
            CaptureFailedMessage = "failed",
        }, listen: false);
        return (coordinator, target, launched, redeemed);
    }

    [Fact]
    public async Task OfficialSuccess_RedeemsTheToken_AndOpensOnTheOriginWindow()
    {
        var (coordinator, target, launched, redeemed) = OfficialSetup(Payload(7));
        using var _ = coordinator;

        await coordinator.RequestCaptureAsync(target);
        await coordinator.HandleProtocolResponseAsync(Response(launched.Single()));

        Assert.Equal(["tok"], redeemed);
        Assert.Single(target.Opened);
        Assert.Equal(1, target.Activations);
        Assert.Empty(target.Statuses);
    }

    [Fact]
    public async Task StaleCorrelation_IsDropped_AndTheNewerRequestStillCompletes()
    {
        var (coordinator, target, launched, _) = OfficialSetup(Payload(8));
        using var _1 = coordinator;

        await coordinator.RequestCaptureAsync(target);
        await coordinator.RequestCaptureAsync(target); // 첫 요청 교체.
        await coordinator.HandleProtocolResponseAsync(Response(launched[0]));
        Assert.Empty(target.Opened); // 이전 콜백은 배달 금지.

        await coordinator.HandleProtocolResponseAsync(Response(launched[1]));
        Assert.Single(target.Opened);
    }

    [Fact]
    public async Task UserCancel499_StaysSilent_AndTheNextImageIsPassiveAgain()
    {
        var (coordinator, target, launched, redeemed) = OfficialSetup(Payload(9));
        using var _ = coordinator;

        await coordinator.RequestCaptureAsync(target);
        await coordinator.HandleProtocolResponseAsync(
            Response(launched.Single(), code: 499, token: null));

        Assert.Empty(target.Statuses);
        Assert.Empty(target.Opened);
        Assert.Empty(redeemed);
        // 요청 종료 뒤 클립보드 이미지는 수동 알림. 자동 열기 금지.
        coordinator.HandlePayload(Payload(10), hasMarker: false);
        Assert.Empty(target.Opened);
        Assert.Single(target.Notices);
    }

    [Fact]
    public async Task ErrorCode_SurfacesTheFailureMessage()
    {
        var (coordinator, target, launched, _) = OfficialSetup(Payload(11));
        using var _1 = coordinator;

        await coordinator.RequestCaptureAsync(target);
        await coordinator.HandleProtocolResponseAsync(
            Response(launched.Single(), code: 500, token: null));

        Assert.Equal(["failed"], target.Statuses);
        Assert.Empty(target.Opened);
    }

    [Fact]
    public async Task OfficialLaunchFailure_DegradesToTheArmedClipboardContract()
    {
        var target = new FakeTarget();
        using var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => target,
            LaunchOfficialCaptureAsync = _ => Task.FromResult(false),
            LaunchFallbackMessage = "fallback",
        }, listen: false);

        await coordinator.RequestCaptureAsync(target);

        Assert.Equal(["fallback"], target.Statuses);
        // Win+Shift+S 수동 캡처도 클립보드로 와 자동 열기.
        coordinator.HandlePayload(Payload(12), hasMarker: false);
        Assert.Single(target.Opened);
    }

    [Fact]
    public async Task ColdStartSuccess_WithoutAnInFlightRequest_StillOpens()
    {
        var (coordinator, target, _, redeemed) = OfficialSetup(Payload(16));
        using var _1 = coordinator;

        await coordinator.HandleProtocolResponseAsync(Response("unseen-correlation"), coldStart: true);

        Assert.Equal(["tok"], redeemed);
        Assert.Single(target.Opened);
    }

    [Fact]
    public async Task WarmCallback_WithoutAnInFlightRequest_NeverRedeems()
    {
        var (coordinator, target, _, redeemed) = OfficialSetup(Payload(17));
        using var _1 = coordinator;

        await coordinator.HandleProtocolResponseAsync(Response("forged-or-replayed"));

        Assert.Empty(redeemed);
        Assert.Empty(target.Opened);
        Assert.Empty(target.Statuses);
    }

    [Fact]
    public async Task ExpiredRequest_Callback_IsRejected()
    {
        var target = new FakeTarget();
        var launched = new List<string>();
        var redeemed = new List<string>();
        var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        using var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => target,
            LaunchOfficialCaptureAsync = id => { launched.Add(id); return Task.FromResult(true); },
            RedeemTokenAsync = (token, _) =>
            {
                redeemed.Add(token);
                return Task.FromResult<ClipboardImagePayload?>(Payload(18));
            },
            Clock = () => now,
        }, listen: false);

        await coordinator.RequestCaptureAsync(target);
        now += CaptureFlow.ArmWindow + TimeSpan.FromSeconds(1);
        await coordinator.HandleProtocolResponseAsync(Response(launched.Single()));

        Assert.Empty(redeemed);
        Assert.Empty(target.Opened);
    }

    [Fact]
    public async Task DuplicateDelivery_OfTheSameCallback_RedeemsOnlyOnce()
    {
        var (coordinator, target, launched, redeemed) = OfficialSetup(Payload(19));
        using var _1 = coordinator;

        await coordinator.RequestCaptureAsync(target);
        var callback = Response(launched.Single());
        await coordinator.HandleProtocolResponseAsync(callback);
        await coordinator.HandleProtocolResponseAsync(callback); // OS 재배송·재생.

        Assert.Single(redeemed);
        Assert.Single(target.Opened);
    }

    [Fact]
    public async Task SlowRedemption_NeverConsumesANewerRequestsTarget()
    {
        var targetA = new FakeTarget();
        var targetB = new FakeTarget();
        var launched = new List<string>();
        var redeems = new Queue<TaskCompletionSource<ClipboardImagePayload?>>();
        using var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => targetB,
            LaunchOfficialCaptureAsync = id => { launched.Add(id); return Task.FromResult(true); },
            RedeemTokenAsync = (_, _) =>
            {
                var source = new TaskCompletionSource<ClipboardImagePayload?>();
                redeems.Enqueue(source);
                return source.Task;
            },
        }, listen: false);

        await coordinator.RequestCaptureAsync(targetA);
        var callbackA = coordinator.HandleProtocolResponseAsync(Response(launched[0]));
        await coordinator.RequestCaptureAsync(targetB); // A 토큰 교환 중 B 요청.
        redeems.Dequeue().SetResult(Payload(21));
        await callbackA;

        // A는 자기 원래 창에서 완료. B 요청 상태는 소비·해제되지 않음.
        Assert.Single(targetA.Opened);
        Assert.Empty(targetB.Opened);

        var callbackB = coordinator.HandleProtocolResponseAsync(Response(launched[1]));
        redeems.Dequeue().SetResult(Payload(22));
        await callbackB;
        Assert.Single(targetB.Opened);
    }

    [Fact]
    public async Task StaleCallback_AfterAFallbackArm_DoesNotDisturbTheFallback()
    {
        var targetA = new FakeTarget();
        var targetB = new FakeTarget();
        var launched = new List<string>();
        var redeemed = new List<string>();
        using var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => targetB,
            LaunchOfficialCaptureAsync = id =>
            {
                launched.Add(id);
                return Task.FromResult(launched.Count == 1); // A 성공, B 실패.
            },
            RedeemTokenAsync = (token, _) =>
            {
                redeemed.Add(token);
                return Task.FromResult<ClipboardImagePayload?>(Payload(23));
            },
            LaunchFallbackMessage = "fallback",
        }, listen: false);

        await coordinator.RequestCaptureAsync(targetA);
        await coordinator.RequestCaptureAsync(targetB); // 실행 실패 후 클립보드 대기.

        await coordinator.HandleProtocolResponseAsync(Response(launched[0])); // 묵은 A 콜백.
        Assert.Empty(redeemed); // 교체됐으니 교환·콜드 스타트 취급 금지.

        // 사용자가 대체 경로를 쓰면 B의 클립보드 대기 계약은 살아 있음.
        coordinator.HandlePayload(Payload(24), hasMarker: false);
        Assert.Single(targetB.Opened);
        Assert.Empty(targetA.Opened);
    }

    [Fact]
    public async Task SuccessWithoutAToken_SurfacesTheFailure()
    {
        var (coordinator, target, launched, redeemed) = OfficialSetup(Payload(25));
        using var _1 = coordinator;

        await coordinator.RequestCaptureAsync(target);
        await coordinator.HandleProtocolResponseAsync(Response(launched.Single(), token: null));

        Assert.Empty(redeemed);
        Assert.Equal(["failed"], target.Statuses);
    }

    [Fact]
    public async Task RedemptionFailure_SurfacesTheFailure()
    {
        var target = new FakeTarget();
        var launched = new List<string>();
        using var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => target,
            LaunchOfficialCaptureAsync = id => { launched.Add(id); return Task.FromResult(true); },
            RedeemTokenAsync = (_, _) => throw new System.Runtime.InteropServices.COMException(
                "token already redeemed"),
            CaptureFailedMessage = "failed",
        }, listen: false);

        await coordinator.RequestCaptureAsync(target);
        await coordinator.HandleProtocolResponseAsync(Response(launched.Single()));

        Assert.Equal(["failed"], target.Statuses);
        Assert.Empty(target.Opened);
    }

    [Fact]
    public async Task CallbackFirst_ThenTheClipboardEcho_IsMutedBySettle()
    {
        var official = Payload(13);
        var (coordinator, target, launched, _) = OfficialSetup(official);
        using var _1 = coordinator;

        await coordinator.RequestCaptureAsync(target);
        await coordinator.HandleProtocolResponseAsync(Response(launched.Single()));
        Assert.Single(target.Opened);

        // 안정 구간은 막 연 캡처와 바이트가 같은 복사만 숨김.
        coordinator.HandlePayload(Payload(13), hasMarker: false);
        Assert.Empty(target.Notices);
        coordinator.HandlePayload(Payload(26), hasMarker: false);
        Assert.Single(target.Notices);
    }

    [Fact]
    public async Task InstantOpen_MutesDuplicateClipboardUpdates_OfTheSameCapture()
    {
        // 한 번 복사도 갱신 메시지가 여러 번 올 수 있어 같은 바이트 재게시 알림 차단.
        var (coordinator, target, _, _) = OfficialSetup(Payload(36));
        using var _1 = coordinator;

        await coordinator.RequestCaptureAsync(target);
        coordinator.HandlePayload(Payload(37), hasMarker: false);
        Assert.Single(target.Opened);

        coordinator.HandlePayload(Payload(37), hasMarker: false); // 중복 갱신.
        Assert.Empty(target.Notices);
        coordinator.HandlePayload(Payload(38), hasMarker: false); // 실제 새 이미지.
        Assert.Single(target.Notices);
    }

    [Fact]
    public void ArmedAutoOpen_MutesDuplicateClipboardUpdates_OfTheSameCapture()
    {
        var target = new FakeTarget();
        using var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => target,
        }, listen: false);

        coordinator.ArmWithoutLaunch();
        coordinator.HandlePayload(Payload(39), hasMarker: false);
        Assert.Single(target.Opened);

        coordinator.HandlePayload(Payload(39), hasMarker: false); // 중복 갱신.
        Assert.Empty(target.Notices);
    }

    [Fact]
    public async Task InFlightOpen_ConsumesTheRequest_SoALateFailureCallbackStaysSilent()
    {
        var (coordinator, target, launched, _) = OfficialSetup(Payload(27));
        using var _1 = coordinator;

        await coordinator.RequestCaptureAsync(target);
        coordinator.HandlePayload(Payload(28), hasMarker: false);
        Assert.Single(target.Opened); // 첫 이미지가 캡처라 즉시 열림.

        await coordinator.HandleProtocolResponseAsync(
            Response(launched.Single(), code: 500, token: null));

        Assert.Empty(target.Statuses); // 소비된 요청의 묵은 콜백은 침묵.
        Assert.Single(target.Opened);
    }

    private static (CaptureCoordinator Coordinator, FakeTarget Target, DelayHub Delays,
        List<string> Launched, List<string> Redeemed) OfficialSetupWithDelays(
            ClipboardImagePayload? redeemResult)
    {
        var target = new FakeTarget();
        var delays = new DelayHub();
        var launched = new List<string>();
        var redeemed = new List<string>();
        var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => target,
            LaunchOfficialCaptureAsync = id =>
            {
                launched.Add(id);
                return Task.FromResult(true);
            },
            RedeemTokenAsync = (token, _) =>
            {
                redeemed.Add(token);
                return Task.FromResult(redeemResult);
            },
            CaptureFailedMessage = "failed",
            Delay = delays.Wait,
        }, listen: false);
        return (coordinator, target, delays, launched, redeemed);
    }

    [Fact]
    public async Task OfficialInFlight_TheFirstClipboardImage_OpensInstantly()
    {
        // 신형 캡처 도구가 없으면 콜백 없이 클립보드만 도착. 첫 이미지가 요청 결과라 즉시 처리.
        var (coordinator, target, launched, redeemed) = OfficialSetup(Payload(30));
        using var _1 = coordinator;

        await coordinator.RequestCaptureAsync(target);
        Assert.Equal(1, target.Prepared); // 촬영에서 빠지도록 최소화.
        coordinator.HandlePayload(Payload(31), hasMarker: false);

        Assert.Single(target.Opened); // 원래 창에서 즉시 열기.
        Assert.Equal(1, target.Activations);
        Assert.Empty(target.Notices);

        // 늦거나 안 오는 콜백은 이중 열기·교환 금지.
        await coordinator.HandleProtocolResponseAsync(Response(launched.Single()));
        Assert.Empty(redeemed);
        Assert.Single(target.Opened);
    }

    [Fact]
    public async Task OfficialWatchdog_EndsASilentRequest_AndRestoresTheWindow()
    {
        var (coordinator, target, delays, launched, redeemed) = OfficialSetupWithDelays(Payload(34));
        using var _1 = coordinator;

        await coordinator.RequestCaptureAsync(target); // 구형 호스트에서 Esc, 결과 없음.
        delays.Fire(CaptureCoordinator.RequestWatchdog);
        await Task.Yield();

        Assert.Equal(1, target.Activations); // 캡처 최소화에서 복원.
        await coordinator.HandleProtocolResponseAsync(Response(launched.Single()));
        Assert.Empty(redeemed); // 만료 요청은 콜백 거절.
    }

    [Fact]
    public async Task LegacyWatchdog_RestoresOnlyAnUnconsumedArm()
    {
        var target = new FakeTarget();
        var delays = new DelayHub();
        using var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => target,
            LaunchCaptureAsync = () => Task.FromResult(true),
            Delay = delays.Wait,
        }, listen: false);

        await coordinator.RequestCaptureAsync(target);
        coordinator.HandlePayload(Payload(35), hasMarker: false); // 캡처가 대기 소비.
        Assert.Single(target.Opened);
        var activationsAfterOpen = target.Activations;

        delays.Fire(CaptureCoordinator.RequestWatchdog);
        await Task.Yield();
        Assert.Equal(activationsAfterOpen, target.Activations); // 소비 뒤 포커스 탈취 없음.
    }

    [Fact]
    public async Task LegacyWatchdog_RestoresTheWindow_WhenTheArmExpiresUnused()
    {
        var target = new FakeTarget();
        var delays = new DelayHub();
        var now = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        using var coordinator = new CaptureCoordinator(new CaptureCoordinatorOptions
        {
            ResolveTarget = () => target,
            LaunchCaptureAsync = () => Task.FromResult(true),
            Delay = delays.Wait,
            Clock = () => now,
        }, listen: false);

        await coordinator.RequestCaptureAsync(target);
        Assert.Equal(1, target.Prepared);

        now += CaptureCoordinator.RequestWatchdog;
        delays.Fire(CaptureCoordinator.RequestWatchdog);
        await Task.Yield();

        Assert.Equal(1, target.Activations); // 버려진 오버레이 뒤 복원.
        Assert.Empty(target.Opened);
    }
}
