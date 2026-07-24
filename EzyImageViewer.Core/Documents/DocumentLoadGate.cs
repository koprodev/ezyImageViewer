namespace EzyImageViewer.Core.Documents;

/// <summary>저장하지 않은 편집 경고의 사용자 선택.</summary>
public enum DiscardDecision
{
    /// <summary>편집을 버리고 진행.</summary>
    Discard,

    /// <summary>교체를 취소하고 현재 문서 유지.</summary>
    Cancel,

    /// <summary>먼저 저장. 대화상자 계층이 성공은 <see cref="Discard"/>, 실패는
    /// <see cref="Cancel"/>로 바꿔 보내므로 게이트에는 도달하지 않음.</summary>
    Save,
}

/// <summary>
/// 저장하지 않은 편집을 모든 문서 교체 경로에서 지키는 공용 게이트.
/// UI 스레드 전용이며 활성화 라우터를 막지 않음.
/// </summary>
public sealed class DocumentLoadGate(
    DocumentSession session,
    DocumentEditor editor,
    Func<Task<DiscardDecision>> confirmDiscardAsync)
{
    private readonly DocumentSession _session = session ?? throw new ArgumentNullException(nameof(session));
    private readonly DocumentEditor _editor = editor ?? throw new ArgumentNullException(nameof(editor));
    private readonly Func<Task<DiscardDecision>> _confirm =
        confirmDiscardAsync ?? throw new ArgumentNullException(nameof(confirmDiscardAsync));

    private bool _promptOpen;
    private Func<CancellationToken, Task<ImageDocument>>? _pending;
    private long _requestSequence;
    private long _activeRequest; // UI 스레드가 아직 완료를 확인하지 못한 최신 요청.

    /// <summary>
    /// 사용자가 버리기로 승인한 문서. 승인한 로드가 끝날 때까지만 최신 요청 묶음에 적용.
    /// 애매하면 다시 묻고, 조용히 날리는 일은 없음.
    /// </summary>
    private Guid _discardApprovedFor;

    public bool IsPrompting => _promptOpen;

    /// <summary>
    /// 교체 질문·디코딩 중이면 true. 이 틈의 편집은 재결합 때 사라지므로 차단.
    /// 최신 요청 세대로만 해제해 오래된 로더가 문을 잠그거나 일찍 열지 못하게 함.
    /// </summary>
    public bool IsReplacementPending => _promptOpen || _activeRequest != 0;

    /// <summary>
    /// 수정 문서는 먼저 확인하고 로드. 질문 중 들어온 요청은 최신 하나로 합침.
    /// 반환 작업은 해당 요청만 추적하며 활성화 경로는 기다리지 않음.
    /// </summary>
    public async Task RequestLoadAsync(Func<CancellationToken, Task<ImageDocument>> loader)
    {
        ArgumentNullException.ThrowIfNull(loader);

        if (_promptOpen)
        {
            _pending = loader;
            return;
        }

        if (!NeedsConfirmation())
        {
            await Start(loader).ConfigureAwait(true);
            return;
        }

        var current = _editor.Document?.Id ?? Guid.Empty;
        _promptOpen = true;
        DiscardDecision decision;
        Func<CancellationToken, Task<ImageDocument>>? queued;
        try
        {
            decision = await _confirm().ConfigureAwait(true);
        }
        finally
        {
            // 질문 상태와 함께 꺼내 예외가 나도 묵은 로더가 부활하지 않게 함.
            _promptOpen = false;
            queued = _pending;
            _pending = null;
        }

        // 명시적 버리기만 진행. 해석 안 된 저장 선택은 망가진 질문이므로 편집 보존.
        if (decision != DiscardDecision.Discard)
            return; // 사용자가 멈췄으니 뒤에 줄 선 요청도 함께 중단.

        // 승인은 이 문서를 이 로드로 바꾸는 한 번에만 유효.
        _discardApprovedFor = current;
        try
        {
            await Start(queued ?? loader).ConfigureAwait(true);
        }
        finally
        {
            _discardApprovedFor = Guid.Empty;
        }
    }

    /// <summary>추가 확인 없이 창을 닫아도 되면 true.</summary>
    public bool CanCloseWithoutPrompt() => !_editor.IsModified;

    private bool NeedsConfirmation()
    {
        if (!_editor.IsModified)
            return false;
        var current = _editor.Document?.Id ?? Guid.Empty;
        return _discardApprovedFor != current;
    }

    private async Task Start(Func<CancellationToken, Task<ImageDocument>> loader)
    {
        var request = ++_requestSequence;
        _activeRequest = request;
        try
        {
            await _session.StartLoadAsync(loader).ConfigureAwait(true);
        }
        finally
        {
            // 최신 요청만 잠금 해제. 늦잠 잔 로더가 문을 먼저 열면 곤란함.
            if (_activeRequest == request)
                _activeRequest = 0;
        }
    }
}
