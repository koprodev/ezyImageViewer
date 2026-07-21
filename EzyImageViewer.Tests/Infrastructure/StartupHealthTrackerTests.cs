using System.Runtime.CompilerServices;
using System.Text.Json;
using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class StartupHealthTrackerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ezy-startup-health-tests", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(
        2026, 7, 19, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RepeatedSameFailure_OffersSafeModeOnSecondOccurrence()
    {
        var tracker = CreateTracker();
        var first = tracker.RecordUnhandledException(CaptureKnownFailure("first"));
        var second = tracker.RecordUnhandledException(CaptureKnownFailure("second"));

        Assert.False(first.ShouldOfferSafeMode);
        Assert.Equal(1, first.ConsecutiveFailures);
        Assert.True(second.ShouldOfferSafeMode);
        Assert.Equal(2, second.ConsecutiveFailures);
        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(_now, second.LastFailureUtc);
    }

    [Fact]
    public void Fingerprint_ExcludesMessagesPathsAndExceptionText()
    {
        const string secretPath = @"C:\Users\someone\private-image.png";
        var tracker = CreateTracker();

        var first = tracker.RecordUnhandledException(CaptureKnownFailure(secretPath));
        tracker.MarkHealthy();
        var second = tracker.RecordUnhandledException(
            CaptureKnownFailure("different confidential message"));
        var json = File.ReadAllText(new AppDataPaths(_directory).StartupHealthFile);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.DoesNotContain(secretPath, json, StringComparison.Ordinal);
        Assert.DoesNotContain("confidential", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            nameof(InvalidOperationException), json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(4, document.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public void Fingerprint_ChangesForExceptionTypeOrStackMethodIdentity()
    {
        var tracker = CreateTracker();
        var known = tracker.RecordUnhandledException(CaptureKnownFailure("same"));
        tracker.MarkHealthy();
        var alternateStack = tracker.RecordUnhandledException(
            CaptureAlternateFailure("same"));
        tracker.MarkHealthy();
        var sharedSite = tracker.RecordUnhandledException(
            CaptureSharedSiteFailure(argumentException: false, "same"));
        tracker.MarkHealthy();
        var alternateType = tracker.RecordUnhandledException(
            CaptureSharedSiteFailure(argumentException: true, "same"));

        Assert.NotEqual(known.Fingerprint, alternateStack.Fingerprint);
        Assert.NotEqual(sharedSite.Fingerprint, alternateType.Fingerprint);
    }

    [Fact]
    public void DifferentFingerprint_ResetsConsecutiveCount()
    {
        var tracker = CreateTracker();
        _ = tracker.RecordUnhandledException(CaptureKnownFailure("one"));
        Assert.True(tracker.RecordUnhandledException(
            CaptureKnownFailure("two")).ShouldOfferSafeMode);

        var reset = tracker.RecordUnhandledException(CaptureAlternateFailure("three"));

        Assert.Equal(1, reset.ConsecutiveFailures);
        Assert.False(reset.ShouldOfferSafeMode);
    }

    [Fact]
    public void FailureOutsideSevenDayWindow_IsDeletedAndStartsNewSequence()
    {
        var time = new MutableTimeProvider(_now);
        var paths = new AppDataPaths(_directory);
        var tracker = new StartupHealthTracker(paths, timeProvider: time);
        _ = tracker.RecordUnhandledException(CaptureKnownFailure("one"));

        time.Advance(TimeSpan.FromDays(7) + TimeSpan.FromTicks(1));

        Assert.False(tracker.GetStatus().ShouldOfferSafeMode);
        Assert.False(File.Exists(paths.StartupHealthFile));
        var restarted = tracker.RecordUnhandledException(CaptureKnownFailure("two"));
        Assert.Equal(1, restarted.ConsecutiveFailures);
    }

    [Fact]
    public void FailureAtSevenDayBoundary_RemainsInSameSequence()
    {
        var time = new MutableTimeProvider(_now);
        var tracker = CreateTracker(timeProvider: time);
        _ = tracker.RecordUnhandledException(CaptureKnownFailure("one"));

        time.Advance(TimeSpan.FromDays(7));

        var repeated = tracker.RecordUnhandledException(CaptureKnownFailure("two"));
        Assert.Equal(2, repeated.ConsecutiveFailures);
        Assert.True(repeated.ShouldOfferSafeMode);
    }

    [Fact]
    public void FutureTimestamp_FailsSafeAndStartsNewSequence()
    {
        var time = new MutableTimeProvider(_now);
        var tracker = CreateTracker(timeProvider: time);
        _ = tracker.RecordUnhandledException(CaptureKnownFailure("one"));

        time.SetUtcNow(_now - TimeSpan.FromMinutes(1));

        Assert.False(tracker.GetStatus().ShouldOfferSafeMode);
        var restarted = tracker.RecordUnhandledException(CaptureKnownFailure("two"));
        Assert.Equal(1, restarted.ConsecutiveFailures);
    }

    [Fact]
    public void MarkHealthy_RemovesPersistedFailureState()
    {
        var paths = new AppDataPaths(_directory);
        var tracker = new StartupHealthTracker(paths);
        _ = tracker.RecordUnhandledException(CaptureKnownFailure("one"));
        Assert.True(File.Exists(paths.StartupHealthFile));

        tracker.MarkHealthy();

        Assert.False(File.Exists(paths.StartupHealthFile));
        Assert.Equal(0, tracker.GetStatus().ConsecutiveFailures);
        Assert.False(tracker.GetStatus().ShouldOfferSafeMode);
    }

    [Theory]
    [InlineData("{ not-json }")]
    [InlineData("")]
    public void MalformedState_IsDeletedAndReturnsSafeDefault(string content)
    {
        var paths = new AppDataPaths(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(paths.StartupHealthFile, content);
        var errors = new List<Exception>();
        var tracker = new StartupHealthTracker(paths, reportError: errors.Add);

        var status = tracker.GetStatus();

        Assert.False(status.ShouldOfferSafeMode);
        Assert.Equal(0, status.ConsecutiveFailures);
        Assert.False(File.Exists(paths.StartupHealthFile));
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void UnknownSchemaOrUnexpectedMember_IsDeletedAndReturnsSafeDefault()
    {
        var paths = new AppDataPaths(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(paths.StartupHealthFile,
            """
            {
              "schemaVersion": 99,
              "fingerprint": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
              "consecutiveFailures": 2,
              "lastFailureUtc": "2026-07-19T04:00:00+00:00",
              "unexpected": true
            }
            """);
        var tracker = new StartupHealthTracker(paths);

        var status = tracker.GetStatus();

        Assert.False(status.ShouldOfferSafeMode);
        Assert.False(File.Exists(paths.StartupHealthFile));
    }

    [Fact]
    public void ExplicitNullFingerprint_IsDeletedAndCannotBreakStartup()
    {
        var paths = new AppDataPaths(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(paths.StartupHealthFile,
            """
            {
              "schemaVersion": 1,
              "fingerprint": null,
              "consecutiveFailures": 2,
              "lastFailureUtc": "2026-07-19T04:00:00+00:00"
            }
            """);
        var tracker = new StartupHealthTracker(paths);

        var exception = Record.Exception(() => tracker.GetStatus());

        Assert.Null(exception);
        Assert.False(File.Exists(paths.StartupHealthFile));
    }

    [Fact]
    public void OversizedOrNonUtcState_IsDeletedAndReturnsSafeDefault()
    {
        var paths = new AppDataPaths(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(paths.StartupHealthFile, new byte[4097]);
        var tracker = new StartupHealthTracker(paths);

        Assert.False(tracker.GetStatus().ShouldOfferSafeMode);
        Assert.False(File.Exists(paths.StartupHealthFile));

        File.WriteAllText(paths.StartupHealthFile,
            """
            {
              "schemaVersion": 1,
              "fingerprint": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
              "consecutiveFailures": 2,
              "lastFailureUtc": "2026-07-19T13:00:00+09:00"
            }
            """);
        Assert.False(tracker.GetStatus().ShouldOfferSafeMode);
        Assert.False(File.Exists(paths.StartupHealthFile));
    }

    [Fact]
    public void ReportingCallbackFailure_IsIsolated()
    {
        var paths = new AppDataPaths(_directory);
        Directory.CreateDirectory(_directory);
        File.WriteAllText(paths.StartupHealthFile, "bad");
        var tracker = new StartupHealthTracker(
            paths,
            reportError: _ => throw new InvalidOperationException("callback"));

        var exception = Record.Exception(() => tracker.GetStatus());

        Assert.Null(exception);
        Assert.False(File.Exists(paths.StartupHealthFile));
    }

    [Fact]
    public async Task MultipleInstances_ProduceOneAtomicBoundedDocument()
    {
        var paths = new AppDataPaths(_directory);
        var trackers = Enumerable.Range(0, 8)
            .Select(_ => new StartupHealthTracker(paths))
            .ToArray();
        var failure = CaptureKnownFailure("shared");

        await Task.WhenAll(Enumerable.Range(0, 40).Select(index => Task.Run(() =>
            trackers[index % trackers.Length].RecordUnhandledException(failure))));

        var status = trackers[0].GetStatus();
        Assert.Equal(40, status.ConsecutiveFailures);
        Assert.True(status.ShouldOfferSafeMode);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
        using var document = JsonDocument.Parse(File.ReadAllText(paths.StartupHealthFile));
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void ConsecutiveCount_IsCappedByConfiguredBound()
    {
        var tracker = new StartupHealthTracker(
            new AppDataPaths(_directory),
            new StartupHealthTrackerOptions { MaximumConsecutiveFailures = 3 });
        var failure = CaptureKnownFailure("repeat");

        StartupHealthStatus status = new();
        for (var index = 0; index < 10; index++)
            status = tracker.RecordUnhandledException(failure);

        Assert.Equal(3, status.ConsecutiveFailures);
        Assert.True(status.ShouldOfferSafeMode);
    }

    [Fact]
    public void AppDataPath_IsAStableChildOfInjectedRoot()
    {
        var paths = new AppDataPaths(_directory);

        Assert.Equal(
            Path.Combine(Path.GetFullPath(_directory), "startup-health.json"),
            paths.StartupHealthFile);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private StartupHealthTracker CreateTracker(TimeProvider? timeProvider = null) =>
        new(
            new AppDataPaths(_directory),
            timeProvider: timeProvider ?? new MutableTimeProvider(_now));

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception CaptureKnownFailure(string message)
    {
        try
        {
            ThrowKnownFailure(message);
        }
        catch (Exception ex)
        {
            return ex;
        }
        throw new InvalidOperationException("Unreachable.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowKnownFailure(string message) =>
        throw new InvalidOperationException(message);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception CaptureAlternateFailure(string message)
    {
        try
        {
            throw new InvalidOperationException(message);
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Exception CaptureSharedSiteFailure(
        bool argumentException,
        string message)
    {
        try
        {
            if (argumentException)
                throw new ArgumentException(message);
            throw new InvalidOperationException(message);
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;

        public void SetUtcNow(DateTimeOffset utcNow) => _utcNow = utcNow;
    }
}
