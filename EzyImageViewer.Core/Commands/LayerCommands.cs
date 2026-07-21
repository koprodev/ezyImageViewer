using EzyImageViewer.Core.Documents.Layers;

namespace EzyImageViewer.Core.Commands;

/// <summary>Adds an empty (or prepared) layer; null index appends on top (UR-007).</summary>
public sealed class AddLayerCommand : IEditCommand
{
    private readonly AnnotationLayer _layer;
    private readonly int? _index;

    public AddLayerCommand(AnnotationLayer layer, int? index = null)
    {
        _layer = AnnotationValidator.Validate(layer);
        _index = index;
    }

    public Guid LayerId => _layer.Id;
    public string Name => "AddLayer";
    public long EstimatedRetainedBytes => _layer.EstimatedRetainedBytes;
    public object? MergeKey => null;
    public DocumentState Apply(DocumentState state) => state.AddLayer(_layer, _index);
    public DocumentState Revert(DocumentState state) => state.RemoveLayer(_layer.Id);
}

/// <summary>Deletes a layer with its objects. Retains the layer and its position for exact undo.
/// The last layer is not deletable — the state operation enforces it, the constructor reports it.</summary>
public sealed class DeleteLayerCommand : IEditCommand
{
    private readonly AnnotationLayer _layer;
    private readonly int _index;

    public DeleteLayerCommand(DocumentState state, Guid layerId)
    {
        ArgumentNullException.ThrowIfNull(state);
        _index = state.LayerIndexOf(layerId);
        if (_index < 0)
            throw new InvalidOperationException($"Layer {layerId} is not in the document.");
        if (state.Layers.Count == 1)
            throw new InvalidOperationException("The last layer cannot be removed.");
        _layer = state.Layers[_index];
    }

    public string Name => "DeleteLayer";
    public long EstimatedRetainedBytes => _layer.EstimatedRetainedBytes;
    public object? MergeKey => null;
    public DocumentState Apply(DocumentState state) => state.RemoveLayer(_layer.Id);
    public DocumentState Revert(DocumentState state) => state.AddLayer(_layer, _index);
}

public sealed class ReorderLayerCommand : IEditCommand
{
    public ReorderLayerCommand(DocumentState state, Guid layerId, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(state);
        var sourceIndex = state.LayerIndexOf(layerId);
        if (sourceIndex < 0)
            throw new InvalidOperationException($"Layer {layerId} is not in the document.");
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(targetIndex, state.Layers.Count);
        LayerId = layerId;
        FromIndex = sourceIndex;
        ToIndex = targetIndex;
    }

    public Guid LayerId { get; }
    public int FromIndex { get; }
    public int ToIndex { get; }
    public bool IsNoOp => FromIndex == ToIndex;
    public string Name => "ReorderLayer";
    public long EstimatedRetainedBytes => 32;
    public object? MergeKey => null;

    public DocumentState Apply(DocumentState state)
    {
        if (state.LayerIndexOf(LayerId) != FromIndex)
            throw new InvalidOperationException("Layer reorder command does not match its source index.");
        return state.ReorderLayer(LayerId, ToIndex);
    }

    public DocumentState Revert(DocumentState state)
    {
        if (state.LayerIndexOf(LayerId) != ToIndex)
            throw new InvalidOperationException("Layer reorder command does not match its target index.");
        return state.ReorderLayer(LayerId, FromIndex);
    }
}

public enum LayerEditKind
{
    Name,
    Visibility,
    Lock,
}

/// <summary>Replaces exactly the one layer property named by its kind; the contained object
/// sequence and every other property must be unchanged (rename, show/hide, lock).</summary>
public sealed class ReplaceLayerCommand : IEditCommand
{
    public ReplaceLayerCommand(LayerEditKind kind, AnnotationLayer before, AnnotationLayer after)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown edit kind.");
        AnnotationValidator.Validate(before);
        AnnotationValidator.Validate(after);
        if (before.Id != after.Id)
            throw new ArgumentException("Replacement layers must have the same id.", nameof(after));
        if (!before.Annotations.SequenceEqual(after.Annotations))
            throw new ArgumentException("Replacement layers must keep the same objects.", nameof(after));
        var othersIntact = kind switch
        {
            LayerEditKind.Name => before.IsVisible == after.IsVisible && before.IsLocked == after.IsLocked,
            LayerEditKind.Visibility => before.Name == after.Name && before.IsLocked == after.IsLocked,
            _ => before.Name == after.Name && before.IsVisible == after.IsVisible,
        };
        if (!othersIntact)
            throw new ArgumentException($"A {kind} edit must not change other layer properties.", nameof(after));
        Kind = kind;
        Before = before;
        After = after;
    }

    public LayerEditKind Kind { get; }
    public AnnotationLayer Before { get; }
    public AnnotationLayer After { get; }
    public string Name => $"Layer.{Kind}";
    public long EstimatedRetainedBytes => 128;
    public object? MergeKey => null;

    public DocumentState Apply(DocumentState state) => Retarget(state, Before, After);
    public DocumentState Revert(DocumentState state) => Retarget(state, After, Before);

    private static DocumentState Retarget(DocumentState state, AnnotationLayer expected, AnnotationLayer next)
    {
        ArgumentNullException.ThrowIfNull(state);
        var current = state.FindLayer(expected.Id)
            ?? throw new InvalidOperationException($"Layer {expected.Id} is not in the document.");
        if (!Equals(current, expected))
            throw new InvalidOperationException("Layer replacement does not match the state it runs against.");
        return state.ReplaceLayer(next);
    }
}

/// <summary>Moves one object to another layer's top; records both endpoints for exact undo.
/// A same-layer target is a no-op in both directions, never an index-shifted reorder.</summary>
public sealed class MoveAnnotationToLayerCommand : IEditCommand
{
    public MoveAnnotationToLayerCommand(DocumentState state, Guid annotationId, Guid targetLayerId)
    {
        ArgumentNullException.ThrowIfNull(state);
        var sourceLayer = state.FindLayerOf(annotationId)
            ?? throw new InvalidOperationException($"Annotation {annotationId} is not on the layer.");
        var target = state.FindLayer(targetLayerId)
            ?? throw new InvalidOperationException($"Layer {targetLayerId} is not in the document.");
        AnnotationId = annotationId;
        FromLayerId = sourceLayer.Id;
        FromInnerIndex = sourceLayer.IndexOf(annotationId);
        ToLayerId = target.Id;
        ToInnerIndex = target.Annotations.Count;
    }

    public Guid AnnotationId { get; }
    public Guid FromLayerId { get; }
    public int FromInnerIndex { get; }
    public Guid ToLayerId { get; }
    public int ToInnerIndex { get; }
    public bool IsNoOp => FromLayerId == ToLayerId;
    public string Name => "MoveAnnotationToLayer";
    public long EstimatedRetainedBytes => 64;
    public object? MergeKey => null;

    public DocumentState Apply(DocumentState state) => IsNoOp
        ? state
        : state.MoveAnnotationToLayer(AnnotationId, ToLayerId, ToInnerIndex);

    public DocumentState Revert(DocumentState state) => IsNoOp
        ? state
        : state.MoveAnnotationToLayer(AnnotationId, FromLayerId, FromInnerIndex);
}
