using EzyImageViewer.Core.Documents.Layers;

namespace EzyImageViewer.Core.Commands;

/// <summary>Adds an object to a layer (FR-LAYER-001 authoring path). The caller passes the active
/// layer; null targets the topmost layer.</summary>
public sealed class AddAnnotationCommand(Annotation annotation, Guid? layerId = null) : IEditCommand
{
    private readonly Annotation _annotation = annotation ?? throw new ArgumentNullException(nameof(annotation));
    private readonly Guid? _layerId = layerId;

    public string Name => "AddAnnotation";

    public long EstimatedRetainedBytes => _annotation.EstimatedRetainedBytes;

    public object? MergeKey => null;

    public DocumentState Apply(DocumentState state) => state.AddAnnotation(_annotation, _layerId);

    public DocumentState Revert(DocumentState state) => state.RemoveAnnotation(_annotation.Id);
}

public sealed class AddImageAnnotationCommand : IEditCommand
{
    private readonly RasterAsset _asset;
    private readonly ImageAnnotation _annotation;
    private readonly Guid? _layerId;

    public AddImageAnnotationCommand(RasterAsset asset, ImageAnnotation annotation, Guid? layerId = null)
    {
        _asset = AnnotationValidator.Validate(asset);
        _annotation = (ImageAnnotation)AnnotationValidator.Validate(annotation);
        _layerId = layerId;
        if (_annotation.AssetId != _asset.Id)
            throw new ArgumentException("Image annotation must reference the supplied asset.", nameof(annotation));
    }

    public string Name => "AddImageAnnotation";
    public long EstimatedRetainedBytes =>
        checked(_asset.EstimatedRetainedBytes + _annotation.EstimatedRetainedBytes);
    public object? MergeKey => null;

    public DocumentState Apply(DocumentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.FindAsset(_asset.Id) is not null)
            throw new InvalidOperationException($"Raster asset {_asset.Id} is already in the document.");
        return state.AddAsset(_asset).AddAnnotation(_annotation, _layerId);
    }

    public DocumentState Revert(DocumentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.RemoveAnnotation(_annotation.Id).RemoveAsset(_asset.Id);
    }
}

/// <summary>
/// Deletes an object (FR-LAYER-004). Retains the object and its (layer, index) position so undo
/// restores identity, owner layer and z-order.
/// </summary>
public sealed class DeleteAnnotationCommand : IEditCommand
{
    private readonly Annotation _annotation;
    private readonly Guid _layerId;
    private readonly int _innerIndex;

    public DeleteAnnotationCommand(DocumentState state, Guid id)
    {
        ArgumentNullException.ThrowIfNull(state);
        var layer = state.FindLayerOf(id)
            ?? throw new InvalidOperationException($"Annotation {id} is not on the layer.");
        _layerId = layer.Id;
        _innerIndex = layer.IndexOf(id);
        _annotation = layer.Annotations[_innerIndex];
    }

    public string Name => "DeleteAnnotation";

    public long EstimatedRetainedBytes => _annotation.EstimatedRetainedBytes;

    public object? MergeKey => null;

    public DocumentState Apply(DocumentState state) => state.RemoveAnnotation(_annotation.Id);

    public DocumentState Revert(DocumentState state) =>
        state.InsertAnnotation(_layerId, _innerIndex, _annotation);
}

/// <summary>Duplicates into the source's own layer, directly above the source (Photoshop order).</summary>
public sealed class DuplicateAnnotationCommand : IEditCommand
{
    private readonly Annotation _duplicate;
    private readonly Guid _layerId;
    private readonly int _innerIndex;

    public DuplicateAnnotationCommand(
        DocumentState state, Guid sourceId, Guid? duplicateId = null, float offset = 10f)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!float.IsFinite(offset))
            throw new ArgumentOutOfRangeException(nameof(offset));
        var layer = state.FindLayerOf(sourceId)
            ?? throw new InvalidOperationException($"Annotation {sourceId} is not on the layer.");
        var source = layer.Annotations[layer.IndexOf(sourceId)];
        var id = duplicateId ?? Guid.NewGuid();
        if (id == Guid.Empty || state.Find(id) is not null)
            throw new ArgumentException("Duplicate id must be non-empty and unique.", nameof(duplicateId));
        _duplicate = source.WithBounds(source.Bounds.Translated(offset, offset)) with { Id = id };
        _layerId = layer.Id;
        _innerIndex = layer.IndexOf(sourceId) + 1;
        AnnotationValidator.Validate(_duplicate);
    }

    public Guid DuplicateId => _duplicate.Id;
    public string Name => "DuplicateAnnotation";
    public long EstimatedRetainedBytes => _duplicate.EstimatedRetainedBytes;
    public object? MergeKey => null;
    public DocumentState Apply(DocumentState state) =>
        state.InsertAnnotation(_layerId, _innerIndex, _duplicate);
    public DocumentState Revert(DocumentState state) => state.RemoveAnnotation(_duplicate.Id);
}

/// <summary>Reorders within the object's own layer — indexes are layer-inner positions (UR-007).</summary>
public sealed class ReorderAnnotationCommand : IEditCommand
{
    public ReorderAnnotationCommand(DocumentState state, Guid id, int targetIndex)
    {
        ArgumentNullException.ThrowIfNull(state);
        var layer = state.FindLayerOf(id)
            ?? throw new InvalidOperationException($"Annotation {id} is not on the layer.");
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(targetIndex, layer.Annotations.Count);
        AnnotationId = id;
        LayerId = layer.Id;
        FromIndex = layer.IndexOf(id);
        ToIndex = targetIndex;
    }

    public Guid AnnotationId { get; }
    public Guid LayerId { get; }
    public int FromIndex { get; }
    public int ToIndex { get; }
    public bool IsNoOp => FromIndex == ToIndex;
    public string Name => "ReorderAnnotation";
    public long EstimatedRetainedBytes => 32;
    public object? MergeKey => null;

    public DocumentState Apply(DocumentState state)
    {
        if (state.FindLayerOf(AnnotationId) is not { } layer
            || layer.Id != LayerId || layer.IndexOf(AnnotationId) != FromIndex)
            throw new InvalidOperationException("Reorder command does not match its source paint index.");
        return state.ReorderAnnotation(AnnotationId, ToIndex);
    }

    public DocumentState Revert(DocumentState state)
    {
        if (state.FindLayerOf(AnnotationId) is not { } layer
            || layer.Id != LayerId || layer.IndexOf(AnnotationId) != ToIndex)
            throw new InvalidOperationException("Reorder command does not match its target paint index.");
        return state.ReorderAnnotation(AnnotationId, FromIndex);
    }
}

/// <summary>
/// Moves an object (FR-LAYER-002). Stores both endpoints rather than a delta so revert is exact
/// regardless of what else touched the object, and so a drag coalesces by rewriting only <see cref="To"/>.
/// </summary>
public sealed class MoveAnnotationCommand(Guid id, RectF from, RectF to, long gestureId = 0) : IEditCommand
{
    public Guid AnnotationId { get; } = id;

    public RectF From { get; } = from;

    public RectF To { get; } = to;

    /// <summary>Identity of the authoring drag; zero means "never coalesce".</summary>
    public long GestureId { get; } = gestureId;

    public string Name => "MoveAnnotation";

    // Two rectangles plus an id.
    public long EstimatedRetainedBytes => 48;

    public object? MergeKey => GestureId == 0 ? null : new MoveMergeKey(AnnotationId, GestureId);

    public DocumentState Apply(DocumentState state) => Retarget(state, To);

    public DocumentState Revert(DocumentState state) => Retarget(state, From);

    /// <summary>Extends an in-progress drag: same object, original start, new end (§7.8 coalescing).</summary>
    public MoveAnnotationCommand ExtendTo(RectF bounds) => new(AnnotationId, From, bounds, GestureId);

    public bool IsNoOp => From == To;

    private DocumentState Retarget(DocumentState state, RectF bounds)
    {
        var target = state.Find(AnnotationId)
            ?? throw new InvalidOperationException($"Annotation {AnnotationId} is not on the layer.");
        return state.ReplaceAnnotation(target.WithBounds(bounds));
    }

    private readonly record struct MoveMergeKey(Guid AnnotationId, long GestureId);
}

public enum AnnotationEditKind
{
    Geometry,
    Style,
    LayerState,
    Content,
}

/// <summary>Replaces one immutable annotation while preserving its id and paint index.</summary>
public sealed class ReplaceAnnotationCommand : IEditCommand
{
    public ReplaceAnnotationCommand(
        AnnotationEditKind kind, Annotation before, Annotation after, long gestureId = 0)
    {
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown edit kind.");
        AnnotationValidator.Validate(before);
        AnnotationValidator.Validate(after);
        if (before.Id != after.Id)
            throw new ArgumentException("Replacement annotations must have the same id.", nameof(after));
        Kind = kind;
        Before = before;
        After = after;
        GestureId = gestureId;
    }

    public AnnotationEditKind Kind { get; }
    public Annotation Before { get; }
    public Annotation After { get; }
    public long GestureId { get; }
    public string Name => $"Annotation.{Kind}";
    public long EstimatedRetainedBytes =>
        checked(Before.EstimatedRetainedBytes + After.EstimatedRetainedBytes);
    public object? MergeKey => GestureId == 0
        ? null
        : new ReplacementMergeKey(Before.Id, Kind, GestureId);

    public DocumentState Apply(DocumentState state) => Retarget(state, Before, After);
    public DocumentState Revert(DocumentState state) => Retarget(state, After, Before);

    private static DocumentState Retarget(DocumentState state, Annotation expected, Annotation next)
    {
        ArgumentNullException.ThrowIfNull(state);
        var current = state.Find(expected.Id)
            ?? throw new InvalidOperationException($"Annotation {expected.Id} is not on the layer.");
        if (!Equals(current, expected))
            throw new InvalidOperationException("Replacement command does not match the state it runs against.");
        return state.ReplaceAnnotation(next);
    }

    private readonly record struct ReplacementMergeKey(
        Guid AnnotationId, AnnotationEditKind Kind, long GestureId);
}
