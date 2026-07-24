using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Rendering;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

public class CropInteractionTests
{
    private static readonly Guid DocumentId = Guid.NewGuid();

    [Fact]
    public void ValidRelease_EntersReviewing_AndLaterCaptureLossIsANoOp()
    {
        var interaction = new CropInteraction();
        interaction.BeginDrag(10f, 20f, DocumentId, 7);
        interaction.UpdateDrag(70f, 60f);

        Assert.True(interaction.CompleteDrag(null, 100f, 100f));
        interaction.CancelDrag();

        Assert.Equal(CropInteractionPhase.Reviewing, interaction.Phase);
        Assert.Equal(new RectF(10f, 20f, 60f, 40f), interaction.Review?.Bounds);
    }

    [Fact]
    public void DegenerateReplacementClick_PreservesExistingReview()
    {
        var interaction = Reviewed();
        var original = interaction.Review;

        interaction.BeginDrag(25f, 25f, DocumentId, 7);

        Assert.False(interaction.CompleteDrag(null, 100f, 100f));
        Assert.Equal(original, interaction.Review);
        Assert.Equal(CropInteractionPhase.Reviewing, interaction.Phase);
    }

    [Fact]
    public void AbnormalCaptureLoss_RestoresExistingReview()
    {
        var interaction = Reviewed();
        var original = interaction.Review;
        interaction.BeginDrag(80f, 80f, DocumentId, 7);
        interaction.UpdateDrag(95f, 95f);

        interaction.CancelDrag();

        Assert.Equal(original, interaction.Review);
        Assert.Equal(CropInteractionPhase.Reviewing, interaction.Phase);
    }

    [Fact]
    public void AbnormalCaptureLoss_FromIdle_ReturnsToIdle()
    {
        var interaction = new CropInteraction();
        interaction.BeginDrag(10f, 10f, DocumentId, 7);

        interaction.CancelDrag();

        Assert.Null(interaction.Review);
        Assert.Equal(CropInteractionPhase.Idle, interaction.Phase);
    }

    [Fact]
    public void NewValidDrag_ReplacesReviewAndIdentity()
    {
        var interaction = Reviewed();
        var successor = Guid.NewGuid();
        interaction.BeginDrag(40f, 40f, successor, 11);
        interaction.UpdateDrag(90f, 80f);

        Assert.True(interaction.CompleteDrag(null, 100f, 100f));

        Assert.Equal(new RectF(40f, 40f, 50f, 40f), interaction.Review?.Bounds);
        Assert.Equal(successor, interaction.Review?.DocumentId);
        Assert.Equal(11, interaction.Review?.Revision);
    }

    [Fact]
    public void CancelAll_DiscardsDraggingAndReviewingState()
    {
        var interaction = Reviewed();

        interaction.CancelAll();

        Assert.Equal(CropInteractionPhase.Idle, interaction.Phase);
        Assert.Null(interaction.Review);
        Assert.Null(interaction.GetPreview(null, 100f, 100f));
    }

    [Fact]
    public void ReviewContainment_IncludesItsBorder()
    {
        var review = new CropReview(new RectF(10f, 20f, 30f, 40f), DocumentId, 7);

        Assert.True(review.Contains(10f, 20f));
        Assert.True(review.Contains(40f, 60f));
        Assert.False(review.Contains(40.1f, 60f));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-1f)]
    public void CompleteDrag_RejectsNonPositiveMinimumSide(float minimumSide)
    {
        var interaction = new CropInteraction();
        interaction.BeginDrag(10f, 10f, DocumentId, 7);
        interaction.UpdateDrag(50f, 50f);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            interaction.CompleteDrag(null, 100f, 100f, minimumSide));
    }

    [Fact]
    public void TryGetValidReview_MatchingDocumentAndRevision_ReturnsBounds()
    {
        var interaction = Reviewed();

        Assert.True(interaction.TryGetValidReview(DocumentId, 7, out var review));
        Assert.Equal(new RectF(10f, 10f, 40f, 40f), review.Bounds);
    }

    [Fact]
    public void TryGetValidReview_StaleDocumentOrRevision_IsFalse()
    {
        var interaction = Reviewed();

        Assert.False(interaction.TryGetValidReview(Guid.NewGuid(), 7, out _));
        Assert.False(interaction.TryGetValidReview(DocumentId, 8, out _));
        // 오래된 상태 확인이 검토 초안 자체를 건드리면 안 됨.
        Assert.Equal(CropInteractionPhase.Reviewing, interaction.Phase);
        Assert.NotNull(interaction.Review);
    }

    [Fact]
    public void TryGetValidReview_OutsideReviewingPhase_IsFalse()
    {
        var idle = new CropInteraction();
        Assert.False(idle.TryGetValidReview(DocumentId, 7, out _));

        var dragging = new CropInteraction();
        dragging.BeginDrag(10f, 10f, DocumentId, 7);
        Assert.False(dragging.TryGetValidReview(DocumentId, 7, out _));
    }

    private static CropInteraction Reviewed()
    {
        var interaction = new CropInteraction();
        interaction.BeginDrag(10f, 10f, DocumentId, 7);
        interaction.UpdateDrag(50f, 50f);
        Assert.True(interaction.CompleteDrag(null, 100f, 100f));
        return interaction;
    }
}
