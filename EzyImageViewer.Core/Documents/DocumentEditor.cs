using EzyImageViewer.Core.Commands;
using EzyImageViewer.Core.Documents.Layers;

namespace EzyImageViewer.Core.Documents;

/// <summary>실행 취소 기록의 개수·메모리 상한.</summary>
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

    /// <summary>실행 취소·다시 실행 스택 전체의 보유 데이터 상한. 통째 픽셀 저장은 입구 컷.</summary>
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
/// 창별 문서 편집 기록. UI 스레드 전용.
/// 저장 상태는 스택 깊이가 아닌 재사용 없는 상태 ID로 추적해 분기 후 거짓 정상 판정을 막음.
/// </summary>
public sealed class DocumentEditor(HistoryLimits? limits = null)
{
    private readonly HistoryLimits _limits = limits ?? HistoryLimits.Default;

    /// <summary>오래된 항목이 앞. 축출은 0번, 탐색은 꼬리부터.</summary>
    private readonly List<Entry> _undo = [];
    private readonly List<Entry> _redo = [];

    private long _nextStateId;
    private long _currentStateId;
    private long _savedStateId;
    private bool _inactiveScopesModified;

    /// <summary>관찰 가능한 변경 뒤 발생. 구독자 예외는 호출자가 처리.</summary>
    public event Action? Changed;

    /// <summary>편집 중인 문서. 수명은 <see cref="DocumentSession"/> 소유.</summary>
    public ImageDocument? Document { get; private set; }

    public DocumentState State { get; private set; } = DocumentState.Empty;

    /// <summary>재결합마다 증가. 문서 교체를 가로지른 제스처가 후임 문서를 건드리지 못하게 함.</summary>
    public long Revision { get; private set; }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>현재 상태가 마지막 저장 상태와 다르면 true. 저장 ID가 축출되면 계속 true.</summary>
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

    /// <summary>새 원본 문서를 결합하고 기록 초기화. 편집 결과 교체에는 사용 금지.</summary>
    public void Reset(ImageDocument? document) => Reset(document, DocumentState.Empty);

    /// <summary>프로젝트의 기존 상태 결합. 기록 없이 해당 상태를 저장점으로 시작.</summary>
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

    /// <summary>명령 적용 후 기록. 명령이 실패하면 상태와 기록 모두 그대로.</summary>
    public void Apply(IEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (Document is null)
            throw new InvalidOperationException("No document is loaded.");

        // 먼저 계산. 실패한 명령이 반쪽짜리 기록을 남기면 안 됨.
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
    /// 드래그 중간값처럼 한 동작인 기록은 최신 항목으로 병합.
    /// 종류·대상·제스처 키가 모두 같을 때만 합쳐 엉뚱한 기록 흡수 방지.
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

        // 같은 동작이라 항목은 교체하되 내용이 달라졌으니 새 상태 ID 부여.
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

    /// <summary>저장 트랜잭션 토큰. 직렬화한 상태와 함께 잡아 <see cref="MarkSaved(long)"/>에 전달.</summary>
    public long CurrentStateId => _currentStateId;

    /// <summary>페이지·프레임 기록 스냅샷. 명령과 상태는 불변이라 안전하게 공유 가능.</summary>
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

    /// <summary>같은 원본 문서를 다른 페이지·프레임 기록에 재결합.</summary>
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

    /// <summary>수정된 비활성 페이지도 교체·저장 보호에 포함.</summary>
    public void SetInactiveScopesModified(bool value)
    {
        if (_inactiveScopesModified == value)
            return;
        _inactiveScopesModified = value;
        Changed?.Invoke();
    }

    /// <summary>현재 상태를 저장 완료로 표시.</summary>
    public void MarkSaved()
    {
        _savedStateId = _currentStateId;
        Changed?.Invoke();
    }

    /// <summary>복구 프로젝트는 저장 경로가 없으므로 사용자가 저장할 때까지 수정 상태 유지.</summary>
    public void MarkRecoveryPendingSave()
    {
        if (Document is null)
            throw new InvalidOperationException("No document is loaded.");
        if (_savedStateId == long.MinValue)
            return;
        _savedStateId = long.MinValue;
        Changed?.Invoke();
    }

    /// <summary>현재 상태가 실제 직렬화한 상태와 같을 때만 저장 완료 처리.</summary>
    public bool MarkSaved(long expectedStateId)
    {
        if (_currentStateId != expectedStateId)
            return false;
        MarkSaved();
        return true;
    }

    /// <summary>두 상한 적용. 과거부터 버리고, 혼자서 상한을 넘는 명령은 기록하지 않음.</summary>
    private void Trim()
    {
        // 혼자서 상한을 넘으면 기록 안 함. 건너뛸 수 없으니 기존 과거도 함께 정리.
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
        /// <summary>입장 때 비용 고정. 뒤늦은 말 바꾸기와 음수 청구는 거절.</summary>
        public static Entry Create(IEditCommand command, long beforeStateId, long afterStateId)
        {
            var bytes = command.EstimatedRetainedBytes;
            ArgumentOutOfRangeException.ThrowIfNegative(bytes, nameof(command));
            return new Entry(command, beforeStateId, afterStateId, bytes);
        }
    }
}
