using EzyImageViewer.Core.Commands;
using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using Xunit;

namespace EzyImageViewer.Tests.Documents;

public class DocumentEditorTests
{
    private static ImageDocument MakeDocument() => new()
    {
        Frame = new DecodedFrame(new byte[16], 2, 2, 8, hasAlpha: false),
        Source = DocumentSource.FromClipboard(),
        NativeSize = new PixelSize(2, 2),
    };

    private static DocumentEditor MakeEditor(HistoryLimits? limits = null)
    {
        var editor = new DocumentEditor(limits);
        editor.Reset(MakeDocument());
        return editor;
    }

    private static RectangleAnnotation Rect(float x = 1, float y = 2, float w = 10, float h = 20) =>
        new() { Id = Guid.NewGuid(), Bounds = new RectF(x, y, w, h) };

    // ---- FR-HIST-001: invertibility ----

    [Fact]
    public void Apply_ThenUndo_RestoresTheExactPreviousState()
    {
        var editor = MakeEditor();
        var before = editor.State;

        editor.Apply(new AddAnnotationCommand(Rect()));
        Assert.Single(editor.State.Annotations);

        Assert.True(editor.Undo());
        Assert.Empty(editor.State.Annotations);
        Assert.Equal(before.Annotations.Count, editor.State.Annotations.Count);
    }

    [Fact]
    public void UndoThenRedo_ReproducesTheAppliedState()
    {
        var editor = MakeEditor();
        var annotation = Rect();

        editor.Apply(new AddAnnotationCommand(annotation));
        editor.Undo();
        Assert.True(editor.Redo());

        Assert.Single(editor.State.Annotations);
        Assert.Equal(annotation.Id, editor.State.Annotations[0].Id);
        Assert.Equal(annotation.Bounds, editor.State.Annotations[0].Bounds);
    }

    [Fact]
    public void Undo_RestoresDeletedObjectAtItsOriginalPaintIndex()
    {
        var editor = MakeEditor();
        var first = Rect(x: 0);
        var middle = Rect(x: 100);
        var last = Rect(x: 200);
        editor.Apply(new AddAnnotationCommand(first));
        editor.Apply(new AddAnnotationCommand(middle));
        editor.Apply(new AddAnnotationCommand(last));

        editor.Apply(new DeleteAnnotationCommand(editor.State, middle.Id));
        Assert.Equal([first.Id, last.Id], editor.State.Annotations.Select(a => a.Id));

        editor.Undo();
        Assert.Equal([first.Id, middle.Id, last.Id], editor.State.Annotations.Select(a => a.Id));
    }

    [Fact]
    public void Undo_OnEmptyHistory_IsANoOp()
    {
        var editor = MakeEditor();
        Assert.False(editor.Undo());
        Assert.False(editor.Redo());
        Assert.False(editor.IsModified);
    }

    [Fact]
    public void NewEdit_DiscardsTheRedoBranch()
    {
        var editor = MakeEditor();
        editor.Apply(new AddAnnotationCommand(Rect()));
        editor.Undo();
        Assert.True(editor.CanRedo);

        editor.Apply(new AddAnnotationCommand(Rect(x: 50)));

        Assert.False(editor.CanRedo);
        Assert.Single(editor.State.Annotations);
    }

    [Fact]
    public void FailingCommand_LeavesStateAndHistoryUntouched()
    {
        var editor = MakeEditor();
        var annotation = Rect();
        editor.Apply(new AddAnnotationCommand(annotation));

        // Deleting an id that is not on the layer throws while building the command's inverse.
        Assert.Throws<InvalidOperationException>(() =>
            editor.Apply(new DeleteAnnotationCommand(editor.State, Guid.NewGuid())));

        Assert.Single(editor.State.Annotations);
        Assert.True(editor.CanUndo);
        Assert.False(editor.CanRedo);
    }

    // ---- FR-HIST-004: modified state ----

    [Fact]
    public void Reset_StartsClean_AndAnEditMarksModified()
    {
        var editor = MakeEditor();
        Assert.False(editor.IsModified);

        editor.Apply(new AddAnnotationCommand(Rect()));

        Assert.True(editor.IsModified);
    }

    [Fact]
    public void UndoBackToTheSavedState_ReadsCleanAgain()
    {
        var editor = MakeEditor();
        editor.Apply(new AddAnnotationCommand(Rect()));
        Assert.True(editor.IsModified);

        editor.Undo();

        Assert.False(editor.IsModified);
    }

    [Fact]
    public void DoUndoRedo_ReturnsToTheSameSavedState_NotJustTheSameDepth()
    {
        var editor = MakeEditor();
        editor.Apply(new AddAnnotationCommand(Rect()));
        editor.MarkSaved();

        editor.Undo();
        Assert.True(editor.IsModified);

        editor.Redo();

        // State ids are carried by the entries, not minted per traversal: redo lands back on the
        // saved state itself.
        Assert.False(editor.IsModified);
    }

    [Fact]
    public void UndoThenBranch_IsModified_EvenThoughTheDepthMatchesTheSavepoint()
    {
        var editor = MakeEditor();
        editor.Apply(new AddAnnotationCommand(Rect(x: 1)));
        editor.Apply(new AddAnnotationCommand(Rect(x: 2)));
        editor.MarkSaved();

        editor.Undo();
        editor.Apply(new AddAnnotationCommand(Rect(x: 999))); // same depth as the savepoint, other content

        Assert.True(editor.IsModified);
    }

    [Fact]
    public void MarkSaved_ClearsModified()
    {
        var editor = MakeEditor();
        editor.Apply(new AddAnnotationCommand(Rect()));

        editor.MarkSaved();

        Assert.False(editor.IsModified);
    }

    [Fact]
    public void RecoveredState_RemainsModifiedUntilExplicitlySaved()
    {
        var editor = MakeEditor();
        Assert.False(editor.IsModified);

        editor.MarkRecoveryPendingSave();

        Assert.True(editor.IsModified);
        Assert.False(editor.CanUndo);
        editor.MarkSaved();
        Assert.False(editor.IsModified);
    }

    [Fact]
    public void MarkSavedWithToken_ClearsModified_WhenTheStateIsStillTheWrittenOne()
    {
        var editor = MakeEditor();
        editor.Apply(new AddAnnotationCommand(Rect()));
        var token = editor.CurrentStateId;

        Assert.True(editor.MarkSaved(token));
        Assert.False(editor.IsModified);
    }

    [Fact]
    public void MarkSavedWithToken_SkipsWhenAnEditLandedDuringTheWrite()
    {
        var editor = MakeEditor();
        editor.Apply(new AddAnnotationCommand(Rect(x: 1)));
        var token = editor.CurrentStateId; // captured with the state a save serialized

        editor.Apply(new AddAnnotationCommand(Rect(x: 2))); // lands while the write is in flight

        // The file on disk holds the captured state, not the current one — stays modified.
        Assert.False(editor.MarkSaved(token));
        Assert.True(editor.IsModified);
    }

    [Fact]
    public void MarkSavedWithToken_NeverCrossesARebind()
    {
        var editor = MakeEditor();
        editor.Apply(new AddAnnotationCommand(Rect()));
        var token = editor.CurrentStateId;

        editor.Reset(MakeDocument()); // replacement finished while the write was in flight

        // Ids are monotonic across resets: the stale token cannot alias the successor's state.
        Assert.False(editor.MarkSaved(token));
        Assert.False(editor.IsModified);
    }

    [Fact]
    public void Reset_DropsHistoryAndModifiedState()
    {
        var editor = MakeEditor();
        editor.Apply(new AddAnnotationCommand(Rect()));

        editor.Reset(MakeDocument());

        Assert.False(editor.IsModified);
        Assert.False(editor.CanUndo);
        Assert.False(editor.CanRedo);
        Assert.Empty(editor.State.Annotations);
    }

    [Fact]
    public void WithNoDocument_NothingIsModifiedAndEditsAreRejected()
    {
        var editor = new DocumentEditor();

        Assert.False(editor.IsModified);
        Assert.Throws<InvalidOperationException>(() => editor.Apply(new AddAnnotationCommand(Rect())));
    }

    // ---- FR-HIST-002: dual cap ----

    [Fact]
    public void EntryCap_EvictsTheOldestUndoEntryFirst()
    {
        var editor = MakeEditor(new HistoryLimits { MaxEntries = 2 });
        var first = Rect(x: 1);
        editor.Apply(new AddAnnotationCommand(first));
        editor.Apply(new AddAnnotationCommand(Rect(x: 2)));
        editor.Apply(new AddAnnotationCommand(Rect(x: 3)));

        // Three edits, two entries retained: the oldest add is no longer undoable.
        Assert.True(editor.Undo());
        Assert.True(editor.Undo());
        Assert.False(editor.Undo());
        Assert.Single(editor.State.Annotations);
        Assert.Equal(first.Id, editor.State.Annotations[0].Id);
    }

    [Fact]
    public void ByteCap_CountsUndoAndRedoTogether()
    {
        // Three entries fit the count cap but not the byte cap (48B each).
        var editor = MakeEditor(new HistoryLimits { MaxEntries = 100, MaxRetainedBytes = 100 });
        editor.Apply(new AddAnnotationCommand(Rect(x: 1)));
        editor.Apply(new AddAnnotationCommand(Rect(x: 2)));
        editor.Apply(new AddAnnotationCommand(Rect(x: 3)));

        Assert.True(editor.RetainedBytes <= 100);

        // Undo migrates payload from the undo stack to the redo stack; the total must not grow.
        editor.Undo();
        editor.Undo();

        Assert.True(editor.RetainedBytes <= 100, $"retained {editor.RetainedBytes} across both stacks");
    }

    [Fact]
    public void EvictedSavepoint_LeavesTheDocumentPermanentlyModified()
    {
        var editor = MakeEditor(new HistoryLimits { MaxEntries = 1 });
        // Saved at the initial state, then two edits: the entry that could return there is evicted.
        editor.Apply(new AddAnnotationCommand(Rect(x: 1)));
        editor.Apply(new AddAnnotationCommand(Rect(x: 2)));

        while (editor.Undo())
        {
        }

        Assert.True(editor.IsModified);
    }

    [Fact]
    public void CommandLargerThanTheByteCap_AppliesButDropsTheHistory()
    {
        var editor = MakeEditor(new HistoryLimits { MaxRetainedBytes = 8 });
        editor.Apply(new AddAnnotationCommand(Rect(x: 1)));

        // Undo cannot skip an unrecorded edit, so the recorded past goes with it.
        Assert.Single(editor.State.Annotations);
        Assert.False(editor.CanUndo);
        Assert.True(editor.IsModified);
    }

    // ---- §7.8: drag coalescing ----

    [Fact]
    public void CoalescedDrag_IsOneUndoEntryThatReturnsToTheDragStart()
    {
        var editor = MakeEditor();
        var annotation = Rect(x: 0, y: 0, w: 10, h: 10);
        editor.Apply(new AddAnnotationCommand(annotation));
        var origin = annotation.Bounds;

        editor.Apply(new MoveAnnotationCommand(annotation.Id, origin, origin.Translated(5, 5), gestureId: 1));
        editor.ApplyCoalesced(new MoveAnnotationCommand(annotation.Id, origin, origin.Translated(20, 20), gestureId: 1));
        editor.ApplyCoalesced(new MoveAnnotationCommand(annotation.Id, origin, origin.Translated(40, 30), gestureId: 1));

        Assert.Equal(origin.Translated(40, 30), editor.State.Annotations[0].Bounds);

        Assert.True(editor.Undo());
        Assert.Equal(origin, editor.State.Annotations[0].Bounds);
        Assert.Single(editor.State.Annotations); // the add survives: only one entry was consumed
    }

    [Fact]
    public void CoalescedDrag_RedoReappliesTheFinalPosition()
    {
        var editor = MakeEditor();
        var annotation = Rect(x: 0, y: 0, w: 10, h: 10);
        editor.Apply(new AddAnnotationCommand(annotation));
        var origin = annotation.Bounds;

        editor.Apply(new MoveAnnotationCommand(annotation.Id, origin, origin.Translated(5, 5), gestureId: 1));
        editor.ApplyCoalesced(new MoveAnnotationCommand(annotation.Id, origin, origin.Translated(40, 30), gestureId: 1));
        editor.Undo();
        editor.Redo();

        Assert.Equal(origin.Translated(40, 30), editor.State.Annotations[0].Bounds);
    }

    [Fact]
    public void ApplyCoalesced_WithNoPriorEntry_BehavesAsApply()
    {
        var editor = MakeEditor();
        var annotation = Rect();

        editor.ApplyCoalesced(new AddAnnotationCommand(annotation));

        Assert.Single(editor.State.Annotations);
        Assert.True(editor.CanUndo);
    }

    [Fact]
    public void ApplyCoalesced_OverAnUnrelatedEntry_StacksInsteadOfFoldingItAway()
    {
        // Regression for the M2 shape of ApplyCoalesced, which replaced the newest entry
        // unconditionally: an unrelated command must never be rewritten by a merge.
        var editor = MakeEditor();
        var annotation = Rect(x: 0, y: 0, w: 10, h: 10);
        editor.Apply(new AddAnnotationCommand(annotation));

        editor.ApplyCoalesced(new MoveAnnotationCommand(
            annotation.Id, annotation.Bounds, annotation.Bounds.Translated(5, 5), gestureId: 1));

        // Two entries: undoing the move must not undo the add with it.
        editor.Undo();
        Assert.Single(editor.State.Annotations);
        Assert.Equal(annotation.Bounds, editor.State.Annotations[0].Bounds);
    }

    // ---- WP2 hardening ----

    [Fact]
    public void HistoryLimits_RejectNonPositiveValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryLimits { MaxEntries = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryLimits { MaxEntries = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryLimits { MaxRetainedBytes = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new HistoryLimits { MaxRetainedBytes = -8 });
    }

    [Fact]
    public void CommandClaimingNegativeBytes_IsRejectedWithoutMutating()
    {
        var editor = MakeEditor();

        Assert.Throws<ArgumentOutOfRangeException>(() => editor.Apply(new LyingCommand()));

        Assert.Empty(editor.State.Annotations);
        Assert.False(editor.CanUndo);
        Assert.False(editor.IsModified);
    }

    [Fact]
    public void ByteAccounting_IsCapturedAtAdmission_NotReReadFromTheCommand()
    {
        var editor = MakeEditor(new HistoryLimits { MaxEntries = 10, MaxRetainedBytes = 100 });
        var mutable = new MutableBytesCommand { Bytes = 10 };
        editor.Apply(mutable);

        // The command inflates its claim after admission; the recorded 10 bytes must still govern.
        mutable.Bytes = long.MaxValue;

        Assert.Equal(10, editor.RetainedBytes);
    }

    [Fact]
    public void Revision_BumpsOnRebindOnly()
    {
        var editor = MakeEditor();
        var bound = editor.Revision;

        editor.Apply(new AddAnnotationCommand(Rect()));
        editor.Undo();
        editor.Redo();
        editor.MarkSaved();
        Assert.Equal(bound, editor.Revision);

        editor.Reset(MakeDocument());
        Assert.Equal(bound + 1, editor.Revision);
    }

    private sealed class LyingCommand : IEditCommand
    {
        public string Name => "Lying";
        public long EstimatedRetainedBytes => -1;
        public object? MergeKey => null;
        public DocumentState Apply(DocumentState state) => state;
        public DocumentState Revert(DocumentState state) => state;
    }

    private sealed class MutableBytesCommand : IEditCommand
    {
        public long Bytes { get; set; }
        public string Name => "MutableBytes";
        public long EstimatedRetainedBytes => Bytes;
        public object? MergeKey => null;
        public DocumentState Apply(DocumentState state) => state;
        public DocumentState Revert(DocumentState state) => state;
    }

    [Fact]
    public void PageSnapshot_RestoresStateUndoAndDirtyStatus()
    {
        var editor = MakeEditor();
        var document = Assert.IsType<ImageDocument>(editor.Document);
        editor.Apply(new AddAnnotationCommand(Rect()));
        var pageOne = editor.CaptureSnapshot();

        editor.Reset(document);
        editor.SetInactiveScopesModified(pageOne.IsModified);
        Assert.True(editor.IsModified);
        Assert.Empty(editor.State.Annotations);

        var pageTwo = editor.CaptureSnapshot();
        editor.RestoreSnapshot(document, pageOne);
        editor.SetInactiveScopesModified(pageTwo.IsModified);

        Assert.Single(editor.State.Annotations);
        Assert.True(editor.CanUndo);
        Assert.True(editor.Undo());
        Assert.Empty(editor.State.Annotations);
    }

    [Fact]
    public void SavedPageSnapshots_ClearAggregateDirtyGuard()
    {
        var editor = MakeEditor();
        editor.Apply(new AddAnnotationCommand(Rect()));
        var savedPage = editor.CaptureSnapshot().AsSaved();

        editor.SetInactiveScopesModified(savedPage.IsModified);
        editor.MarkSaved();

        Assert.False(editor.IsModified);
    }

    [Fact]
    public void PageSnapshot_WithoutHistoryPreservesStateAndDirtyStatus()
    {
        var editor = MakeEditor();
        editor.Apply(new AddAnnotationCommand(Rect()));

        var compacted = editor.CaptureSnapshot().WithoutHistory();

        Assert.Single(compacted.State.Annotations);
        Assert.True(compacted.IsModified);
        Assert.Equal(0, compacted.RetainedBytes);
    }

    // ---- notification ----

    [Fact]
    public void EveryMutation_RaisesChanged()
    {
        var editor = MakeEditor();
        var count = 0;
        editor.Changed += () => count++;

        editor.Apply(new AddAnnotationCommand(Rect()));
        editor.Undo();
        editor.Redo();
        editor.MarkSaved();
        editor.Reset(MakeDocument());

        Assert.Equal(5, count);
    }
}
