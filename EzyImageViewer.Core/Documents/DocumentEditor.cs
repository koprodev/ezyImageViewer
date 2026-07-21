using EzyImageViewer.Core.Commands;
using EzyImageViewer.Core.Documents.Layers;

namespace EzyImageViewer.Core.Documents;

/// <summary>History bounds (FR-HIST-002 offers count or memory — both are enforced).</summary>
public sealed record HistoryLimits
{
    public static HistoryLimits Default { get; } = new();

    private readonly int _maxEntries = 100;
    private readonly long _maxRetainedBytes = 64L * 1024 * 1024;

    public int MaxEntries
    {
        get => _maxEntries;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxEntries = value;
        }
    }

    /// <summary>
    /// Retained payload ceiling across the undo AND redo stacks. Small because commands store
    /// geometry, not pixels; a single decoded frame is 192MB at the display budget and could never
    /// fit here — which is what keeps whole-frame preimages out without a separate ban (ADR-0008).
    /// </summary>
    public long MaxRetainedBytes
    {
        get => _maxRetainedBytes;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _maxRetainedBytes = value;
        }
    }
}

/// <summary>
/// Per-window edit history over one document (§6.3 EditHistory). Sits beside <see cref="DocumentSession"/>
/// rather than inside it: the session owns the load lifecycle (worker threads, latest-wins, gated
/// swap), while editing is UI-thread-affine and must not disturb zoom/pan or cancel a load.
///
/// Threading: single-threaded — construct, mutate and read on the UI thread only.
///
/// Saved-state tracking is by state id, never by stack depth: undoing and then branching returns to
/// the same depth with different content, which a depth savepoint would read as clean. State ids
/// are monotonic and never reused, so once the saved id is evicted out of reach no reachable state
/// can ever equal it again — eviction alone makes <see cref="IsModified"/> permanently true, with
/// no separate reachability flag to keep in sync.
/// </summary>
public sealed class DocumentEditor(HistoryLimits? limits = null)
{
    private readonly HistoryLimits _limits = limits ?? HistoryLimits.Default;

    /// <summary>Oldest first: eviction takes index 0, traversal takes the tail.</summary>
    private readonly List<Entry> _undo = [];
    private readonly List<Entry> _redo = [];

    private long _nextStateId;
    private long _currentStateId;
    private long _savedStateId;
    private bool _inactiveScopesModified;

    /// <summary>Raised after any observable change. Subscriber exceptions are the caller's problem
    /// (unlike the session, this runs on the UI thread with no load to protect).</summary>
    public event Action? Changed;

    /// <summary>The document being edited. Not owned — <see cref="DocumentSession"/> disposes it.</summary>
    public ImageDocument? Document { get; private set; }

    public DocumentState State { get; private set; } = DocumentState.Empty;

    /// <summary>
    /// Bumped on every <see cref="Reset"/> (rebind). A UI gesture captures this with the document id
    /// when it starts and re-validates before mutating, so an interaction that straddles a document
    /// replacement dies instead of committing into the successor. Distinct from state ids, which
    /// track history positions within one binding.
    /// </summary>
    public long Revision { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>FR-HIST-004. True while the current state differs from the last saved one — and
    /// permanently once the saved state id has been evicted out of reach (ids are never reused).</summary>
    public bool IsModified => Document is not null
        && (_currentStateId != _savedStateId || _inactiveScopesModified);

    public long RetainedBytes
    {
        get
        {
            long total = 0;
            foreach (var entry in _undo)
                total = checked(total + entry.RetainedBytes);
            foreach (var entry in _redo)
                total = checked(total + entry.RetainedBytes);
            return total;
        }
    }

    public long MaxRetainedBytes => _limits.MaxRetainedBytes;

    /// <summary>
    /// Binds a newly loaded source document and drops all history. Called only for a *source* load —
    /// never for a document produced by an edit, whose history must outlive the instance swap.
    /// </summary>
    public void Reset(ImageDocument? document) => Reset(document, DocumentState.Empty);

    /// <summary>Binds with a pre-existing state (project open, FR-OUT-009). The seeded state arrives
    /// clean: no history and saved at exactly this state.</summary>
    public void Reset(ImageDocument? document, DocumentState initialState)
    {
        ArgumentNullException.ThrowIfNull(initialState);
        Document = document;
        State = initialState;
        _undo.Clear();
        _redo.Clear();
        _currentStateId = checked(++_nextStateId);
        _savedStateId = _currentStateId;
        _inactiveScopesModified = false;
        Revision = checked(Revision + 1);
        Changed?.Invoke();
    }

    /// <summary>
    /// Applies a command and records it. Throws only if the command itself throws, in which case
    /// state and history are both unchanged.
    /// </summary>
    public void Apply(IEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Document is null)
            throw new InvalidOperationException("No document is loaded.");

        // Compute first: a throwing command must not leave a half-recorded history.
        var next = command.Apply(State);
        var entry = Entry.Create(command, _currentStateId, checked(++_nextStateId));

        State = next;
        _currentStateId = entry.AfterStateId;
        _redo.Clear();
        _undo.Add(entry);
        Trim();
        Changed?.Invoke();
    }

    /// <summary>
    /// Replaces the newest entry instead of stacking a new one — for a drag that reports many
    /// intermediate positions but is one user action (§7.8). Coalescing requires matching non-null
    /// merge keys (same kind, target and gesture); anything else stacks normally, so a caller can
    /// never fold an unrelated entry away by accident.
    /// </summary>
    public void ApplyCoalesced(IEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Document is null)
            throw new InvalidOperationException("No document is loaded.");

        var key = command.MergeKey;
        if (_undo.Count == 0 || key is null || !key.Equals(_undo[^1].Command.MergeKey))
        {
            Apply(command);
            return;
        }

        var previous = _undo[^1];
        var reverted = previous.Command.Revert(State);
        var next = command.Apply(reverted);

        // Same user action, so the entry is replaced rather than stacked — but the content differs,
        // so the resulting state earns its own id (a savepoint must never alias a refined state).
        var entry = Entry.Create(command, previous.BeforeStateId, checked(++_nextStateId));
        State = next;
        _undo[^1] = entry;
        _currentStateId = entry.AfterStateId;
        _redo.Clear();
        Trim();
        Changed?.Invoke();
    }

    public bool Undo()
    {
        if (_undo.Count == 0)
            return false;

        var entry = _undo[^1];
        State = entry.Command.Revert(State);
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(entry);
        _currentStateId = entry.BeforeStateId;
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0)
            return false;

        var entry = _redo[^1];
        State = entry.Command.Apply(State);
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(entry);
        _currentStateId = entry.AfterStateId;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Save-transaction token: capture with the state a write serializes, then pass to
    /// <see cref="MarkSaved(long)"/>. Ids are monotonic across resets, so a token from a replaced
    /// document can never alias the successor's state.</summary>
    public long CurrentStateId => _currentStateId;

    /// <summary>Opaque page/frame history snapshot. Commands and state are immutable and can be shared safely.</summary>
    public sealed class Snapshot
    {
        internal readonly Entry[] UndoEntries;
        internal readonly Entry[] RedoEntries;

        internal Snapshot(
            DocumentState state,
            Entry[] undo,
            Entry[] redo,
            long currentStateId,
            long savedStateId)
        {
            State = state;
            UndoEntries = undo;
            RedoEntries = redo;
            CurrentStateId = currentStateId;
            SavedStateId = savedStateId;
        }

        public DocumentState State { get; }
        public long CurrentStateId { get; }
        public long SavedStateId { get; }
        public bool IsModified => CurrentStateId != SavedStateId;
        public long RetainedBytes => UndoEntries.Sum(entry => entry.RetainedBytes)
            + RedoEntries.Sum(entry => entry.RetainedBytes);

        public Snapshot AsSaved() => new(
            State,
            [.. UndoEntries],
            [.. RedoEntries],
            CurrentStateId,
            CurrentStateId);

        public Snapshot WithoutHistory() => new(
            State,
            [],
            [],
            CurrentStateId,
            SavedStateId);
    }

    public Snapshot CaptureSnapshot() => new(
        State,
        [.. _undo],
        [.. _redo],
        _currentStateId,
        _savedStateId);

    public Snapshot CreateCleanSnapshot(DocumentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var stateId = checked(++_nextStateId);
        return new Snapshot(state, [], [], stateId, stateId);
    }

    /// <summary>Rebinds the same source document to another page/frame history.</summary>
    public void RestoreSnapshot(ImageDocument document, Snapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(snapshot);
        Document = document;
        State = snapshot.State;
        _undo.Clear();
        _undo.AddRange(snapshot.UndoEntries);
        _redo.Clear();
        _redo.AddRange(snapshot.RedoEntries);
        _currentStateId = snapshot.CurrentStateId;
        _savedStateId = snapshot.SavedStateId;
        _nextStateId = Math.Max(_nextStateId, Math.Max(_currentStateId, _savedStateId));
        Revision = checked(Revision + 1);
        Changed?.Invoke();
    }

    /// <summary>Includes dirty inactive pages in the replacement/save guard.</summary>
    public void SetInactiveScopesModified(bool value)
    {
        if (_inactiveScopesModified == value)
            return;
        _inactiveScopesModified = value;
        Changed?.Invoke();
    }

    /// <summary>Marks the current state as persisted (FR-HIST-004 clears on save).</summary>
    public void MarkSaved()
    {
        _savedStateId = _currentStateId;
        Changed?.Invoke();
    }

    /// <summary>A crash-recovered project has no durable user-selected save target. Preserve its
    /// restored state but force the normal modified/close/save contract until the user saves it.</summary>
    public void MarkRecoveryPendingSave()
    {
        if (Document is null)
            throw new InvalidOperationException("No document is loaded.");
        if (_savedStateId == long.MinValue)
            return;
        _savedStateId = long.MinValue;
        Changed?.Invoke();
    }

    /// <summary>Marks saved only if the current state is still the one the write serialized.
    /// An edit or rebind that landed during the write keeps the document modified — the file on
    /// disk holds the captured state, not this one.</summary>
    public bool MarkSaved(long expectedStateId)
    {
        if (_currentStateId != expectedStateId)
            return false;
        MarkSaved();
        return true;
    }

    /// <summary>
    /// Enforces both caps. Oldest undo entries go first; the distant future goes only after the
    /// whole past is gone. An entry too large to ever fit is not recorded at all — and since undo
    /// cannot skip an unrecorded edit, the past is dropped with it.
    /// </summary>
    private void Trim()
    {
        // An entry that could never fit is not recorded at all. Undo cannot skip an unrecorded
        // edit, so the recorded past is dropped with it rather than left inconsistent.
        if (_undo.Count > 0 && _undo[^1].RetainedBytes > _limits.MaxRetainedBytes)
        {
            _undo.Clear();
            _redo.Clear();
            return;
        }

        while (_undo.Count + _redo.Count > _limits.MaxEntries || RetainedBytes > _limits.MaxRetainedBytes)
        {
            if (_undo.Count > 0)
                _undo.RemoveAt(0);
            else if (_redo.Count > 0)
                _redo.RemoveAt(0);
            else
                break;
        }
    }

    internal readonly record struct Entry(IEditCommand Command, long BeforeStateId, long AfterStateId, long RetainedBytes)
    {
        /// <summary>Byte cost is captured once here: a mutable or lying command cannot skew the
        /// budget after admission, and a negative claim is rejected outright.</summary>
        public static Entry Create(IEditCommand command, long beforeStateId, long afterStateId)
        {
            var bytes = command.EstimatedRetainedBytes;
            ArgumentOutOfRangeException.ThrowIfNegative(bytes, nameof(command));
            return new Entry(command, beforeStateId, afterStateId, bytes);
        }
    }
}
