namespace EzyImageViewer.Core.Documents;

/// <summary>What the user chose when warned about unsaved edits (FR-HIST-005).</summary>
public enum DiscardDecision
{
    /// <summary>Proceed and lose the edits.</summary>
    Discard,

    /// <summary>Abandon the replacement; the edited document stays.</summary>
    Cancel,

    /// <summary>Persist first, then proceed. The dialog layer resolves this before answering the
    /// gate: a successful save comes back as <see cref="Discard"/>, a failed one as
    /// <see cref="Cancel"/> — so the gate itself never sees this value.</summary>
    Save,
}

/// <summary>
/// Guards every document replacement against unsaved edits (FR-HIST-005). Lives here rather than in
/// the window because the policy — not the dialog — is what needs testing, and because the prompt is
/// injected, the same policy serves the toolbar, drag-drop, clipboard, folder navigation and the
/// single-instance activation redirect, which is the only ingress the user does not initiate.
///
/// Threading: UI-thread-affine, like <see cref="DocumentEditor"/>.
///
/// Non-blocking by construction: <see cref="RequestLoadAsync"/> is awaited by nobody on the
/// activation path, so a prompt can never stall the router queue.
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
    private long _activeRequest; // latest request whose settlement the UI thread has not yet observed

    /// <summary>
    /// Document the user agreed to discard. The approval covers the latest-wins batch of requests
    /// that start while the approved load is still decoding (they replace the same edits the user
    /// already gave up); it is cleared when the approved load settles, so anything after a failed
    /// or superseded decode finds the edits alive and re-prompts. Over-prompting is the accepted
    /// failure direction — silent loss never is.
    /// </summary>
    private Guid _discardApprovedFor;

    public bool IsPrompting => _promptOpen;

    /// <summary>
    /// True while a replacement is prompted for or decoding. Editing is disallowed here: the swap
    /// lands without re-consulting the guard, so an edit made now would be destroyed unasked.
    /// Keyed on the LATEST request's generation, cleared by its own UI-thread continuation — not on
    /// a raw request count (a superseded loader that ignores its token would block a Ready document
    /// forever), and not on the session state (the worker flips it to Ready before the UI thread has
    /// rebound the editor, and an edit slipped into that gap would be destroyed by the rebind).
    /// </summary>
    public bool IsReplacementPending => _promptOpen || _activeRequest != 0;

    /// <summary>
    /// Starts a load, prompting first if the current document has unsaved edits. Requests arriving
    /// while a prompt is open collapse to the newest one (a prompt storm answers about a file the
    /// user has already moved past).
    /// The returned task settles when this request's load settles — or immediately when the request
    /// was queued behind an open prompt (its fate then belongs to the prompting call). Callers on
    /// the activation path discard it, which is what keeps the router non-blocking.
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
            // Captured with the prompt flag so a throwing confirm cannot leave a stale loader
            // latched for a later prompt to resurrect; the fault itself propagates fail-closed
            // (no load started, edits kept).
            _promptOpen = false;
            queued = _pending;
            _pending = null;
        }

        // Fail-closed: only an explicit Discard proceeds. Save never reaches here — the dialog
        // layer resolves it into Discard (write succeeded) or Cancel (write failed or refused),
        // so an unresolved Save could only mean a broken prompt, and it must not destroy edits.
        if (decision != DiscardDecision.Discard)
            return; // Also drops what queued behind the prompt — the user said stop.

        // Approval authorizes replacing *this* document with *this* load and nothing more: it is
        // scoped to the load and cleared when the load settles, so a failed or superseded decode
        // leaves the edits intact and still guarded.
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

    /// <summary>True when the window may close without asking (FR-HIST-005).</summary>
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
            // Only the latest request may clear the flag: a stale loader resuming later must not
            // reopen editing while a newer replacement is still pending.
            if (_activeRequest == request)
                _activeRequest = 0;
        }
    }
}
