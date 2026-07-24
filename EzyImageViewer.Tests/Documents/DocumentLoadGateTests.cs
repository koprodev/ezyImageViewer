using EzyImageViewer.Core.Commands;
using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using Xunit;

namespace EzyImageViewer.Tests.Documents;

/// <summary>모든 문서 교체 앞의 저장 안 한 편집 보호 검증.</summary>
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

        /// <summary>문서를 열고 수정해 게이트가 작동하는 상태 생성.</summary>
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
        // 승인은 진행 중 로드 범위. 같은 수정 문서의 둘째 요청은 재질문하지 않고 완료 뒤 승인 폐기.
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

        // 승인은 기록만 하고 실제 로드 성공 전에는 아무것도 버리지 않음.
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
        // 저장이 버리기 경로로 새지 않게 안전 차단.
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

        // 대기 로더는 오류 뒤 살아남아 다음 질문에서 부활하면 안 됨.
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
        // 실패 로드보다 오래 산 버리기 승인이 다음 열기 게이트를 우회하던 회귀.
        using var harness = new Harness();
        await harness.ArmAsync();

        await harness.Gate.RequestLoadAsync(_ => Task.FromException<ImageDocument>(new IOException("boom")));
        Assert.Equal(1, harness.Prompts);
        Assert.True(harness.Editor.IsModified);

        // 둘째 열기에도 수정 원본이 남아 다시 질문해야 함.
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
        // 첫 로더가 승자보다 오래 살아도 Pending은 요청 수가 아니라 세션 상태를 따라야 함.
        using var harness = new Harness();
        var stuck = new TaskCompletionSource<ImageDocument>();

        var first = harness.Gate.RequestLoadAsync(async _ => await stuck.Task);
        await harness.Gate.RequestLoadAsync(_ => Task.FromResult(MakeDocument()));

        Assert.Equal(SessionState.Ready, harness.Session.State);
        Assert.False(harness.Gate.IsReplacementPending);

        stuck.SetResult(MakeDocument()); // 묵은 결과라 게시 없이 해제.
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
