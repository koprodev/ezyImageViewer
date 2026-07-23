using EzyImageViewer.Core.Documents.Layers;

namespace EzyImageViewer.Core.Commands;

/// <summary>
/// One undo entry for the region lift (UR-009): appends the erase op that vacates the source rect
/// AND adds the lifted pixels as a raster-asset annotation. Both endpoints verify the transform
/// they run against, like <see cref="TransformCommand"/>, so a state swapped underneath fails
/// loudly instead of corrupting.
/// </summary>
public sealed class LiftRegionCommand : IEditCommand
{
    private readonly RasterAsset _asset;
    private readonly ImageAnnotation _annotation;
    private readonly Guid? _layerId;
    private readonly BackgroundTransform _before;
    private readonly BackgroundTransform _after;

    public LiftRegionCommand(
        RasterAsset asset,
        ImageAnnotation annotation,
        Guid? layerId,
        BackgroundTransform before,
        EraseOp erase)
    {
        _asset = AnnotationValidator.Validate(asset);
        _annotation = (ImageAnnotation)AnnotationValidator.Validate(annotation);
        if (_annotation.AssetId != _asset.Id)
            throw new ArgumentException("Image annotation must reference the supplied asset.", nameof(annotation));
        _layerId = layerId;
        _before = before ?? throw new ArgumentNullException(nameof(before));
        ArgumentNullException.ThrowIfNull(erase);
        _after = before.Append(erase);
    }

    public string Name => "LiftRegion";

    public long EstimatedRetainedBytes => checked(
        _asset.EstimatedRetainedBytes + _annotation.EstimatedRetainedBytes
        + _before.EstimatedRetainedBytes + _after.EstimatedRetainedBytes);

    public object? MergeKey => null;

    public DocumentState Apply(DocumentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!_before.Equals(state.Transform))
            throw new InvalidOperationException("Lift command does not match the state it runs against.");
        if (state.FindAsset(_asset.Id) is not null)
            throw new InvalidOperationException($"Raster asset {_asset.Id} is already in the document.");
        return state.WithTransform(_after).AddAsset(_asset).AddAnnotation(_annotation, _layerId);
    }

    public DocumentState Revert(DocumentState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!_after.Equals(state.Transform))
            throw new InvalidOperationException("Lift command does not match the state it runs against.");
        return state.RemoveAnnotation(_annotation.Id).RemoveAsset(_asset.Id).WithTransform(_before);
    }
}
