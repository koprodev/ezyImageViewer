using System.Collections.Concurrent;
using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class RecentFileCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ezy-recent-coordinator-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RecordOpened_IsNonblockingFifoAndDeduplicatesNewestEntry()
    {
        Directory.CreateDirectory(_directory);
        var first = CreateDocument("first.png");
        var second = CreateDocument("second.png");
        var firstProbeEntered = CreateSignal();
        var releaseFirstProbe = CreateSignal();
        var firstProbeCount = 0;
        var store = new RecentFileStore(
            Path.Combine(_directory, "recent.json"),
            fileExists: path =>
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(path, first)
                    && Interlocked.Increment(ref firstProbeCount) == 1)
                {
                    firstProbeEntered.TrySetResult(true);
                    releaseFirstProbe.Task.GetAwaiter().GetResult();
                }
                return File.Exists(path);
            });
        await using var coordinator = new RecentFileCoordinator(store, initiallyEnabled: true);

        Assert.True(coordinator.RecordOpened(first));
        Task<IReadOnlyList<RecentFileEntry>> snapshotTask;
        bool secondAccepted;
        bool firstAcceptedAgain;
        bool snapshotWasPending;
        try
        {
            await firstProbeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            secondAccepted = coordinator.RecordOpened(second);
            firstAcceptedAgain = coordinator.RecordOpened(first);
            snapshotTask = coordinator.SnapshotAsync();
            snapshotWasPending = !snapshotTask.IsCompleted;
        }
        finally
        {
            releaseFirstProbe.TrySetResult(true);
        }
        var snapshot = await snapshotTask;

        Assert.True(secondAccepted);
        Assert.True(firstAcceptedAgain);
        Assert.True(snapshotWasPending);
        Assert.Equal(new[] { first, second }, snapshot.Select(entry => entry.Path));
    }

    [Fact]
    public async Task Disable_ClosesAdmissionBeforeClearAndEnableWaitsBehindClear()
    {
        Directory.CreateDirectory(_directory);
        var first = CreateDocument("first.png");
        var later = CreateDocument("later.png");
        var firstProbeEntered = CreateSignal();
        var releaseFirstProbe = CreateSignal();
        var firstProbeCount = 0;
        var recentPath = Path.Combine(_directory, "recent.json");
        var store = new RecentFileStore(
            recentPath,
            fileExists: path =>
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(path, first)
                    && Interlocked.Increment(ref firstProbeCount) == 1)
                {
                    firstProbeEntered.TrySetResult(true);
                    releaseFirstProbe.Task.GetAwaiter().GetResult();
                }
                return File.Exists(path);
            });
        await using var coordinator = new RecentFileCoordinator(store, initiallyEnabled: true);

        Assert.True(coordinator.RecordOpened(first));
        Task disableTask;
        Task enableTask;
        bool recordAfterDisable;
        bool recordBeforeEnableCompleted;
        bool disableWasPending;
        bool enableWasPending;
        try
        {
            await firstProbeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            disableTask = coordinator.SetEnabledAsync(enabled: false);
            recordAfterDisable = coordinator.RecordOpened(later);
            enableTask = coordinator.SetEnabledAsync(enabled: true);
            recordBeforeEnableCompleted = coordinator.RecordOpened(later);
            disableWasPending = !disableTask.IsCompleted;
            enableWasPending = !enableTask.IsCompleted;
        }
        finally
        {
            releaseFirstProbe.TrySetResult(true);
        }
        await disableTask;
        await enableTask;

        Assert.False(recordAfterDisable);
        Assert.False(recordBeforeEnableCompleted);
        Assert.True(disableWasPending);
        Assert.True(enableWasPending);
        Assert.True(coordinator.RecordOpened(later));
        await coordinator.DrainAsync();
        var snapshot = await coordinator.SnapshotAsync();

        Assert.Equal(new[] { later }, snapshot.Select(entry => entry.Path));
        Assert.DoesNotContain(first, File.ReadAllText(recentPath));
    }

    [Fact]
    public async Task Snapshot_PrunesMissingFilesAfterPrecedingWrites()
    {
        Directory.CreateDirectory(_directory);
        var retained = CreateDocument("retained.png");
        var removed = CreateDocument("removed.png");
        var store = new RecentFileStore(Path.Combine(_directory, "recent.json"));
        await using var coordinator = new RecentFileCoordinator(store, initiallyEnabled: true);

        Assert.True(coordinator.RecordOpened(retained));
        Assert.True(coordinator.RecordOpened(removed));
        await coordinator.DrainAsync();
        File.Delete(removed);

        var snapshot = await coordinator.SnapshotAsync();

        Assert.Equal(new[] { retained }, snapshot.Select(entry => entry.Path));
    }

    [Fact]
    public async Task Clear_RemovesHistoryThenReopensAdmissionWhenEnabled()
    {
        Directory.CreateDirectory(_directory);
        var previous = CreateDocument("previous.png");
        var later = CreateDocument("later.png");
        var store = new RecentFileStore(Path.Combine(_directory, "recent.json"));
        await using var coordinator = new RecentFileCoordinator(store, initiallyEnabled: true);
        Assert.True(coordinator.RecordOpened(previous));

        await coordinator.ClearAsync();

        Assert.Empty(await coordinator.SnapshotAsync());
        Assert.True(coordinator.RecordOpened(later));
        Assert.Equal(later, Assert.Single(await coordinator.SnapshotAsync()).Path);
    }

    [Fact]
    public async Task Drain_WaitsForEarlierRecord()
    {
        Directory.CreateDirectory(_directory);
        var path = CreateDocument("blocked.png");
        var probeEntered = CreateSignal();
        var releaseProbe = CreateSignal();
        var store = new RecentFileStore(
            Path.Combine(_directory, "recent.json"),
            fileExists: candidate =>
            {
                probeEntered.TrySetResult(true);
                releaseProbe.Task.GetAwaiter().GetResult();
                return File.Exists(candidate);
            });
        await using var coordinator = new RecentFileCoordinator(store, initiallyEnabled: true);

        Assert.True(coordinator.RecordOpened(path));
        Task drainTask;
        bool drainWasPending;
        try
        {
            await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            drainTask = coordinator.DrainAsync();
            drainWasPending = !drainTask.IsCompleted;
        }
        finally
        {
            releaseProbe.TrySetResult(true);
        }
        await drainTask;

        Assert.True(drainWasPending);
        Assert.True(File.Exists(Path.Combine(_directory, "recent.json")));
    }

    [Fact]
    public async Task Dispose_DrainsAcceptedWorkAndRejectsNewWork()
    {
        Directory.CreateDirectory(_directory);
        var accepted = CreateDocument("accepted.png");
        var rejected = CreateDocument("rejected.png");
        var probeEntered = CreateSignal();
        var releaseProbe = CreateSignal();
        var store = new RecentFileStore(
            Path.Combine(_directory, "recent.json"),
            fileExists: path =>
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(path, accepted))
                {
                    probeEntered.TrySetResult(true);
                    releaseProbe.Task.GetAwaiter().GetResult();
                }
                return File.Exists(path);
            });
        var coordinator = new RecentFileCoordinator(store, initiallyEnabled: true);

        Assert.True(coordinator.RecordOpened(accepted));
        ValueTask disposeTask;
        bool disposeWasPending;
        bool rejectedRecordAccepted;
        try
        {
            await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            disposeTask = coordinator.DisposeAsync();
            disposeWasPending = !disposeTask.IsCompleted;
            rejectedRecordAccepted = coordinator.RecordOpened(rejected);
        }
        finally
        {
            releaseProbe.TrySetResult(true);
        }
        await disposeTask;
        await coordinator.DisposeAsync();

        Assert.True(disposeWasPending);
        Assert.False(rejectedRecordAccepted);
        await Assert.ThrowsAsync<ObjectDisposedException>(coordinator.DrainAsync);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => coordinator.SetEnabledAsync(true));
        await Assert.ThrowsAsync<ObjectDisposedException>(coordinator.SnapshotAsync);
        Assert.DoesNotContain(rejected, File.ReadAllText(Path.Combine(_directory, "recent.json")));
    }

    [Fact]
    public async Task ClearFailure_KeepsAdmissionClosedUntilADeleteRetrySucceeds()
    {
        Directory.CreateDirectory(_directory);
        var previous = CreateDocument("previous.png");
        var later = CreateDocument("later.png");
        var recentPath = Path.Combine(_directory, "recent.json");
        var store = new RecentFileStore(recentPath);
        store.Add(previous, enabled: true);
        var errors = new ConcurrentQueue<Exception>();
        await using var coordinator = new RecentFileCoordinator(
            store,
            initiallyEnabled: true,
            errors.Enqueue);

        using (var deleteBlocker = new FileStream(
            recentPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            var disableError = await Assert.ThrowsAsync<RecentFileHistoryClearException>(
                () => coordinator.SetEnabledAsync(enabled: false));
            var enableError = await Assert.ThrowsAsync<RecentFileHistoryClearException>(
                () => coordinator.SetEnabledAsync(enabled: true));

            Assert.False(coordinator.RecordOpened(later));
            Assert.True(File.Exists(recentPath));
            Assert.IsAssignableFrom<IOException>(disableError.InnerException);
            Assert.IsAssignableFrom<IOException>(enableError.InnerException);
        }

        await coordinator.SetEnabledAsync(enabled: true);
        Assert.True(coordinator.RecordOpened(later));
        var snapshot = await coordinator.SnapshotAsync();

        Assert.Equal(new[] { later }, snapshot.Select(entry => entry.Path));
        Assert.True(errors.Count >= 2);
        Assert.All(errors, exception => Assert.IsAssignableFrom<IOException>(exception));
    }

    [Fact]
    public async Task Worker_ReportsArgumentAndIoErrorsAndContinuesWithoutFaultingDrain()
    {
        Directory.CreateDirectory(_directory);
        var errors = new ConcurrentQueue<Exception>();
        var validDocument = CreateDocument("valid.png");
        var validStore = new RecentFileStore(Path.Combine(_directory, "valid-recent.json"));
        await using (var coordinator = new RecentFileCoordinator(
            validStore,
            initiallyEnabled: true,
            errors.Enqueue))
        {
            Assert.True(coordinator.RecordOpened("\0"));
            Assert.True(coordinator.RecordOpened(validDocument));
            await coordinator.DrainAsync();
            Assert.Single(await coordinator.SnapshotAsync());
        }

        var ioStore = new RecentFileStore(_directory);
        await using (var coordinator = new RecentFileCoordinator(
            ioStore,
            initiallyEnabled: true,
            exception =>
            {
                errors.Enqueue(exception);
                throw new InvalidOperationException("The reporter must not stop the writer.");
            }))
        {
            Assert.True(coordinator.RecordOpened(validDocument));
            await coordinator.DrainAsync();
        }

        Assert.Contains(errors, exception => exception is ArgumentException);
        Assert.Contains(errors, exception => exception is IOException or UnauthorizedAccessException);
    }

    [Fact]
    public async Task PauseForSession_HidesAndRejectsWithoutDeletingPersistedHistory()
    {
        Directory.CreateDirectory(_directory);
        var previous = CreateDocument("previous.png");
        var rejected = CreateDocument("rejected.png");
        var recentPath = Path.Combine(_directory, "recent.json");
        var store = new RecentFileStore(recentPath);
        store.Add(previous, enabled: true);
        await using var coordinator = new RecentFileCoordinator(store, initiallyEnabled: true);

        coordinator.PauseForSession();
        await coordinator.SetEnabledAsync(enabled: true);

        Assert.False(coordinator.RecordOpened(rejected));
        Assert.Empty(await coordinator.SnapshotAsync());
        await coordinator.DrainAsync();
        Assert.Equal(
            previous,
            Assert.Single(new RecentFileStore(recentPath).Load(enabled: true)).Path);
    }

    [Fact]
    public async Task InitiallyDisabled_ClearsExistingStoreAndRejectsRecords()
    {
        Directory.CreateDirectory(_directory);
        var path = CreateDocument("private.png");
        var recentPath = Path.Combine(_directory, "recent.json");
        var store = new RecentFileStore(recentPath);
        store.Add(path, enabled: true);
        Assert.True(File.Exists(recentPath));

        await using var coordinator = new RecentFileCoordinator(store, initiallyEnabled: false);

        Assert.False(coordinator.RecordOpened(path));
        await coordinator.DrainAsync();
        Assert.False(File.Exists(recentPath));
        Assert.Empty(await coordinator.SnapshotAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private string CreateDocument(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, [1]);
        return path;
    }

    private static TaskCompletionSource<bool> CreateSignal() => new(
        TaskCreationOptions.RunContinuationsAsynchronously);
}
