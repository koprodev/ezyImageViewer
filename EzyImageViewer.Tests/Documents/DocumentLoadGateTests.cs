using EzyImageViewer.Core.Commands;
using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using Xunit;

namespace EzyImageViewer.Tests.Documents;

/// <summary>FR-HIST-005: the unsaved-edit guard in front of every document replacement.</summary>
public class DocumentLoadGateTests
{
    private static ImageDocument MakeDocument() => new()
    {
        Frame = new DecodedFrame(new byte[16], 2, 2, 8, hasAlpha: false),
        Source = DocumentSource.FromClipboard(),
        NativeSize = new PixelSize(2, 2),
    };

    private static RectangleAnnotation Rect() =>
        new() { Id = Guid.NewGuid(), Bounds = new RectF(0, 0, 5, 5) };

    private sealed class Harness : IDisposable
    {
        public DocumentSession Session { get; } = new();
        public DocumentEditor Editor { get; } = new();
        public DocumentLoadGate Gate { get; }
        public int Prompts { get; private set; }
        public DiscardDecision Answer { get; set; } = DiscardDecision.Discard;
        public TaskCompletionSource<DiscardDecision>? Pending { get; set; }

        public Harness()
        {
            Gate = new DocumentLoadGate(Session, Editor, () =>
            {
                Prompts++;
                return Pending is null ? Task.FromResult(Answer) : Pending.Task;
            });
        }

        /// <summary>Loads a document and dirties it, which is the only state that arms the gate.</summary>
        public async Task ArmAsync()
        {
            await Session.StartLoadAsync(_ => Task.FromResult(MakeDocument()));
            Editor.Reset(Session.Current);
            Editor.Apply(new AddAnnotationCommand(Rect()));
        }

        public void Dispose() => Session.Dispose();
    }

    [Fact]
    public async Task CleanDocument_LoadsWithoutPrompting()
    {
        using var harness = new Harness();
        var loaded = MakeDocument();

        await harness.Gate.RequestLoadAsync(_ => Task.FromResult(loaded));

        Assert.Equal(0, harness.Prompts);
        Assert.Same(loaded, harness.Session.Current);
    }

    [Fact]
    public async Task ModifiedDocument_PromptsBeforeReplacing()
    {
        using var harness = new Harness();
        await harness.ArmAsync();
        var replacement = MakeDocument();

        await harness.Gate.RequestLoadAsync(_ => Task.FromResult(replacement));

        Assert.Equal(1, harness.Prompts);
        Assert.Same(replacement, harness.Session.Current);
    }

    [Fact]
    public async Task Cancel_KeepsTheEditedDocument()
    {
        using var harness = new Harness();
        await harness.ArmAsync();
        var original = harness.Session.Current;
        harness.Answer = DiscardDecision.Cancel;

        await harness.Gate.RequestLoadAsync(_ => Task.FromResult(MakeDocument()));

        Assert.Same(original, harness.Session.Current);
        Assert.True(harness.Editor.IsModified);
    }

    [Fact]
    public async Task ApprovedDiscard_IsNotAskedAgainWhileThatLoadIsStillDecoding()
    {
        // Approval is scoped to the in-flight load: a second request for the same still-dirty
        // document during that decode must not re-prompt. (Once the load settles the approval is
        // dropped — see FailedDiscard_DoesNotLatchApprovalIntoTheNextLoad.)
        using var harness = new Harness();
        await harness.ArmAsync();
        var decoding = new TaskCompletionSource<ImageDocument>();

        var first = harness.Gate.RequestLoadAsync(async _ => await decoding.Task);
        Assert.Equal(1, harness.Prompts);

        await harness.Gate.RequestLoadAsync(_ => Task.FromResult(MakeDocument()));
        Assert.Equal(1, harness.Prompts);

        decoding.SetResult(MakeDocument());
        await first;
    }

    [Fact]
    public async Task FailedLoadAfterDiscard_LeavesTheEditsIntact()
    {
        using var harness = new Harness();
        await harness.ArmAsync();
        var original = harness.Session.Current;

        await harness.Gate.RequestLoadAsync(_ => Task.FromException<ImageDocument>(new IOException("disk gone")));

        // Approval is recorded, not acted on: nothing is destroyed until a load actually succeeds.
        Assert.Same(original, harness.Session.Current);
        Assert.True(harness.Editor.IsModified);
        Assert.Single(harness.Editor.State.Annotations);
    }

    [Fact]
    public async Task RequestsArrivingDuringAPrompt_CollapseToTheNewest()
    {
        using var harness = new Harness();
        await harness.ArmAsync();
        harness.Pending = new TaskCompletionSource<DiscardDecision>();

        var first = harness.Gate.RequestLoadAsync(_ => Task.FromResult(MakeDocument()));
        var superseded = MakeDocument();
        var winner = MakeDocument();
        await harness.Gate.RequestLoadAsync(_ => Task.FromResult(superseded));
        await harness.Gate.RequestLoadAsync(_ => Task.FromResult(winner));

        harness.Pending.SetResult(DiscardDecision.Discard);
        await first;

        Assert.Equal(1, harness.Prompts);
        Assert.Same(winner, harness.Session.Current);
    }

    [Fact]
    public async Task CancelDuringAPrompt_AlsoDropsWhatQueuedBehindIt()
    {
        using var harness = new Harness();
        await harness.ArmAsync();
        var original = harness.Session.Current;
        harness.Pending = new TaskCompletionSource<DiscardDecision>();

        var first = harness.Gate.RequestLoadAsync(_ => Task.FromResult(MakeDocument()));
        await harness.Gate.RequestLoadAsync(_ => Task.FromResult(MakeDocument()));

        harness.Pending.SetResult(DiscardDecision.Cancel);
        await first;

        Assert.Same(original, harness.Session.Current);
        Assert.Equal(1, harness.Prompts);
    }

    [Fact]
    public async Task SaveDecision_DoesNotReplaceWhileNoWriterExists()
    {
        // Fail-closed until M6 wires a writer: Save must never fall through to the discard path.
        using var harness = new Harness();
        await harness.ArmAsync();
        var original = harness.Session.Current;
        harness.Answer = DiscardDecision.Save;

        await harness.Gate.RequestLoadAsync(_ => Task.FromResult(MakeDocument()));

        Assert.Equal(1, harness.Prompts);
        Assert.Same(original, harness.Session.Current);
        Assert.True(harness.Editor.IsModified);
    }

    [Fact]
    public async Task ThrowingConfirm_DropsTheQueuedLoader_AndTheNextRequestPromptsFresh()
    {
        using var harness = new Harness();
        await harness.ArmAsync();
        var original = harness.Session.Current;
        harness.Pending = new TaskCompletionSource<DiscardDecision>();

        var first = harness.Gate.RequestLoadAsync(_ => Task.FromResult(MakeDocument()));
        var queuedRan = false;
        await harness.Gate.RequestLoadAsync(_ =>
        {
            queuedRan = true;
            return Task.FromResult(MakeDocument());
        });

        harness.Pending.SetException(new InvalidOperationException("dialog torn down"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first);

        // The queued loader must not survive the fault to be resurrected by a later prompt.
        harness.Pending = null;
        harness.Answer = DiscardDecision.Cancel;
        await harness.Gate.RequestLoadAsync(_ => Task.FromResult(MakeDocument()));

        Assert.False(queuedRan);
        Assert.Equal(2, harness.Prompts);
        Assert.Same(original, harness.Session.Current);
        Assert.True(harness.Editor.IsModified);
    }

    [Fact]
    public async Task FailedDiscard_DoesNotLatchApprovalIntoTheNextLoad()
    {
        // Regression: a discard approval that outlived a failed load once bypassed the guard on the
        // very next open, destroying edits with no prompt.
        using var harness = new Harness();
        await harness.ArmAsync();

        await harness.Gate.RequestLoadAsync(_ => Task.FromException<ImageDocument>(new IOException("boom")));
        Assert.Equal(1, harness.Prompts);
        Assert.True(harness.Editor.IsModified);

        // Second open: the document is still the dirty original, so it must prompt again.
        harness.Answer = DiscardDecision.Cancel;
        var original = harness.Session.Current;
        await harness.Gate.RequestLoadAsync(_ => Task.FromResult(MakeDocument()));

        Assert.Equal(2, harness.Prompts);
        Assert.Same(original, harness.Session.Current);
    }

    [Fact]
    public async Task ReplacementPending_IsReportedWhileALoadDecodes()
    {
        using var harness = new Harness();
        var gate = new TaskCompletionSource<ImageDocument>();

        var loading = harness.Gate.RequestLoadAsync(async _ => await gate.Task);
        Assert.True(harness.Gate.IsReplacementPending);

        gate.SetResult(MakeDocument());
        await loading;
        Assert.False(harness.Gate.IsReplacementPending);
    }

    [Fact]
    public async Task SupersededLoaderThatIgnoresCancellation_DoesNotBlockEditingAfterTheWinnerLands()
    {
        // Latest-wins: the stuck first loader can outlive the winning load. Pending must follow the
        // session state, not an in-flight request count, or editing stays blocked on a Ready document.
        using var harness = new Harness();
        var stuck = new TaskCompletionSource<ImageDocument>();

        var first = harness.Gate.RequestLoadAsync(async _ => await stuck.Task);
        await harness.Gate.RequestLoadAsync(_ => Task.FromResult(MakeDocument()));

        Assert.Equal(SessionState.Ready, harness.Session.State);
        Assert.False(harness.Gate.IsReplacementPending);

        stuck.SetResult(MakeDocument()); // stale result: disposed unpublished
        await first;
        Assert.False(harness.Gate.IsReplacementPending);
    }

    [Fact]
    public async Task CanCloseWithoutPrompt_TracksTheModifiedFlag()
    {
        using var harness = new Harness();
        Assert.True(harness.Gate.CanCloseWithoutPrompt());

        await harness.ArmAsync();
        Assert.False(harness.Gate.CanCloseWithoutPrompt());

        harness.Editor.Undo();
        Assert.True(harness.Gate.CanCloseWithoutPrompt());
    }
}
