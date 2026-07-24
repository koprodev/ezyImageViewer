using System.Numerics;
using EzyImageViewer.Core.Documents.Layers;

namespace EzyImageViewer.Rendering;

public enum CropInteractionPhase
{
    Idle,
    Dragging,
    Reviewing,
}

public readonly record struct CropReview(RectF Bounds, Guid DocumentId, long Revision)
{
    public bool Contains(float x, float y) =>
        x >= Bounds.X && x <= Bounds.Right && y >= Bounds.Y && y <= Bounds.Bottom;
}

    /// <summary>명시적 확정 전까지 검토 초안을 문서 기록 밖에 보관.</summary>
public sealed class CropInteraction
{
    private Vector2 _anchor;
    private Vector2 _current;
    private Guid _dragDocumentId;
    private long _dragRevision;
    private CropReview? _reviewBeforeDrag;

    public CropInteractionPhase Phase { get; private set; }

    public CropReview? Review { get; private set; }

    public void BeginDrag(float x, float y, Guid documentId, long revision)
    {
        ValidatePoint(x, y);
        if (Phase == CropInteractionPhase.Dragging)
            throw new InvalidOperationException("A crop drag is already active.");

        _reviewBeforeDrag = Review is { } review
            && review.DocumentId == documentId
            && review.Revision == revision
                ? review
                : null;
        Review = null;
        _anchor = new Vector2(x, y);
        _current = _anchor;
        _dragDocumentId = documentId;
        _dragRevision = revision;
        Phase = CropInteractionPhase.Dragging;
    }

    public void UpdateDrag(float x, float y)
    {
        ValidatePoint(x, y);
        if (Phase == CropInteractionPhase.Dragging)
            _current = new Vector2(x, y);
    }

    public RectF? GetPreview(float? ratio, float canvasWidth, float canvasHeight)
    {
        ValidateGeometry(ratio, canvasWidth, canvasHeight);
        return Phase switch
        {
            CropInteractionPhase.Dragging => CropGeometry.Constrain(
                (_anchor.X, _anchor.Y), (_current.X, _current.Y),
                ratio, canvasWidth, canvasHeight),
            CropInteractionPhase.Reviewing => Review?.Bounds,
            _ => null,
        };
    }

    public bool CompleteDrag(float? ratio, float canvasWidth, float canvasHeight, float minimumSide = 2f)
    {
        ValidateGeometry(ratio, canvasWidth, canvasHeight);
        if (!float.IsFinite(minimumSide) || minimumSide <= 0f)
            throw new ArgumentOutOfRangeException(nameof(minimumSide));
        if (Phase != CropInteractionPhase.Dragging)
            return false;

        var bounds = CropGeometry.Constrain(
            (_anchor.X, _anchor.Y), (_current.X, _current.Y),
            ratio, canvasWidth, canvasHeight);
        var previous = _reviewBeforeDrag;
        var documentId = _dragDocumentId;
        var revision = _dragRevision;
        ClearDrag();

        if (bounds.Width < minimumSide || bounds.Height < minimumSide)
        {
            Review = previous;
            Phase = previous is null ? CropInteractionPhase.Idle : CropInteractionPhase.Reviewing;
            return false;
        }

        Review = new CropReview(bounds, documentId, revision);
        Phase = CropInteractionPhase.Reviewing;
        return true;
    }

    /// <summary>검토는 그려진 바로 그 문서 리비전에만 적용 가능.
    /// FR-EDIT-007 영역 복사도 ViewerWindow.TryCommitCropReview의 확정 게이트 공유.</summary>
    public bool TryGetValidReview(Guid documentId, long revision, out CropReview review)
    {
        if (Phase == CropInteractionPhase.Reviewing
            && Review is { } current
            && current.DocumentId == documentId
            && current.Revision == revision)
        {
            review = current;
            return true;
        }

        review = default;
        return false;
    }

    public void CancelDrag()
    {
        if (Phase != CropInteractionPhase.Dragging)
            return;

        Review = _reviewBeforeDrag;
        ClearDrag();
        Phase = Review is null ? CropInteractionPhase.Idle : CropInteractionPhase.Reviewing;
    }

    public void CancelAll()
    {
        Review = null;
        ClearDrag();
        Phase = CropInteractionPhase.Idle;
    }

    private void ClearDrag()
    {
        _anchor = default;
        _current = default;
        _dragDocumentId = default;
        _dragRevision = default;
        _reviewBeforeDrag = null;
    }

    private static void ValidatePoint(float x, float y)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y))
            throw new ArgumentOutOfRangeException(nameof(x), "Crop coordinates must be finite.");
    }

    private static void ValidateGeometry(float? ratio, float canvasWidth, float canvasHeight)
    {
        if (!float.IsFinite(canvasWidth) || !float.IsFinite(canvasHeight)
            || canvasWidth <= 0f || canvasHeight <= 0f)
            throw new ArgumentOutOfRangeException(nameof(canvasWidth), "Canvas dimensions must be positive and finite.");
        if (ratio is { } value && (!float.IsFinite(value) || value <= 0f))
            throw new ArgumentOutOfRangeException(nameof(ratio), "Crop ratio must be positive and finite.");
    }
}
