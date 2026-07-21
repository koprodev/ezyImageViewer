using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class StructuredLogServiceTests
{
    [Fact]
    public async Task DrainAsync_WritesQueuedEventsInFifoOrderAndRejectsNewEvents()
    {
        var written = new List<string>();
        var service = new StructuredLogService(
            (_, logEvent, _) =>
            {
                written.Add(logEvent.Name);
                return true;
            },
            capacity: 8);

        Assert.True(service.TryEnqueue(
            LocalLogLevel.Information,
            Event(StructuredLogEventNames.AppStarted)));
        Assert.True(service.TryEnqueue(
            LocalLogLevel.Information,
            Event(StructuredLogEventNames.DocumentOpened)));
        Assert.True(service.TryEnqueue(
            LocalLogLevel.Information,
            Event(StructuredLogEventNames.RecoverySaved)));

        await service.DrainAsync();

        Assert.Equal(
            [
                StructuredLogEventNames.AppStarted,
                StructuredLogEventNames.DocumentOpened,
                StructuredLogEventNames.RecoverySaved,
            ],
            written);
        Assert.False(service.TryEnqueue(
            LocalLogLevel.Information,
            Event(StructuredLogEventNames.SettingsSaved)));
        Assert.Equal(0, service.DroppedCount);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task FullQueue_DropsNewestAndIncrementsDroppedCount()
    {
        var started = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new List<string>();
        var writeCount = 0;
        var service = new StructuredLogService(
            (_, logEvent, _) =>
            {
                written.Add(logEvent.Name);
                if (Interlocked.Increment(ref writeCount) == 1)
                {
                    started.TrySetResult();
                    release.Task.GetAwaiter().GetResult();
                }
                return true;
            },
            capacity: 2);

        try
        {
            Assert.True(service.TryEnqueue(
                LocalLogLevel.Information,
                Event(StructuredLogEventNames.AppStarted)));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(service.TryEnqueue(
                LocalLogLevel.Information,
                Event(StructuredLogEventNames.DocumentOpened)));
            Assert.True(service.TryEnqueue(
                LocalLogLevel.Information,
                Event(StructuredLogEventNames.RecoverySaved)));
            Assert.False(service.TryEnqueue(
                LocalLogLevel.Information,
                Event(StructuredLogEventNames.SettingsSaved)));
            Assert.Equal(1, service.DroppedCount);
        }
        finally
        {
            release.TrySetResult();
            await service.DisposeAsync();
        }

        Assert.Equal(
            [
                StructuredLogEventNames.AppStarted,
                StructuredLogEventNames.DocumentOpened,
                StructuredLogEventNames.RecoverySaved,
            ],
            written);
    }

    [Fact]
    public async Task Worker_ReportsWriterFailuresAndContinuesProcessing()
    {
        var calls = 0;
        var failures = new List<Exception>();
        var service = new StructuredLogService(
            (_, _, _) => Interlocked.Increment(ref calls) switch
            {
                1 => throw new InvalidOperationException("sensitive failure detail"),
                2 => false,
                _ => true,
            },
            capacity: 4,
            failureReporter: failures.Add);

        Assert.True(service.TryEnqueue(
            LocalLogLevel.Error,
            Event(StructuredLogEventNames.DocumentOpenFailed)));
        Assert.True(service.TryEnqueue(
            LocalLogLevel.Warning,
            Event(StructuredLogEventNames.RecoverySaved)));
        Assert.True(service.TryEnqueue(
            LocalLogLevel.Information,
            Event(StructuredLogEventNames.RecoveryRestored)));

        await service.DisposeAsync();

        Assert.Equal(3, calls);
        Assert.Collection(
            failures,
            failure => Assert.IsType<InvalidOperationException>(failure),
            failure => Assert.IsType<IOException>(failure));
    }

    [Fact]
    public async Task ThrowingFailureReporter_DoesNotFaultWorkerOrLoseLaterEntries()
    {
        var calls = 0;
        var failureReports = 0;
        var service = new StructuredLogService(
            (_, _, _) => Interlocked.Increment(ref calls) != 1,
            capacity: 2,
            failureReporter: _ =>
            {
                Interlocked.Increment(ref failureReports);
                throw new InvalidOperationException("reporter failure");
            });

        Assert.True(service.TryEnqueue(
            LocalLogLevel.Warning,
            Event(StructuredLogEventNames.ReleasePageLaunchFailed)));
        Assert.True(service.TryEnqueue(
            LocalLogLevel.Information,
            Event(StructuredLogEventNames.AppStarted)));

        await service.DrainAsync();

        Assert.Equal(2, calls);
        Assert.Equal(1, failureReports);
    }

    [Fact]
    public async Task TryEnqueue_AllowsOnlyDeclaredM9EventNames()
    {
        string[] allowedNames =
        [
            StructuredLogEventNames.AppStarted,
            StructuredLogEventNames.AppStopped,
            StructuredLogEventNames.DocumentOpened,
            StructuredLogEventNames.DocumentOpenFailed,
            StructuredLogEventNames.RecoverySaved,
            StructuredLogEventNames.RecoveryOperationFailed,
            StructuredLogEventNames.RecoveryRestored,
            StructuredLogEventNames.RecoveryCleanupFailed,
            StructuredLogEventNames.RecentFileOperationFailed,
            StructuredLogEventNames.AppDataProtectionFailed,
            StructuredLogEventNames.StartupFailureRecorded,
            StructuredLogEventNames.SafeModeEnabled,
            StructuredLogEventNames.SettingsSaved,
            StructuredLogEventNames.ReleasePageLaunchFailed,
        ];
        var written = new List<string>();
        var service = new StructuredLogService(
            (_, logEvent, _) =>
            {
                written.Add(logEvent.Name);
                return true;
            },
            capacity: allowedNames.Length);

        foreach (var name in allowedNames)
        {
            Assert.True(service.TryEnqueue(
                LocalLogLevel.Information,
                Event(name)));
        }
        Assert.Throws<ArgumentException>(() => service.TryEnqueue(
            LocalLogLevel.Information,
            Event("customer-name-or-free-text")));

        await service.DisposeAsync();

        Assert.Equal(allowedNames, written);
    }

    private static StructuredLogEvent Event(string name)
    {
        return new StructuredLogEvent { Name = name };
    }
}
