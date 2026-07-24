using EzyImageViewer.Core.Commands;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using Xunit;

namespace EzyImageViewer.Tests.Documents;

/// <summary>UR-009: 지우기 연산 + 자산 + 주석을 실행 취소 항목 하나로 원자 처리.</summary>
public class LiftRegionCommandTests
{
    private static RasterAsset Asset(Guid id) => new()
    {
        Id = id,
        EncodedBytes = [1, 2, 3],
        PixelSize = new PixelSize(4, 4),
        Format = "Png",
    };

    private static (LiftRegionCommand Command, Guid AssetId, Guid AnnotationId) Command(
        BackgroundTransform before)
    {
        var assetId = Guid.NewGuid();
        var annotation = new ImageAnnotation
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            Bounds = new RectF(1f, 2f, 4f, 4f),
        };
        var command = new LiftRegionCommand(
            Asset(assetId), annotation, null, before, new EraseOp(new RectF(1f, 2f, 4f, 4f)));
        return (command, assetId, annotation.Id);
    }

    [Fact]
    public void ApplyThenRevert_RestoresTransformAssetAndAnnotation()
    {
        var (command, assetId, annotationId) = Command(BackgroundTransform.Identity);

        var applied = command.Apply(DocumentState.Empty);
        Assert.Single(applied.Transform.Ops);
        Assert.IsType<EraseOp>(applied.Transform.Ops[0]);
        Assert.NotNull(applied.FindAsset(assetId));
        Assert.NotNull(applied.Find(annotationId));

        var reverted = command.Revert(applied);
        Assert.True(reverted.Transform.IsIdentity);
        Assert.Null(reverted.FindAsset(assetId));
        Assert.Null(reverted.Find(annotationId));
    }

    [Fact]
    public void Apply_AgainstADifferentTransform_Throws()
    {
        var (command, _, _) = Command(BackgroundTransform.Identity);
        var drifted = DocumentState.Empty.WithTransform(
            BackgroundTransform.Identity.Append(new FlipOp(Horizontal: true)));

        Assert.Throws<InvalidOperationException>(() => command.Apply(drifted));
    }

    [Fact]
    public void Revert_AgainstTheWrongTransform_Throws()
    {
        var (command, _, _) = Command(BackgroundTransform.Identity);

        Assert.Throws<InvalidOperationException>(() => command.Revert(DocumentState.Empty));
    }

    [Fact]
    public void MismatchedAssetReference_IsRejectedAtConstruction()
    {
        var annotation = new ImageAnnotation
        {
            Id = Guid.NewGuid(),
            AssetId = Guid.NewGuid(),
            Bounds = new RectF(0f, 0f, 2f, 2f),
        };

        Assert.Throws<ArgumentException>(() => new LiftRegionCommand(
            Asset(Guid.NewGuid()), annotation, null,
            BackgroundTransform.Identity, new EraseOp(new RectF(0f, 0f, 2f, 2f))));
    }
}
