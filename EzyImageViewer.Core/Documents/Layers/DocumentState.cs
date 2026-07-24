namespace EzyImageViewer.Core.Documents.Layers;

/// <summary>
/// 불변 문서 편집 상태. 레이어와 내부 객체 순서가 그리기 순서를 결정.
/// 활성 레이어는 창 UI 상태이며 배경 래스터는 원본 문서가 소유.
/// </summary>
public sealed record DocumentState
{
    public static DocumentState Empty { get; } = new();

    /// <summary>그리기 순서. 0번이 맨 뒤이며 비어 있지 않음.</summary>
    public IReadOnlyList<AnnotationLayer> Layers { get; init; } =
        [new AnnotationLayer { Id = AnnotationLayer.InitialLayerId }];

    /// <summary>숨김 포함 전체 레이어의 평탄화된 그리기 순서.</summary>
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

    /// <summary>인코딩 래스터는 한 번 소유하고 이미지 주석이 참조.</summary>
    public IReadOnlyList<RasterAsset> Assets { get; init; } = [];

    /// <summary>배경 변환 파이프라인. 주석도 같은 행렬을 타서 이미지에 붙어 다님.</summary>
    public BackgroundTransform Transform { get; init; } = BackgroundTransform.Identity;

    public DocumentState WithTransform(BackgroundTransform transform)
    {
        ArgumentNullException.ThrowIfNull(transform);
        return this with { Transform = transform };
    }

    // ---- 레이어 작업 ------------------------------------------------------------------------

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

    /// <summary>주석이 든 레이어. 없으면 null.</summary>
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

    /// <summary>레이어와 객체 제거. 마지막 레이어는 작업 자리라 제거 불가.</summary>
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

    /// <summary>레이어 속성만 교체. 객체 목록 변경은 전용 작업으로만 처리.</summary>
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

    /// <summary>객체 하나를 다른 레이어의 지정 위치로 이동.</summary>
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

    // ---- 주석 작업 --------------------------------------------------------------------------

    /// <summary>지정 레이어 맨 위에 추가. 미지정이면 최상단 레이어 사용.</summary>
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

    /// <summary>기록한 레이어·위치에 재삽입해 삭제 취소 시 순서까지 복원.</summary>
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

    /// <summary>객체가 속한 레이어 안에서만 순서 변경.</summary>
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

    /// <summary>표시·진단용 전체 레이어 평탄화 순번.</summary>
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

    /// <summary>객체와 레이어가 모두 보이면 true.</summary>
    public bool IsEffectivelyVisible(Guid id) =>
        FindLayerOf(id) is { IsVisible: true } layer
        && layer.Annotations[layer.IndexOf(id)].IsVisible;

    /// <summary>객체나 레이어가 잠겨 편집 불가면 true.</summary>
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

    /// <summary>점에 걸린 최상단 편집 가능 객체. 숨김·잠금 레이어는 통과.</summary>
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

    /// <summary>고무줄 선택과 겹친 최상단 단일 객체.</summary>
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
