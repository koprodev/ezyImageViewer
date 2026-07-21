using EzyImageViewer.Capture.Clipboard;
using EzyImageViewer.Capture.Snipping;
using Xunit;

namespace EzyImageViewer.Tests.Capture;

/// <summary>
/// [21차] 보완 2 race contracts, driven through injected clipboard/launcher fakes: a second
/// update during a read is coalesced (not lost), a result completing after dispose touches
/// nothing, a closed armed target falls back, and a stale launch failure stays silent.
/// </summary>
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

    /// <summary>Deterministic stand-in for the coordinator's Delay seam: pending timers fire
    /// only when a test says so, keyed by the requested duration.</summary>
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
        var second = coordinator.PumpClipboardUpdateAsync(); // arrives while read #1 is in flight
        await second; // returns immediately: marked pending

        Assert.Single(issued);
        reads.Dequeue().SetResult(Payload(1)); // read #1 completes → pending turn issues read #2
        await Task.Yield();
        Assert.Equal(2, issued.Count);
        reads.Dequeue().SetResult(Payload(2));
        await first;

        // Both images were served: the first notified, the second (different content) too.
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

        await coordinator.RequestCaptureAsync(closed); // armed on a window that then closes
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
        launch1.SetResult(false); // the OLDER request fails after a newer one superseded it
        await request1;
        await request2;

        Assert.Empty(target.Statuses); // stale failure may not speak
        coordinator.HandlePayload(Payload(5), hasMarker: false);
        Assert.Single(target.Opened); // the newer arm still auto-opens
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
        // The user follows the guidance (Win+Shift+S): that capture still auto-opens.
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
        await coordinator.RequestCaptureAsync(target); // supersedes the first request
        await coordinator.HandleProtocolResponseAsync(Response(launched[0]));
        Assert.Empty(target.Opened); // the older callback may not deliver

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
        // The request ended: a later clipboard image is unsolicited (notice), never auto-open.
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
        // Win+Shift+S guidance: the manual capture arrives via clipboard and still auto-opens.
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
        await coordinator.HandleProtocolResponseAsync(callback); // OS re-delivery / replay

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
        await coordinator.RequestCaptureAsync(targetB); // arrives while A's token is redeeming
        redeems.Dequeue().SetResult(Payload(21));
        await callbackA;

        // A completed on ITS origin; B's request state was not consumed or disarmed.
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
                return Task.FromResult(launched.Count == 1); // A launches, B fails
            },
            RedeemTokenAsync = (token, _) =>
            {
                redeemed.Add(token);
                return Task.FromResult<ClipboardImagePayload?>(Payload(23));
            },
            LaunchFallbackMessage = "fallback",
        }, listen: false);

        await coordinator.RequestCaptureAsync(targetA);
        await coordinator.RequestCaptureAsync(targetB); // launch fails → clipboard fallback armed

        await coordinator.HandleProtocolResponseAsync(Response(launched[0])); // stale A callback
        Assert.Empty(redeemed); // superseded: it may not redeem or be treated as cold-start

        // The user follows the fallback (Win+Shift+S): B's armed clipboard contract is intact.
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

        // Settle mutes only a byte-identical copy of the capture that just opened.
        coordinator.HandlePayload(Payload(13), hasMarker: false);
        Assert.Empty(target.Notices);
        coordinator.HandlePayload(Payload(26), hasMarker: false);
        Assert.Single(target.Notices);
    }

    [Fact]
    public async Task InstantOpen_MutesDuplicateClipboardUpdates_OfTheSameCapture()
    {
        // A single copy can raise several WM_CLIPBOARDUPDATEs: after the capture opened, its
        // byte-identical re-post must not raise the passive notice (사용자 게이트 2026-07-18).
        var (coordinator, target, _, _) = OfficialSetup(Payload(36));
        using var _1 = coordinator;

        await coordinator.RequestCaptureAsync(target);
        coordinator.HandlePayload(Payload(37), hasMarker: false);
        Assert.Single(target.Opened);

        coordinator.HandlePayload(Payload(37), hasMarker: false); // duplicate update
        Assert.Empty(target.Notices);
        coordinator.HandlePayload(Payload(38), hasMarker: false); // genuinely new image
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

        coordinator.HandlePayload(Payload(39), hasMarker: false); // duplicate update
        Assert.Empty(target.Notices);
    }

    [Fact]
    public async Task InFlightOpen_ConsumesTheRequest_SoALateFailureCallbackStaysSilent()
    {
        var (coordinator, target, launched, _) = OfficialSetup(Payload(27));
        using var _1 = coordinator;

        await coordinator.RequestCaptureAsync(target);
        coordinator.HandlePayload(Payload(28), hasMarker: false);
        Assert.Single(target.Opened); // instant: the first image is the capture

        await coordinator.HandleProtocolResponseAsync(
            Response(launched.Single(), code: 500, token: null));

        Assert.Empty(target.Statuses); // the consumed request's stale callback may not speak
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
        // Measured on this machine: no modern Snipping Tool → the redirect callback never
        // arrives and the capture only lands on the clipboard. No grace wait: the first
        // image to arrive is the requested capture.
        var (coordinator, target, launched, redeemed) = OfficialSetup(Payload(30));
        using var _1 = coordinator;

        await coordinator.RequestCaptureAsync(target);
        Assert.Equal(1, target.Prepared); // minimized out of the shot
        coordinator.HandlePayload(Payload(31), hasMarker: false);

        Assert.Single(target.Opened); // instantly, on the origin window
        Assert.Equal(1, target.Activations);
        Assert.Empty(target.Notices);

        // The late (or never) callback may not double-open or redeem.
        await coordinator.HandleProtocolResponseAsync(Response(launched.Single()));
        Assert.Empty(redeemed);
        Assert.Single(target.Opened);
    }

    [Fact]
    public async Task OfficialWatchdog_EndsASilentRequest_AndRestoresTheWindow()
    {
        var (coordinator, target, delays, launched, redeemed) = OfficialSetupWithDelays(Payload(34));
        using var _1 = coordinator;

        await coordinator.RequestCaptureAsync(target); // Esc on a legacy host: nothing arrives
        delays.Fire(CaptureCoordinator.RequestWatchdog);
        await Task.Yield();

        Assert.Equal(1, target.Activations); // restored from the capture minimize
        await coordinator.HandleProtocolResponseAsync(Response(launched.Single()));
        Assert.Empty(redeemed); // the expired request no longer accepts its callback
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
        coordinator.HandlePayload(Payload(35), hasMarker: false); // capture consumed the arm
        Assert.Single(target.Opened);
        var activationsAfterOpen = target.Activations;

        delays.Fire(CaptureCoordinator.RequestWatchdog);
        await Task.Yield();
        Assert.Equal(activationsAfterOpen, target.Activations); // no focus steal after consume
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

        Assert.Equal(1, target.Activations); // brought back after the abandoned overlay
        Assert.Empty(target.Opened);
    }
}
