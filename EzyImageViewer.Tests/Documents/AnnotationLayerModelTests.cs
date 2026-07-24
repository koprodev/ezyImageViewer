using EzyImageViewer.Core.Commands;
using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Documents.Serialization;
using Xunit;

namespace EzyImageViewer.Tests.Documents;

/// <summary>레이어 컨테이너·레이어별 z 순서·승격·정확한 실행 취소 검증.</summary>
public sealed class AnnotationLayerModelTests
{
    private static RectangleAnnotation Rect(float x = 0f) => new()
    {
        Id = Guid.NewGuid(),
        Bounds = new RectF(x, 0, 10, 10),
        StrokeArgb = 0xFF11_2233,
        StrokeWidth = 2f,
    };

    private static DocumentState TwoLayerState(out AnnotationLayer top)
    {
        top = new AnnotationLayer { Id = Guid.NewGuid(), Name = "top" };
        return DocumentState.Empty.AddLayer(top);
    }

    [Fact]
    public void EmptyState_HasExactlyOneInitialLayer()
    {
        var layer = Assert.Single(DocumentState.Empty.Layers);
        Assert.Equal(AnnotationLayer.InitialLayerId, layer.Id);
        Assert.True(layer.IsVisible);
        Assert.False(layer.IsLocked);
        Assert.Empty(DocumentState.Empty.Annotations);
    }

    [Fact]
    public void AddAnnotation_TargetsTheGivenLayer_AndFlattenFollowsLayerOrder()
    {
        var state = TwoLayerState(out var top);
        var back = Rect(0f);
        var front = Rect(20f);
        state = state.AddAnnotation(back, AnnotationLayer.InitialLayerId);
        state = state.AddAnnotation(front, top.Id);

        Assert.Equal([back.Id, front.Id], state.Annotations.Select(a => a.Id));
        Assert.Equal(AnnotationLayer.InitialLayerId, state.FindLayerOf(back.Id)!.Id);
        Assert.Equal(top.Id, state.FindLayerOf(front.Id)!.Id);
        Assert.Equal(1, state.IndexOf(front.Id));
    }

    [Fact]
    public void LastLayer_CannotBeRemoved_AndDeleteCommandRefusesIt()
    {
        Assert.Throws<InvalidOperationException>(
            () => DocumentState.Empty.RemoveLayer(AnnotationLayer.InitialLayerId));
        Assert.Throws<InvalidOperationException>(
            () => new DeleteLayerCommand(DocumentState.Empty, AnnotationLayer.InitialLayerId));
    }

    [Fact]
    public void DeleteLayer_UndoRestoresPositionAndObjects()
    {
        var state = TwoLayerState(out var top);
        var annotation = Rect();
        state = state.AddAnnotation(annotation, top.Id);
        var command = new DeleteLayerCommand(state, top.Id);

        var removed = command.Apply(state);
        Assert.Single(removed.Layers);
        Assert.Null(removed.Find(annotation.Id));

        var restored = command.Revert(removed);
        Assert.Equal(2, restored.Layers.Count);
        Assert.Equal(top.Id, restored.Layers[1].Id);
        Assert.Equal(annotation.Id, Assert.Single(restored.FindLayer(top.Id)!.Annotations).Id);
    }

    [Fact]
    public void ReorderLayer_RoundTripsExactly()
    {
        var state = TwoLayerState(out var top);
        var command = new ReorderLayerCommand(state, top.Id, 0);

        var applied = command.Apply(state);
        Assert.Equal([top.Id, AnnotationLayer.InitialLayerId], applied.Layers.Select(l => l.Id));

        var reverted = command.Revert(applied);
        Assert.Equal([AnnotationLayer.InitialLayerId, top.Id], reverted.Layers.Select(l => l.Id));
    }

    [Fact]
    public void ReplaceLayer_ChangesPropsButNeverMembership()
    {
        var state = TwoLayerState(out var top);
        var before = state.FindLayer(top.Id)!;
        var command = new ReplaceLayerCommand(
            LayerEditKind.Visibility, before, before with { IsVisible = false });

        var applied = command.Apply(state);
        Assert.False(applied.FindLayer(top.Id)!.IsVisible);
        Assert.True(command.Revert(applied).FindLayer(top.Id)!.IsVisible);

        var annotation = Rect();
        var membershipChange = before with { Annotations = [annotation] };
        Assert.Throws<InvalidOperationException>(() => state.ReplaceLayer(membershipChange));
    }

    [Fact]
    public void MoveAnnotationToLayer_UndoReturnsToTheOriginalSlot()
    {
        var state = TwoLayerState(out var top);
        var first = Rect(0f);
        var second = Rect(20f);
        state = state.AddAnnotation(first, AnnotationLayer.InitialLayerId);
        state = state.AddAnnotation(second, AnnotationLayer.InitialLayerId);
        var command = new MoveAnnotationToLayerCommand(state, first.Id, top.Id);

        var moved = command.Apply(state);
        Assert.Equal(top.Id, moved.FindLayerOf(first.Id)!.Id);
        Assert.Equal([second.Id, first.Id], moved.Annotations.Select(a => a.Id));

        var reverted = command.Revert(moved);
        Assert.Equal(AnnotationLayer.InitialLayerId, reverted.FindLayerOf(first.Id)!.Id);
        Assert.Equal(0, reverted.FindLayer(AnnotationLayer.InitialLayerId)!.IndexOf(first.Id));
    }

    [Fact]
    public void ObjectZOrder_IsScopedToItsOwnLayer()
    {
        var state = TwoLayerState(out var top);
        var back = Rect(0f);
        var frontA = Rect(20f);
        var frontB = Rect(40f);
        state = state.AddAnnotation(back, AnnotationLayer.InitialLayerId);
        state = state.AddAnnotation(frontA, top.Id);
        state = state.AddAnnotation(frontB, top.Id);

        var command = new ReorderAnnotationCommand(state, frontB.Id, 0);
        var applied = command.Apply(state);

        // frontB가 상단 레이어 안에서 frontA 아래로 이동해도 레이어 전체는 back 위.
        Assert.Equal([back.Id, frontB.Id, frontA.Id], applied.Annotations.Select(a => a.Id));
        Assert.Equal(
            [frontB.Id, frontA.Id], applied.FindLayer(top.Id)!.Annotations.Select(a => a.Id));
    }

    [Fact]
    public void HitTest_SkipsHiddenAndLockedLayers()
    {
        var state = TwoLayerState(out var top);
        var bottom = Rect(0f);
        var covering = Rect(0f);
        state = state.AddAnnotation(bottom, AnnotationLayer.InitialLayerId);
        state = state.AddAnnotation(covering, top.Id);

        Assert.Equal(covering.Id, state.HitTest(5f, 5f)!.Id);

        var hiddenTop = state.ReplaceLayer(state.FindLayer(top.Id)! with { IsVisible = false });
        Assert.Equal(bottom.Id, hiddenTop.HitTest(5f, 5f)!.Id);
        Assert.False(hiddenTop.IsEffectivelyVisible(covering.Id));

        var lockedTop = state.ReplaceLayer(state.FindLayer(top.Id)! with { IsLocked = true });
        Assert.Equal(bottom.Id, lockedTop.HitTest(5f, 5f)!.Id);
        Assert.True(lockedTop.IsEffectivelyLocked(covering.Id));
    }

    [Fact]
    public void DuplicateAnnotation_LandsDirectlyAboveItsSourceInTheSameLayer()
    {
        var state = TwoLayerState(out var top);
        var first = Rect(0f);
        var second = Rect(20f);
        state = state.AddAnnotation(first, top.Id);
        state = state.AddAnnotation(second, top.Id);

        var command = new DuplicateAnnotationCommand(state, first.Id);
        var applied = command.Apply(state);

        Assert.Equal(
            [first.Id, command.DuplicateId, second.Id],
            applied.FindLayer(top.Id)!.Annotations.Select(a => a.Id));
    }

    [Fact]
    public void V1FlatFragment_MigratesToASingleInitialLayer()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var json = $$"""
            {"transform":[],"annotations":[
            {"kind":"rectangle","id":"{{first}}","x":1,"y":2,"width":3,"height":4,"strokeArgb":1,"strokeWidth":2},
            {"kind":"rectangle","id":"{{second}}","x":5,"y":6,"width":7,"height":8,"strokeArgb":1,"strokeWidth":2,"isLocked":true}]}
            """;

        var state = DocumentStateSerializer.Read(json);

        var layer = Assert.Single(state.Layers);
        Assert.Equal(AnnotationLayer.InitialLayerId, layer.Id);
        Assert.Equal([first, second], layer.Annotations.Select(a => a.Id));
        Assert.True(state.Find(second)!.IsLocked);
    }

    [Fact]
    public void ManifestSchemaVersion_MustMatchTheFragmentShape()
    {
        var flat = """{"transform":[],"annotations":[]}""";
        var layered = $$"""{"transform":[],"layers":[{"id":"{{Guid.NewGuid()}}","annotations":[]}]}""";

        // 양방향 버전 일치는 읽고, 불일치·범위 밖 버전은 실패.
        Assert.Single(DocumentStateSerializer.Read(flat, 1).Layers);
        Assert.Single(DocumentStateSerializer.Read(layered, 2).Layers);
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(flat, 2));
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(layered, 1));
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(flat, 0));
        Assert.Throws<InvalidDataException>(
            () => DocumentStateSerializer.Read(layered, ProjectManifest.CurrentSchemaVersion + 1));
    }

    [Fact]
    public void V1Read_ThenWrite_ThenRead_ChainsIntoV2()
    {
        var id = Guid.NewGuid();
        var v1 = $$"""
            {"transform":[],"annotations":[
            {"kind":"rectangle","id":"{{id}}","x":1,"y":2,"width":3,"height":4,"strokeArgb":1,"strokeWidth":2}]}
            """;

        var migrated = DocumentStateSerializer.Read(v1, 1);
        var v2Json = DocumentStateSerializer.Write(migrated);
        var final = DocumentStateSerializer.Read(v2Json, 2);

        var layer = Assert.Single(final.Layers);
        Assert.Equal(AnnotationLayer.InitialLayerId, layer.Id);
        Assert.Equal(id, Assert.Single(layer.Annotations).Id);
    }

    [Fact]
    public void ReplaceLayer_RejectsContentMutationOfContainedObjects()
    {
        var state = TwoLayerState(out var top);
        var annotation = Rect();
        state = state.AddAnnotation(annotation, top.Id);
        var layer = state.FindLayer(top.Id)!;
        var mutated = layer with
        {
            Annotations = [annotation with { StrokeArgb = 0xFFFF_0000 }],
        };

        Assert.Throws<InvalidOperationException>(() => state.ReplaceLayer(mutated));
        Assert.Throws<ArgumentException>(
            () => new ReplaceLayerCommand(LayerEditKind.Name, layer, mutated));
        // 종류 한정 편집이 두 번째 속성 변경을 몰래 끼우면 안 됨.
        Assert.Throws<ArgumentException>(() => new ReplaceLayerCommand(
            LayerEditKind.Name, layer, layer with { Name = "renamed", IsLocked = true }));
    }

    [Fact]
    public void MoveAnnotationToLayer_SameLayerCommand_IsANoOpBothWays()
    {
        var state = DocumentState.Empty.AddAnnotation(Rect());
        var id = state.Annotations[0].Id;
        var command = new MoveAnnotationToLayerCommand(state, id, AnnotationLayer.InitialLayerId);

        Assert.True(command.IsNoOp);
        Assert.Same(state, command.Apply(state));
        Assert.Same(state, command.Revert(state));
    }

    [Fact]
    public void V2LayeredState_RoundTripsThroughSerializerAndV3Container()
    {
        var state = TwoLayerState(out var top);
        var bottomObject = Rect(0f);
        var topObject = Rect(20f);
        state = state.AddAnnotation(bottomObject, AnnotationLayer.InitialLayerId);
        state = state.AddAnnotation(topObject, top.Id);
        state = state.ReplaceLayer(state.FindLayer(top.Id)! with { Name = "메모", IsLocked = true });

        var json = DocumentStateSerializer.Write(state, activeLayerId: top.Id);
        var restored = DocumentStateSerializer.Read(json);

        Assert.Equal(2, restored.Layers.Count);
        Assert.Equal(state.Layers.Select(l => l.Id), restored.Layers.Select(l => l.Id));
        var restoredTop = restored.FindLayer(top.Id)!;
        Assert.Equal("메모", restoredTop.Name);
        Assert.True(restoredTop.IsLocked);
        Assert.Equal([bottomObject.Id, topObject.Id], restored.Annotations.Select(a => a.Id));

        using var buffer = new MemoryStream();
        EzyProjectArchive.Write(buffer, new EzyProject
        {
            Manifest = ProjectManifest.Create("test"),
            DocumentJson = ProjectDocumentSerializer.Write(
                [new ProjectPageState(state, top.Id)], activePageIndex: 0),
        });
        buffer.Position = 0;
        var project = EzyProjectArchive.Read(buffer);
        Assert.Equal(3, project.Manifest.SchemaVersion);
        Assert.Equal(2,
            ProjectDocumentSerializer.Read(project.DocumentJson, project.Manifest.SchemaVersion)
                .Pages[0].State.Layers.Count);
    }

    [Fact]
    public void ActiveLayerId_RoundTripsThroughTheMetadataOverload()
    {
        var state = TwoLayerState(out var top);
        var json = DocumentStateSerializer.Write(state, activeLayerId: top.Id);

        DocumentStateSerializer.Read(json, 2, out var restored);
        Assert.Equal(top.Id, restored);

        DocumentStateSerializer.Read(
            DocumentStateSerializer.Write(state), 2, out var absent);
        Assert.Null(absent);
    }

    [Fact]
    public void EditorReset_WithSeededState_StartsCleanAtThatState()
    {
        using var document = new ImageDocument
        {
            Frame = new EzyImageViewer.Core.Imaging.DecodedFrame(new byte[16], 2, 2, 8, hasAlpha: false),
            Source = DocumentSource.FromProject(@"C:\tmp\sample.ezyimg"),
            NativeSize = new EzyImageViewer.Core.Imaging.PixelSize(2, 2),
        };
        var seeded = TwoLayerState(out var top).AddAnnotation(Rect(), top.Id);
        var editor = new DocumentEditor();

        editor.Reset(document, seeded);

        Assert.Same(seeded, editor.State);
        Assert.False(editor.IsModified);
        Assert.False(editor.CanUndo);
        editor.Apply(new AddAnnotationCommand(Rect(30f)));
        Assert.True(editor.IsModified);
        editor.MarkSaved();
        Assert.False(editor.IsModified);
    }

    [Fact]
    public void HostileLayerFragments_FailTheRead()
    {
        var id = Guid.NewGuid();
        // 두 모양 동시 존재.
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(
            $$"""{"transform":[],"annotations":[],"layers":[{"id":"{{id}}","annotations":[]}]}"""));
        // 모양 둘 다 없음.
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(
            """{"transform":[]}"""));
        // 레이어 0개.
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(
            """{"transform":[],"layers":[]}"""));
        // 레이어 ID 중복.
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(
            $$"""{"transform":[],"layers":[{"id":"{{id}}","annotations":[]},{"id":"{{id}}","annotations":[]}]}"""));
        // 활성 레이어가 없는 곳을 가리킴.
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(
            $$"""{"transform":[],"layers":[{"id":"{{id}}","annotations":[]}],"activeLayerId":"{{Guid.NewGuid()}}"}"""));
        // 레이어 사이 주석 ID 중복.
        var annotation = Guid.NewGuid();
        Assert.Throws<InvalidDataException>(() => DocumentStateSerializer.Read(
            $$"""
            {"transform":[],"layers":[
            {"id":"{{id}}","annotations":[{"kind":"rectangle","id":"{{annotation}}","x":1,"y":2,"width":3,"height":4,"strokeArgb":1,"strokeWidth":2}]},
            {"id":"{{Guid.NewGuid()}}","annotations":[{"kind":"rectangle","id":"{{annotation}}","x":1,"y":2,"width":3,"height":4,"strokeArgb":1,"strokeWidth":2}]}]}
            """));
    }
}
