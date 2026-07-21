using System.Collections.Concurrent;
using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class RecoverySessionCoordinatorTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ezy-recovery-coordinator-tests", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 7, 19, 4, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Start_AfterCallerEnumerationCreatesOneMarkerAndPreservesEarlierCandidates()
    {
        var store = CreateStore();
        var previousSession = Guid.NewGuid();
        var previousWindow = Guid.NewGuid();
        store.BeginSession(previousSession);
        store.Save(CreateRecord(previousSession, previousWindow, 1));
        var previousCandidates = store.EnumerateSummaries();
        var sessionId = Guid.NewGuid();
        var coordinator = CreateImmediateCoordinator(store);

        coordinator.Start(sessionId);

        Assert.Equal(previousWindow, Assert.Single(previousCandidates).WindowId);
        Assert.Equal(2, store.EnumerateCrashMarkers().Count);
        Assert.Throws<InvalidOperationException>(() => coordinator.Start(sessionId));

        await coordinator.DisposeAsync();
        Assert.Equal(2, store.EnumerateCrashMarkers().Count);
        Assert.Equal(previousWindow, Assert.Single(store.Enumerate()).WindowId);
    }

    [Fact]
    public async Task Schedule_CancelsSupersededDebounceAndSavesOnlyLatestSnapshot()
    {
        var store = CreateStore();
        var delays = new DelayHub();
        var coordinator = new RecoverySessionCoordinator(store, TimeSpan.FromSeconds(5), delays.DelayAsync);
        var sessionId = Guid.NewGuid();
        var windowId = Guid.NewGuid();
        var firstFactoryCalled = false;
        coordinator.Start(sessionId);

        var first = coordinator.Schedule(windowId, _ =>
        {
            firstFactoryCalled = true;
            return Task.FromResult(CreateRecord(sessionId, windowId, 1));
        });
        _ = await delays.TakeAsync();

        var second = coordinator.Schedule(
            windowId,
            _ => Task.FromResult(CreateRecord(sessionId, windowId, 2)));
        var secondDelay = await delays.TakeAsync();
        secondDelay.Release();

        await Task.WhenAll(first, second);
        var saved = Assert.Single(store.Enumerate());
        Assert.False(firstFactoryCalled);
        Assert.Equal(new byte[] { 2 }, saved.Payload);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Schedule_OlderFactoryIgnoringCancellationCannotOverwriteNewerSave()
    {
        var store = CreateStore();
        var coordinator = CreateImmediateCoordinator(store);
        var sessionId = Guid.NewGuid();
        var windowId = Guid.NewGuid();
        var olderEntered = CreateSignal();
        var releaseOlder = CreateSignal();
        coordinator.Start(sessionId);

        var older = coordinator.Schedule(windowId, async _ =>
        {
            olderEntered.TrySetResult(true);
            await releaseOlder.Task;
            return CreateRecord(sessionId, windowId, 1);
        });
        await olderEntered.Task;

        var newer = coordinator.Schedule(
            windowId,
            _ => Task.FromResult(CreateRecord(sessionId, windowId, 2)));
        await newer;
        releaseOlder.TrySetResult(true);
        await older;

        Assert.Equal(new byte[] { 2 }, Assert.Single(store.Enumerate()).Payload);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Schedule_ConcurrentCallersRemainThreadSafeAndFinalScheduleWins()
    {
        var store = CreateStore();
        var coordinator = CreateImmediateCoordinator(store);
        var sessionId = Guid.NewGuid();
        var windowId = Guid.NewGuid();
        var concurrentWork = new ConcurrentBag<Task>();
        coordinator.Start(sessionId);

        Parallel.For(0, 64, value => concurrentWork.Add(coordinator.Schedule(
            windowId,
            _ => Task.FromResult(CreateRecord(sessionId, windowId, (byte)(value + 1))))));
        var final = coordinator.Schedule(
            windowId,
            _ => Task.FromResult(CreateRecord(sessionId, windowId, byte.MaxValue)));

        await Task.WhenAll(concurrentWork.Append(final));
        Assert.Equal(new byte[] { byte.MaxValue }, Assert.Single(store.Enumerate()).Payload);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Schedule_InvalidIdentityIsReportedAndErrorCallbackFaultIsContained()
    {
        var store = CreateStore();
        var errors = new ConcurrentQueue<Exception>();
        var coordinator = new RecoverySessionCoordinator(
            store,
            TimeSpan.Zero,
            static (_, _) => Task.CompletedTask,
            exception =>
            {
                errors.Enqueue(exception);
                throw new InvalidOperationException("The observer failed.");
            });
        var sessionId = Guid.NewGuid();
        var windowId = Guid.NewGuid();
        coordinator.Start(sessionId);

        await coordinator.Schedule(
            windowId,
            _ => Task.FromResult(CreateRecord(sessionId, Guid.NewGuid(), 1)));

        var error = Assert.Single(errors);
        Assert.IsType<InvalidDataException>(error);
        Assert.Empty(store.Enumerate());
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task StopWindow_MarksClosedBeforeDrainingAndClearsAfterIgnoringCancellation()
    {
        var store = CreateStore();
        var coordinator = CreateImmediateCoordinator(store);
        var sessionId = Guid.NewGuid();
        var windowId = Guid.NewGuid();
        var factoryEntered = CreateSignal();
        var releaseFactory = CreateSignal();
        coordinator.Start(sessionId);
        await coordinator.Schedule(
            windowId,
            _ => Task.FromResult(CreateRecord(sessionId, windowId, 1)));

        var pending = coordinator.Schedule(windowId, async _ =>
        {
            factoryEntered.TrySetResult(true);
            await releaseFactory.Task;
            return CreateRecord(sessionId, windowId, 2);
        });
        await factoryEntered.Task;

        var stop = coordinator.StopWindowAsync(windowId);
        Assert.False(stop.IsCompleted);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = coordinator.Schedule(
                windowId,
                _ => Task.FromResult(CreateRecord(sessionId, windowId, 3)));
        });

        releaseFactory.TrySetResult(true);
        await Task.WhenAll(pending, stop);
        Assert.Null(store.TryLoad(sessionId, windowId));
        Assert.Equal(sessionId, Assert.Single(store.EnumerateCrashMarkers()).SessionId);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task ClearWindow_DrainsOldWorkThenReopensForANewSave()
    {
        var store = CreateStore();
        var coordinator = CreateImmediateCoordinator(store);
        var sessionId = Guid.NewGuid();
        var windowId = Guid.NewGuid();
        var factoryEntered = CreateSignal();
        var releaseFactory = CreateSignal();
        coordinator.Start(sessionId);
        await coordinator.Schedule(
            windowId,
            _ => Task.FromResult(CreateRecord(sessionId, windowId, 1)));

        var pending = coordinator.Schedule(windowId, async _ =>
        {
            factoryEntered.TrySetResult(true);
            await releaseFactory.Task;
            return CreateRecord(sessionId, windowId, 2);
        });
        await factoryEntered.Task;

        var clear = coordinator.ClearWindowAsync(windowId);
        Assert.False(clear.IsCompleted);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = coordinator.Schedule(
                windowId,
                _ => Task.FromResult(CreateRecord(sessionId, windowId, 3)));
        });

        releaseFactory.TrySetResult(true);
        await Task.WhenAll(pending, clear);
        Assert.Null(store.TryLoad(sessionId, windowId));

        await coordinator.Schedule(
            windowId,
            _ => Task.FromResult(CreateRecord(sessionId, windowId, 4)));
        Assert.Equal(new byte[] { 4 }, Assert.Single(store.Enumerate()).Payload);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Complete_DrainsEveryWindowAndInvokesStoreCompletionOnlyOnce()
    {
        var store = CreateStore();
        var coordinator = CreateImmediateCoordinator(store);
        var sessionId = Guid.NewGuid();
        var firstWindow = Guid.NewGuid();
        var secondWindow = Guid.NewGuid();
        var firstEntered = CreateSignal();
        var secondEntered = CreateSignal();
        var releaseFactories = CreateSignal();
        coordinator.Start(sessionId);

        var first = coordinator.Schedule(firstWindow, async _ =>
        {
            firstEntered.TrySetResult(true);
            await releaseFactories.Task;
            return CreateRecord(sessionId, firstWindow, 1);
        });
        var second = coordinator.Schedule(secondWindow, async _ =>
        {
            secondEntered.TrySetResult(true);
            await releaseFactories.Task;
            return CreateRecord(sessionId, secondWindow, 2);
        });
        await Task.WhenAll(firstEntered.Task, secondEntered.Task);

        var completion = coordinator.CompleteAsync();
        var duplicate = coordinator.CompleteAsync();
        Assert.Same(completion, duplicate);
        Assert.False(completion.IsCompleted);
        Assert.Throws<InvalidOperationException>(() =>
        {
            _ = coordinator.Schedule(
                firstWindow,
                _ => Task.FromResult(CreateRecord(sessionId, firstWindow, 3)));
        });

        releaseFactories.TrySetResult(true);
        await Task.WhenAll(first, second, completion, duplicate);
        Assert.Empty(store.EnumerateCrashMarkers());
        Assert.Empty(store.Enumerate());

        store.BeginSession(sessionId);
        store.Save(CreateRecord(sessionId, firstWindow, 4));
        Assert.Same(completion, coordinator.CompleteAsync());
        Assert.Equal(sessionId, Assert.Single(store.EnumerateCrashMarkers()).SessionId);
        Assert.Equal(new byte[] { 4 }, Assert.Single(store.Enumerate()).Payload);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_AbnormallyDrainsPendingWorkAndPreservesMarkerAndLatestSave()
    {
        var store = CreateStore();
        var coordinator = CreateImmediateCoordinator(store);
        var sessionId = Guid.NewGuid();
        var windowId = Guid.NewGuid();
        var factoryEntered = CreateSignal();
        var releaseFactory = CreateSignal();
        coordinator.Start(sessionId);
        await coordinator.Schedule(
            windowId,
            _ => Task.FromResult(CreateRecord(sessionId, windowId, 1)));

        var pending = coordinator.Schedule(windowId, async _ =>
        {
            factoryEntered.TrySetResult(true);
            await releaseFactory.Task;
            return CreateRecord(sessionId, windowId, 2);
        });
        await factoryEntered.Task;

        var disposal = coordinator.DisposeAsync();
        Assert.False(disposal.IsCompleted);
        Assert.Throws<ObjectDisposedException>(() =>
        {
            _ = coordinator.Schedule(
                windowId,
                _ => Task.FromResult(CreateRecord(sessionId, windowId, 3)));
        });

        releaseFactory.TrySetResult(true);
        await Task.WhenAll(pending, disposal.AsTask());
        Assert.Equal(sessionId, Assert.Single(store.EnumerateCrashMarkers()).SessionId);
        Assert.Equal(new byte[] { 1 }, Assert.Single(store.Enumerate()).Payload);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Schedule_FactoryFaultIsReportedWithoutFaultingBackgroundTask()
    {
        var store = CreateStore();
        var errors = new ConcurrentQueue<Exception>();
        var coordinator = new RecoverySessionCoordinator(
            store,
            TimeSpan.Zero,
            static (_, _) => Task.CompletedTask,
            errors.Enqueue);
        var sessionId = Guid.NewGuid();
        var windowId = Guid.NewGuid();
        coordinator.Start(sessionId);

        var work = coordinator.Schedule(
            windowId,
            _ => Task.FromException<RecoveryRecord>(new IOException("Snapshot failed.")));

        await work;
        Assert.IsType<IOException>(Assert.Single(errors));
        Assert.True(work.IsCompletedSuccessfully);
        await coordinator.DisposeAsync();
    }

    [Fact]
    public async Task Schedule_SuccessReportsThePersistedRecoveryRecord()
    {
        var store = CreateStore();
        var saved = new ConcurrentQueue<RecoveryRecord>();
        var availableCount = 0;
        var coordinator = new RecoverySessionCoordinator(
            store,
            TimeSpan.Zero,
            static (_, _) => Task.CompletedTask,
            reportSaved: saved.Enqueue,
            reportAvailable: () => Interlocked.Increment(ref availableCount));
        var sessionId = Guid.NewGuid();
        var windowId = Guid.NewGuid();
        coordinator.Start(sessionId);

        await coordinator.Schedule(
            windowId,
            _ => Task.FromResult(CreateRecord(sessionId, windowId, 7)));

        Assert.Equal(windowId, Assert.Single(saved).WindowId);
        Assert.Equal(1, availableCount);
        Assert.Equal(new byte[] { 7 }, Assert.Single(store.Enumerate()).Payload);
        await coordinator.DisposeAsync();
    }

    private RecoveryStore CreateStore()
    {
        return new RecoveryStore(
            new AppDataPaths(_directory),
            timeProvider: new FixedTimeProvider(_now));
    }

    private static RecoverySessionCoordinator CreateImmediateCoordinator(RecoveryStore store)
    {
        return new RecoverySessionCoordinator(
            store,
            TimeSpan.Zero,
            static (_, _) => Task.CompletedTask);
    }

    private RecoveryRecord CreateRecord(Guid sessionId, Guid windowId, byte payload)
    {
        return new RecoveryRecord
        {
            SessionId = sessionId,
            WindowId = windowId,
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
            Payload = [payload],
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private static TaskCompletionSource<bool> CreateSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class DelayHub
    {
        private readonly ConcurrentQueue<DelayRequest> _requests = new();
        private readonly SemaphoreSlim _available = new(0);

        public Task DelayAsync(TimeSpan _, CancellationToken cancellationToken)
        {
            var request = new DelayRequest(cancellationToken);
            _requests.Enqueue(request);
            _available.Release();
            return request.Task;
        }

        public async Task<DelayRequest> TakeAsync()
        {
            await _available.WaitAsync();
            if (!_requests.TryDequeue(out var request))
                throw new InvalidOperationException("No deterministic delay is available.");
            return request;
        }
    }

    private sealed class DelayRequest
    {
        private readonly TaskCompletionSource<bool> _completion = CreateSignal();

        public DelayRequest(CancellationToken cancellationToken)
        {
            Task = WaitAsync(cancellationToken);
        }

        public Task Task { get; }

        public void Release() => _completion.TrySetResult(true);

        private async Task WaitAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(
                () => _completion.TrySetCanceled(cancellationToken));
            await _completion.Task;
        }
    }
}
