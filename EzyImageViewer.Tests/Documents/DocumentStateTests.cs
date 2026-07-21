using EzyImageViewer.Core.Commands;
using EzyImageViewer.Core.Documents.Layers;
using Xunit;

namespace EzyImageViewer.Tests.Documents;

public class DocumentStateTests
{
    private static RectangleAnnotation Rect(float x = 0, float y = 0, float w = 10, float h = 10) =>
        new() { Id = Guid.NewGuid(), Bounds = new RectF(x, y, w, h) };

    [Fact]
    public void Mutations_ReturnNewStates_AndLeaveTheOriginalIntact()
    {
        var original = DocumentState.Empty;

        var next = original.AddAnnotation(Rect());

        Assert.Empty(original.Annotations);
        Assert.Single(next.Annotations);
    }

    [Fact]
    public void FromCorners_NormalizesADragInAnyDirection()
    {
        var forward = RectF.FromCorners(10, 10, 40, 30);
        var backward = RectF.FromCorners(40, 30, 10, 10);

        Assert.Equal(forward, backward);
        Assert.Equal(new RectF(10, 10, 30, 20), forward);
    }

    [Fact]
    public void HitTest_ReturnsTheTopmostObject()
    {
        var bottom = Rect(x: 0, y: 0, w: 100, h: 100);
        var top = Rect(x: 10, y: 10, w: 20, h: 20);
        var state = DocumentState.Empty.AddAnnotation(bottom).AddAnnotation(top);

        Assert.Equal(top.Id, state.HitTest(15, 15)?.Id);
        Assert.Equal(bottom.Id, state.HitTest(90, 90)?.Id);
        Assert.Null(state.HitTest(500, 500));
    }

    [Fact]
    public void RemovingAnUnknownObject_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => DocumentState.Empty.RemoveAnnotation(Guid.NewGuid()));
    }

    [Fact]
    public void MoveCommand_StoresBothEndpoints_SoRevertIsExactRatherThanADelta()
    {
        var annotation = Rect(x: 5, y: 5);
        var state = DocumentState.Empty.AddAnnotation(annotation);
        var origin = annotation.Bounds;
        var target = origin.Translated(30, 40);

        var command = new MoveAnnotationCommand(annotation.Id, origin, target);
        var moved = command.Apply(state);
        Assert.Equal(target, moved.Annotations[0].Bounds);

        var reverted = command.Revert(moved);
        Assert.Equal(origin, reverted.Annotations[0].Bounds);
    }

    [Fact]
    public void MoveCommand_ExtendTo_KeepsTheOriginalStart()
    {
        var origin = new RectF(0, 0, 10, 10);
        var command = new MoveAnnotationCommand(Guid.NewGuid(), origin, origin.Translated(5, 5));

        var extended = command.ExtendTo(origin.Translated(50, 50));

        Assert.Equal(origin, extended.From);
        Assert.Equal(origin.Translated(50, 50), extended.To);
    }

    [Fact]
    public void Commands_ReportTheirRetainedBytes()
    {
        var annotation = Rect();
        var state = DocumentState.Empty.AddAnnotation(annotation);

        Assert.True(new AddAnnotationCommand(annotation).EstimatedRetainedBytes > 0);
        Assert.True(new DeleteAnnotationCommand(state, annotation.Id).EstimatedRetainedBytes > 0);
        Assert.True(new MoveAnnotationCommand(annotation.Id, default, default).EstimatedRetainedBytes > 0);
    }
}
