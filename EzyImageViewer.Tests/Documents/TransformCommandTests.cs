using EzyImageViewer.Core.Commands;
using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using Xunit;

namespace EzyImageViewer.Tests.Documents;

/// <summary>Transform edits ride the same command stack as annotations (FR-HIST-001), with the
/// structured merge key guarding coalescing (§7.8).</summary>
public class TransformCommandTests
{
    private static ImageDocument MakeDocument() => new()
    {
        Frame = new DecodedFrame(new byte[16], 2, 2, 8, hasAlpha: false),
        Source = DocumentSource.FromClipboard(),
        NativeSize = new PixelSize(2, 2),
    };

    private static DocumentEditor MakeEditor()
    {
        var editor = new DocumentEditor();
        editor.Reset(MakeDocument());
        return editor;
    }

    private static TransformCommand Rotate(BackgroundTransform before, float degrees, long gestureId = 0) =>
        new(TransformEditKind.Rotate, before, before.Append(new RotateOp(degrees)), gestureId);

    [Fact]
    public void ApplyThenUndo_RestoresTheIdentityTransform()
    {
        var editor = MakeEditor();

        editor.Apply(Rotate(editor.State.Transform, 90f));
        Assert.Single(editor.State.Transform.Ops);
        Assert.True(editor.IsModified);

        editor.Undo();
        Assert.True(editor.State.Transform.IsIdentity);
        Assert.False(editor.IsModified);
    }

    [Fact]
    public void UndoThenRedo_ReproducesThePipeline()
    {
        var editor = MakeEditor();
        editor.Apply(Rotate(editor.State.Transform, 90f));
        var applied = editor.State.Transform;

        editor.Undo();
        editor.Redo();

        Assert.Equal(applied, editor.State.Transform);
    }

    [Fact]
    public void CommandAgainstTheWrongState_ThrowsAndLeavesEverythingUntouched()
    {
        var editor = MakeEditor();
        var againstIdentity = Rotate(BackgroundTransform.Identity, 90f);
        editor.Apply(againstIdentity);

        // The same before-state no longer matches: the pipeline moved on.
        Assert.Throws<InvalidOperationException>(() => editor.Apply(Rotate(BackgroundTransform.Identity, 180f)));

        Assert.Single(editor.State.Transform.Ops);
        Assert.True(editor.CanUndo);
        Assert.False(editor.CanRedo);
    }

    [Fact]
    public void SameGesture_Coalesces_IntoOneEntry()
    {
        var editor = MakeEditor();
        var identity = editor.State.Transform;

        editor.Apply(Rotate(identity, 10f, gestureId: 7));
        editor.ApplyCoalesced(Rotate(identity, 25f, gestureId: 7));

        Assert.Equal(25f, Assert.IsType<RotateOp>(Assert.Single(editor.State.Transform.Ops)).Degrees);
        editor.Undo();
        Assert.True(editor.State.Transform.IsIdentity);
        Assert.False(editor.CanUndo); // one entry, not two
    }

    [Fact]
    public void DifferentGesture_StacksInsteadOfCoalescing()
    {
        var editor = MakeEditor();
        var identity = editor.State.Transform;

        editor.Apply(Rotate(identity, 10f, gestureId: 7));
        editor.ApplyCoalesced(Rotate(editor.State.Transform, 25f, gestureId: 8));

        Assert.Equal(2, editor.State.Transform.Ops.Count);
        editor.Undo();
        Assert.Single(editor.State.Transform.Ops); // lands mid-way, not at identity
    }

    [Fact]
    public void DifferentKind_StacksEvenWithinTheSameGesture()
    {
        var editor = MakeEditor();
        editor.Apply(Rotate(editor.State.Transform, 90f, gestureId: 7));
        var afterRotate = editor.State.Transform;

        editor.ApplyCoalesced(new TransformCommand(
            TransformEditKind.Flip, afterRotate, afterRotate.Append(new FlipOp(true)), gestureId: 7));

        Assert.Equal(2, editor.State.Transform.Ops.Count);
    }

    [Fact]
    public void ZeroGestureId_NeverCoalesces()
    {
        var editor = MakeEditor();
        editor.Apply(Rotate(editor.State.Transform, 10f));
        editor.ApplyCoalesced(Rotate(editor.State.Transform, 20f));

        Assert.Equal(2, editor.State.Transform.Ops.Count);
    }

    [Fact]
    public void MoveDrags_FromDifferentGestures_DoNotMerge()
    {
        var editor = MakeEditor();
        var annotation = new RectangleAnnotation { Id = Guid.NewGuid(), Bounds = new RectF(0, 0, 10, 10) };
        editor.Apply(new AddAnnotationCommand(annotation));
        var origin = annotation.Bounds;

        editor.Apply(new MoveAnnotationCommand(annotation.Id, origin, origin.Translated(5, 5), gestureId: 1));
        editor.ApplyCoalesced(new MoveAnnotationCommand(
            annotation.Id, origin.Translated(5, 5), origin.Translated(9, 9), gestureId: 2));

        // Two drags = two entries: undo returns to the first drag's end, not to the origin.
        editor.Undo();
        Assert.Equal(origin.Translated(5, 5), editor.State.Annotations[0].Bounds);
    }

    [Fact]
    public void EstimatedBytes_GrowWithThePipeline_ButStayTrivial()
    {
        var transform = BackgroundTransform.Identity.Append(new RotateOp(90f)).Append(new FlipOp(true));
        var command = new TransformCommand(TransformEditKind.Flip, BackgroundTransform.Identity, transform);

        Assert.InRange(command.EstimatedRetainedBytes, 1, 4096);
    }
}
