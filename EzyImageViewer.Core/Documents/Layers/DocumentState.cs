namespace EzyImageViewer.Core.Documents.Layers;

/// <summary>
/// The editable content of a document (§6.3 BackgroundLayer + AnnotationLayers), immutable so a
/// command's result is a value and undo is an exact replacement rather than a reversal in place.
/// Annotations live inside ordered <see cref="AnnotationLayer"/> containers (UR-007): layer order is
/// the coarse paint order, object order inside a layer is the fine one. There is always at least one
/// layer. The active (authoring) layer is UI state owned by the window, not part of this value —
/// selecting a layer is not an undoable document edit.
/// The background raster itself is not held here: it is the <see cref="ImageDocument"/>'s frame,
/// which commands never mutate (annotations composite over it at paint time — ADR-0008).
/// </summary>
public sealed record DocumentState
{
    public static DocumentState Empty { get; } = new();

    /// <summary>Paint order: index 0 is farthest back. Never empty.</summary>
    public IReadOnlyList<AnnotationLayer> Layers { get; init; } =
        [new AnnotationLayer { Id = AnnotationLayer.InitialLayerId }];

    /// <summary>Flattened paint order across all layers (computed; hidden layers included).
    /// Fine-grained operations should address (layer, index) pairs instead.</summary>
    public IReadOnlyList<Annotation> Annotations
    {
        get
        {
            var flat = new List<Annotation>();
            foreach (var layer in Layers)
                flat.AddRange(layer.Annotations);
            return flat;
        }
    }

    /// <summary>Encoded raster payloads are owned once and referenced by <see cref="ImageAnnotation"/>.</summary>
    public IReadOnlyList<RasterAsset> Assets { get; init; } = [];

    /// <summary>Background transform pipeline (FR-EDIT-001~004). Annotations share it: their native
    /// coordinates ride the same derived matrix, so they stay glued to the image (ADR-0009).</summary>
    public BackgroundTransform Transform { get; init; } = BackgroundTransform.Identity;

    public DocumentState WithTransform(BackgroundTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return this with { Transform = transform };
    }

    // ---- Layer operations -------------------------------------------------------------------

    public AnnotationLayer? FindLayer(Guid layerId)
    {
        var index = LayerIndexOf(layerId);
        return index < 0 ? null : Layers[index];
    }

    public int LayerIndexOf(Guid layerId)
    {
        for (var i = 0; i < Layers.Count; i++)
        {
            if (Layers[i].Id == layerId)
                return i;
        }
        return -1;
    }

    /// <summary>The layer containing the annotation, or null.</summary>
    public AnnotationLayer? FindLayerOf(Guid annotationId)
    {
        foreach (var layer in Layers)
        {
            if (layer.IndexOf(annotationId) >= 0)
                return layer;
        }
        return null;
    }

    public DocumentState AddLayer(AnnotationLayer layer, int? index = null)
    {
        AnnotationValidator.Validate(layer);
        if (LayerIndexOf(layer.Id) >= 0)
            throw new InvalidOperationException($"Layer {layer.Id} is already in the document.");
        if (Layers.Count >= AnnotationValidator.MaxLayers)
            throw new InvalidOperationException(
                $"Document exceeds the {AnnotationValidator.MaxLayers} layer limit.");
        var target = index ?? Layers.Count;
        ArgumentOutOfRangeException.ThrowIfNegative(target);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(target, Layers.Count);
        foreach (var annotation in layer.Annotations)
        {
            AnnotationValidator.Validate(annotation);
            ValidateAssetReference(annotation);
            if (FindLayerOf(annotation.Id) is not null)
                throw new InvalidOperationException($"Annotation {annotation.Id} is already in the document.");
        }
        ValidateUniqueWithin(layer);
        var next = Layers.ToList();
        next.Insert(target, layer);
        return this with { Layers = next };
    }

    /// <summary>Removes a layer and its objects. The last layer cannot be removed (§UR-007: a
    /// document always has an authoring target).</summary>
    public DocumentState RemoveLayer(Guid layerId)
    {
        var index = LayerIndexOf(layerId);
        if (index < 0)
            throw new InvalidOperationException($"Layer {layerId} is not in the document.");
        if (Layers.Count == 1)
            throw new InvalidOperationException("The last layer cannot be removed.");
        var next = Layers.ToList();
        next.RemoveAt(index);
        return this with { Layers = next };
    }

    /// <summary>Replaces layer-level properties (name, visibility, lock). The contained object
    /// sequence must be value-equal — membership AND content move through the dedicated operations.</summary>
    public DocumentState ReplaceLayer(AnnotationLayer layer)
    {
        AnnotationValidator.Validate(layer);
        var index = LayerIndexOf(layer.Id);
        if (index < 0)
            throw new InvalidOperationException($"Layer {layer.Id} is not in the document.");
        var current = Layers[index];
        if (current.Annotations.Count != layer.Annotations.Count)
            throw new InvalidOperationException("ReplaceLayer cannot change layer membership.");
        for (var i = 0; i < current.Annotations.Count; i++)
        {
            if (!Equals(current.Annotations[i], layer.Annotations[i]))
                throw new InvalidOperationException("ReplaceLayer cannot change the contained objects.");
        }
        var next = Layers.ToList();
        next[index] = layer;
        return this with { Layers = next };
    }

    public DocumentState ReorderLayer(Guid layerId, int targetIndex)
    {
        var sourceIndex = LayerIndexOf(layerId);
        if (sourceIndex < 0)
            throw new InvalidOperationException($"Layer {layerId} is not in the document.");
        ArgumentOutOfRangeException.ThrowIfNegative(targetIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(targetIndex, Layers.Count);
        if (sourceIndex == targetIndex)
            return this;
        var next = Layers.ToList();
        var layer = next[sourceIndex];
        next.RemoveAt(sourceIndex);
        next.Insert(targetIndex, layer);
        return this with { Layers = next };
    }

    /// <summary>Moves one object into another layer at the given inner index (UR-007 layer transfer).</summary>
    public DocumentState MoveAnnotationToLayer(Guid annotationId, Guid targetLayerId, int targetInnerIndex)
    {
        var sourceLayer = FindLayerOf(annotationId)
            ?? throw new InvalidOperationException($"Annotation {annotationId} is not in the document.");
        var targetLayerIndex = LayerIndexOf(targetLayerId);
        if (targetLayerIndex < 0)
            throw new InvalidOperationException($"Layer {targetLayerId} is not in the document.");
        var annotation = sourceLayer.Annotations[sourceLayer.IndexOf(annotationId)];

        var withoutSource = ReplaceLayerUnchecked(sourceLayer.Id, layer =>
        {
            var items = layer.Annotations.ToList();
            items.RemoveAt(layer.IndexOf(annotationId));
            return layer with { Annotations = items };
        });
        var target = withoutSource.FindLayer(targetLayerId)!;
        ArgumentOutOfRangeException.ThrowIfNegative(targetInnerIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(targetInnerIndex, target.Annotations.Count);
        return withoutSource.ReplaceLayerUnchecked(targetLayerId, layer =>
        {
            var items = layer.Annotations.ToList();
            items.Insert(targetInnerIndex, annotation);
            return layer with { Annotations = items };
        });
    }

    // ---- Annotation operations --------------------------------------------------------------

    /// <summary>Adds on top of the given layer, or the topmost layer when none is specified.</summary>
    public DocumentState AddAnnotation(Annotation annotation, Guid? layerId = null)
    {
        AnnotationValidator.Validate(annotation);
        ValidateAssetReference(annotation);
        if (FindLayerOf(annotation.Id) is not null)
            throw new InvalidOperationException($"Annotation {annotation.Id} is already on the layer.");
        var targetId = layerId ?? Layers[^1].Id;
        if (LayerIndexOf(targetId) < 0)
            throw new InvalidOperationException($"Layer {targetId} is not in the document.");
        return ReplaceLayerUnchecked(targetId, layer =>
            layer with { Annotations = [.. layer.Annotations, annotation] });
    }

    public DocumentState RemoveAnnotation(Guid id)
    {
        var layer = FindLayerOf(id)
            ?? throw new InvalidOperationException($"Annotation {id} is not on the layer.");
        return ReplaceLayerUnchecked(layer.Id, current =>
        {
            var items = current.Annotations.ToList();
            items.RemoveAt(current.IndexOf(id));
            return current with { Annotations = items };
        });
    }

    /// <summary>Re-inserts at a recorded (layer, index) position so an undone delete restores paint
    /// order exactly.</summary>
    public DocumentState InsertAnnotation(Guid layerId, int innerIndex, Annotation annotation)
    {
        AnnotationValidator.Validate(annotation);
        ValidateAssetReference(annotation);
        if (FindLayerOf(annotation.Id) is not null)
            throw new InvalidOperationException($"Annotation {annotation.Id} is already on the layer.");
        var layerIndex = LayerIndexOf(layerId);
        if (layerIndex < 0)
            throw new InvalidOperationException($"Layer {layerId} is not in the document.");
        ArgumentOutOfRangeException.ThrowIfNegative(innerIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(innerIndex, Layers[layerIndex].Annotations.Count);
        return ReplaceLayerUnchecked(layerId, layer =>
        {
            var items = layer.Annotations.ToList();
            items.Insert(innerIndex, annotation);
            return layer with { Annotations = items };
        });
    }

    public DocumentState ReplaceAnnotation(Annotation annotation)
    {
        AnnotationValidator.Validate(annotation);
        ValidateAssetReference(annotation);
        var layer = FindLayerOf(annotation.Id)
            ?? throw new InvalidOperationException($"Annotation {annotation.Id} is not on the layer.");
        return ReplaceLayerUnchecked(layer.Id, current =>
        {
            var items = current.Annotations.ToList();
            items[current.IndexOf(annotation.Id)] = annotation;
            return current with { Annotations = items };
        });
    }

    /// <summary>Reorders within the object's own layer (UR-007: layer z-order and in-layer object
    /// z-order are separate axes).</summary>
    public DocumentState ReorderAnnotation(Guid id, int targetInnerIndex)
    {
        var layer = FindLayerOf(id)
            ?? throw new InvalidOperationException($"Annotation {id} is not on the layer.");
        ArgumentOutOfRangeException.ThrowIfNegative(targetInnerIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(targetInnerIndex, layer.Annotations.Count);
        var sourceIndex = layer.IndexOf(id);
        if (sourceIndex == targetInnerIndex)
            return this;
        return ReplaceLayerUnchecked(layer.Id, current =>
        {
            var items = current.Annotations.ToList();
            var annotation = items[sourceIndex];
            items.RemoveAt(sourceIndex);
            items.Insert(targetInnerIndex, annotation);
            return current with { Annotations = items };
        });
    }

    public Annotation? Find(Guid id) => FindLayerOf(id) is { } layer
        ? layer.Annotations[layer.IndexOf(id)]
        : null;

    /// <summary>Flat paint index across layers, for display/diagnostic purposes only.</summary>
    public int IndexOf(Guid id)
    {
        var offset = 0;
        foreach (var layer in Layers)
        {
            var inner = layer.IndexOf(id);
            if (inner >= 0)
                return offset + inner;
            offset += layer.Annotations.Count;
        }
        return -1;
    }

    /// <summary>Visible on screen: both the object and its layer are visible.</summary>
    public bool IsEffectivelyVisible(Guid id) =>
        FindLayerOf(id) is { IsVisible: true } layer
        && layer.Annotations[layer.IndexOf(id)].IsVisible;

    /// <summary>Immutable to edits: the object or its layer is locked.</summary>
    public bool IsEffectivelyLocked(Guid id) =>
        FindLayerOf(id) is not { } layer
        || layer.IsLocked
        || layer.Annotations[layer.IndexOf(id)].IsLocked;

    public RasterAsset? FindAsset(Guid id)
    {
        for (var i = 0; i < Assets.Count; i++)
        {
            if (Assets[i].Id == id)
                return Assets[i];
        }
        return null;
    }

    public DocumentState AddAsset(RasterAsset asset)
    {
        AnnotationValidator.Validate(asset);
        if (FindAsset(asset.Id) is not null)
            throw new InvalidOperationException($"Raster asset {asset.Id} is already in the document.");
        var total = checked(Assets.Sum(item => item.EstimatedRetainedBytes) + asset.EstimatedRetainedBytes);
        if (total > AnnotationValidator.MaxRasterAssetBytes)
            throw new InvalidOperationException(
                $"Raster assets exceed the {AnnotationValidator.MaxRasterAssetBytes:N0} byte document limit.");
        return this with { Assets = [.. Assets, asset] };
    }

    public DocumentState RemoveAsset(Guid id)
    {
        if (Annotations.Any(annotation => annotation is ImageAnnotation image && image.AssetId == id))
            throw new InvalidOperationException($"Raster asset {id} is still referenced.");
        var index = Assets.ToList().FindIndex(asset => asset.Id == id);
        if (index < 0)
            throw new InvalidOperationException($"Raster asset {id} is not in the document.");
        var next = Assets.ToList();
        next.RemoveAt(index);
        return this with { Assets = next };
    }

    /// <summary>Topmost editable object at the point; hit order is the reverse of paint order.
    /// Hidden or locked layers are transparent to hits, like their objects.</summary>
    public Annotation? HitTest(float x, float y, float tolerance = 0f)
    {
        for (var l = Layers.Count - 1; l >= 0; l--)
        {
            var layer = Layers[l];
            if (!layer.IsVisible || layer.IsLocked)
                continue;
            for (var i = layer.Annotations.Count - 1; i >= 0; i--)
            {
                var annotation = layer.Annotations[i];
                if (annotation.IsVisible && !annotation.IsLocked
                    && AnnotationGeometry.HitTest(annotation, x, y, tolerance))
                    return annotation;
            }
        }
        return null;
    }

    /// <summary>Topmost single object intersecting a rubber-band; multi-selection remains FR-LAYER-005.</summary>
    public Annotation? HitTest(RectF selectionBounds)
    {
        AnnotationValidator.ValidateBounds(selectionBounds);
        for (var l = Layers.Count - 1; l >= 0; l--)
        {
            var layer = Layers[l];
            if (!layer.IsVisible || layer.IsLocked)
                continue;
            for (var i = layer.Annotations.Count - 1; i >= 0; i--)
            {
                var annotation = layer.Annotations[i];
                if (annotation.IsVisible && !annotation.IsLocked
                    && AnnotationGeometry.Intersects(annotation, selectionBounds))
                    return annotation;
            }
        }
        return null;
    }

    private DocumentState ReplaceLayerUnchecked(Guid layerId, Func<AnnotationLayer, AnnotationLayer> update)
    {
        var index = LayerIndexOf(layerId);
        var next = Layers.ToList();
        next[index] = update(next[index]);
        return this with { Layers = next };
    }

    private static void ValidateUniqueWithin(AnnotationLayer layer)
    {
        var seen = new HashSet<Guid>();
        foreach (var annotation in layer.Annotations)
        {
            if (!seen.Add(annotation.Id))
                throw new InvalidOperationException($"Annotation {annotation.Id} is duplicated in the layer.");
        }
    }

    private void ValidateAssetReference(Annotation annotation)
    {
        if (annotation is ImageAnnotation image && FindAsset(image.AssetId) is null)
            throw new InvalidOperationException($"Raster asset {image.AssetId} is not in the document.");
    }
}
