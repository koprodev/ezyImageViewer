using EzyImageViewer.Core.Activation;
using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Activation;

public class ActivationRouterTests
{
    [Fact]
    public async Task Post_AssignsMonotonicSequences_AndDispatchesInPostOrder()
    {
        await using var router = new ActivationRouter();
        var dispatched = new List<long>();
        var done = new TaskCompletionSource();

        for (var i = 0; i < 10; i++)
            router.Post(new LaunchActivation());

        router.Start(envelope =>
        {
            lock (dispatched)
            {
                dispatched.Add(envelope.Sequence);
                if (dispatched.Count == 10)
                    done.TrySetResult();
            }
            return Task.CompletedTask;
        });

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Enumerable.Range(0, 10).Select(i => (long)i), dispatched);
    }

    [Fact]
    public async Task Post_SameRequestObjectTwice_GetsDistinctSequences()
    {
        await using var router = new ActivationRouter();
        var request = new LaunchActivation();

        var first = router.Post(request);
        var second = router.Post(request);

        Assert.Equal(0, first);
        Assert.Equal(1, second);
    }

    [Fact]
    public async Task ConcurrentPosts_DispatchOrderEqualsSequenceOrder()
    {
        await using var router = new ActivationRouter();
        var dispatched = new List<long>();
        var done = new TaskCompletionSource();
        const int total = 400;

        router.Start(envelope =>
        {
            lock (dispatched)
            {
                dispatched.Add(envelope.Sequence);
                if (dispatched.Count == total)
                    done.TrySetResult();
            }
            return Task.CompletedTask;
        });

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            for (var i = 0; i < total / 4; i++)
                router.Post(new LaunchActivation());
        })));

        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Atomic assign+enqueue: FIFO dispatch must be strictly increasing, not merely unique.
        Assert.Equal(Enumerable.Range(0, total).Select(i => (long)i), dispatched);
    }

    [Fact]
    public async Task HandlerException_RaisesDispatchFailed_AndLoopContinues()
    {
        await using var router = new ActivationRouter();
        var failures = new List<Exception>();
        var second = new TaskCompletionSource();
        router.DispatchFailed += (_, ex) => { lock (failures) failures.Add(ex); };

        router.Start(envelope =>
        {
            if (envelope.Sequence == 0)
                throw new InvalidOperationException("boom");
            second.TrySetResult();
            return Task.CompletedTask;
        });

        router.Post(new LaunchActivation());
        router.Post(new LaunchActivation());

        await second.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(failures);
        Assert.IsType<InvalidOperationException>(failures[0]);
    }

    [Fact]
    public async Task Dispatch_IsNotBlockedByWorkKickedOffByHandler()
    {
        await using var router = new ActivationRouter();
        var longWork = new TaskCompletionSource();
        var secondDispatched = new TaskCompletionSource();

        router.Start(envelope =>
        {
            if (envelope.Sequence == 0)
                _ = longWork.Task; // kicked-off work is NOT awaited (handler contract)
            else
                secondDispatched.TrySetResult();
            return Task.CompletedTask;
        });

        router.Post(new LaunchActivation());
        router.Post(new LaunchActivation());

        await secondDispatched.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(longWork.Task.IsCompleted);
        longWork.TrySetResult();
    }

    [Fact]
    public void FileActivation_ExposesPrimaryPathAndPayload()
    {
        var activation = new FileActivation(["a.png", "b.png"], OpenTarget.NewWindow);
        Assert.Equal("a.png", activation.PrimaryPath);
        Assert.Equal(2, activation.Paths.Count);
        Assert.Equal(OpenTarget.NewWindow, activation.Target);
        Assert.False(activation.IsInitial);
    }

    [Theory]
    [InlineData(SingleInstanceBehavior.ReuseExistingWindow, false, OpenTarget.ExistingWindow)]
    [InlineData(SingleInstanceBehavior.OpenNewWindow, false, OpenTarget.NewWindow)]
    [InlineData(SingleInstanceBehavior.OpenNewWindow, true, OpenTarget.ExistingWindow)]
    public void ActivationRoutingPolicy_AppliesOnlyToWarmUntargetedFileActivation(
        SingleInstanceBehavior behavior,
        bool initial,
        OpenTarget expected)
    {
        var request = new FileActivation(["a.png"], IsInitial: initial);

        var routed = Assert.IsType<FileActivation>(ActivationRoutingPolicy.Apply(
            request,
            new AppSettings { SingleInstanceBehavior = behavior }));

        Assert.Equal(expected, routed.Target);
        Assert.Equal(initial, routed.IsInitial);
    }

    [Fact]
    public void ActivationRoutingPolicy_SafeModeSuppressesOnlyInitialExternalPayloads()
    {
        var settings = new AppSettings();
        var initialFile = new FileActivation(["crash.png"], IsInitial: true);
        var initialProtocol = new ProtocolActivation(
            new Uri("ezyimageviewer://open?value=crash"),
            IsInitial: true);
        var warmFile = new FileActivation(["chosen.png"], IsInitial: false);

        Assert.IsType<LaunchActivation>(ActivationRoutingPolicy.Apply(
            initialFile, settings, safeMode: true));
        Assert.IsType<LaunchActivation>(ActivationRoutingPolicy.Apply(
            initialProtocol, settings, safeMode: true));
        Assert.Same(warmFile, ActivationRoutingPolicy.Apply(
            warmFile, settings, safeMode: true));
        Assert.Same(initialFile, ActivationRoutingPolicy.Apply(
            initialFile, settings, safeMode: false));
    }
}
