using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using Xunit;

namespace EzyImageViewer.Tests.Documents;

public class DocumentSessionTests
{
    private static ImageDocument MakeDocument() => new()
    {
        Frame = new DecodedFrame(new byte[16], 2, 2, 8, hasAlpha: false),
        Source = DocumentSource.FromClipboard(),
        NativeSize = new PixelSize(2, 2),
    };

    [Fact]
    public async Task LatestWins_StaleResultIsDisposedAndNeverPublished()
    {
        using var session = new DocumentSession();
        var slowGate = new TaskCompletionSource<ImageDocument>();
        var slowDocument = MakeDocument();
        var fastDocument = MakeDocument();

        var slowTask = session.StartLoadAsync(async _ => await slowGate.Task);
        var fastTask = session.StartLoadAsync(_ => Task.FromResult(fastDocument));
        await fastTask;

        Assert.Equal(SessionState.Ready, session.State);
        Assert.Same(fastDocument, session.Current);

        // The superseded load completes late (its decoder ignored cancellation).
        slowGate.SetResult(slowDocument);
        await slowTask;

        Assert.Same(fastDocument, session.Current);
        Assert.True(slowDocument.Frame.IsDisposed);
        Assert.False(fastDocument.Frame.IsDisposed);
    }

    [Fact]
    public async Task DisposeDuringLoad_BlocksPublishAndDisposesResult()
    {
        var session = new DocumentSession();
        var gate = new TaskCompletionSource<ImageDocument>();
        var document = MakeDocument();

        var loadTask = session.StartLoadAsync(async _ => await gate.Task);
        session.Dispose();
        gate.SetResult(document);
        await loadTask;

        Assert.Equal(SessionState.Disposed, session.State);
        Assert.Null(session.Current);
        Assert.True(document.Frame.IsDisposed);
    }

    [Fact]
    public async Task Cancellation_IsNotPublishedAsFailed_AndKeepsExistingDocument()
    {
        using var session = new DocumentSession();
        var ready = MakeDocument();
        await session.StartLoadAsync(_ => Task.FromResult(ready));
        Assert.Equal(SessionState.Ready, session.State);

        await session.StartLoadAsync(ct => Task.FromCanceled<ImageDocument>(new CancellationToken(true)));

        Assert.Equal(SessionState.Ready, session.State);
        Assert.Same(ready, session.Current);
        Assert.Null(session.LastError);
    }

    [Fact]
    public async Task Failure_KeepsExistingReadyDocument_AndRecordsError()
    {
        using var session = new DocumentSession();
        var ready = MakeDocument();
        await session.StartLoadAsync(_ => Task.FromResult(ready));

        await session.StartLoadAsync(_ => Task.FromException<ImageDocument>(new IOException("disk gone")));

        Assert.Equal(SessionState.Ready, session.State);
        Assert.Same(ready, session.Current);
        Assert.IsType<IOException>(session.LastError);
    }

    [Fact]
    public async Task Failure_WithNoDocument_PublishesFailed()
    {
        using var session = new DocumentSession();

        await session.StartLoadAsync(_ => Task.FromException<ImageDocument>(new IOException("nope")));

        Assert.Equal(SessionState.Failed, session.State);
        Assert.Null(session.Current);
    }

    [Fact]
    public async Task NewLoad_CancelsActiveLoad()
    {
        using var session = new DocumentSession();
        var firstStarted = new TaskCompletionSource();
        var firstCanceled = new TaskCompletionSource();

        var first = session.StartLoadAsync(async ct =>
        {
            firstStarted.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, ct);
            }
            catch (OperationCanceledException)
            {
                firstCanceled.SetResult();
                throw;
            }
            return MakeDocument();
        });

        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replacement = MakeDocument();
        await session.StartLoadAsync(_ => Task.FromResult(replacement));

        await firstCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await first;
        Assert.Same(replacement, session.Current);
        Assert.Equal(SessionState.Ready, session.State);
    }

    // ---- subscriber isolation (M1 defects found while planning M2) ----

    [Fact]
    public async Task ThrowingSubscriber_DoesNotTurnASuccessfulLoadIntoAFailure()
    {
        using var session = new DocumentSession();
        var faults = new List<Exception>();
        session.SubscriberFaulted += faults.Add;
        session.Changed += () => throw new InvalidOperationException("subscriber blew up");

        var document = MakeDocument();
        await session.StartLoadAsync(_ => Task.FromResult(document));

        Assert.Equal(SessionState.Ready, session.State);
        Assert.Same(document, session.Current);
        Assert.Null(session.LastError);
        Assert.NotEmpty(faults);
    }

    [Fact]
    public async Task ThrowingSubscriber_OnTheLoadingNotification_StillRunsTheLoader()
    {
        using var session = new DocumentSession();
        session.SubscriberFaulted += _ => { };
        session.Changed += () => throw new InvalidOperationException("subscriber blew up");

        var loaderRan = false;
        var document = MakeDocument();
        await session.StartLoadAsync(_ =>
        {
            loaderRan = true;
            return Task.FromResult(document);
        });

        Assert.True(loaderRan);
        Assert.Equal(SessionState.Ready, session.State);
        Assert.Same(document, session.Current);
    }

    [Fact]
    public async Task ThrowingSubscriber_DoesNotSuppressTheOnesBehindIt()
    {
        using var session = new DocumentSession();
        var reached = 0;
        session.SubscriberFaulted += _ => { };
        session.Changed += () => throw new InvalidOperationException("first subscriber blew up");
        session.Changed += () => reached++;

        await session.StartLoadAsync(_ => Task.FromResult(MakeDocument()));

        Assert.True(reached > 0, "a faulting subscriber must not stop the notification fan-out");
    }

    [Fact]
    public async Task ThrowingDiagnosticsSink_IsContained_AndDoesNotSuppressTheSinksBehindIt()
    {
        using var session = new DocumentSession();
        var reachedSecondSink = 0;
        session.SubscriberFaulted += _ => throw new InvalidOperationException("sink blew up too");
        session.SubscriberFaulted += _ => reachedSecondSink++;
        session.Changed += () => throw new InvalidOperationException("subscriber blew up");

        await session.StartLoadAsync(_ => Task.FromResult(MakeDocument()));

        Assert.Equal(SessionState.Ready, session.State);
        Assert.Null(session.LastError);
        Assert.True(reachedSecondSink > 0, "a faulting diagnostics sink must not stop the others");
    }

    [Fact]
    public async Task ReplacedDocument_IsDisposedExactlyOnActivePublish()
    {
        using var session = new DocumentSession();
        var first = MakeDocument();
        var second = MakeDocument();

        await session.StartLoadAsync(_ => Task.FromResult(first));
        await session.StartLoadAsync(_ => Task.FromResult(second));

        Assert.True(first.Frame.IsDisposed);
        Assert.False(second.Frame.IsDisposed);
        Assert.Same(second, session.Current);
    }
}
