using System.Collections.Immutable;
using EzyImageViewer.Core.Commands;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Documents.Serialization;
using EzyImageViewer.Core.Imaging;
using Xunit;

namespace EzyImageViewer.Tests.Documents;

public class M4BLayerTests
{
    [Fact]
    public void ImageAssetAndAnnotation_RoundTripWithoutInliningPayloadInAnnotation()
    {
        var asset = Asset();
        var image = Image(asset.Id) with
        {
            Name = "reference",
            IsVisible = false,
            IsLocked = true,
            RotationDegrees = 37f,
        };
        var state = DocumentState.Empty.AddAsset(asset).AddAnnotation(image);

        var restored = DocumentStateSerializer.Read(DocumentStateSerializer.Write(state));

        var restoredAsset = Assert.Single(restored.Assets);
        Assert.Equal(asset.Id, restoredAsset.Id);
        Assert.Equal(asset.EncodedBytes.ToArray(), restoredAsset.EncodedBytes.ToArray());
        Assert.Equal(image, Assert.IsType<ImageAnnotation>(Assert.Single(restored.Annotations)));
    }

    [Fact]
    public void ImageAnnotation_RequiresAnAssetInTheSameState()
    {
        var asset = Asset();

        Assert.Throws<InvalidOperationException>(() =>
            DocumentState.Empty.AddAnnotation(Image(asset.Id)));
    }

    [Fact]
    public void AddImageCommand_RevertsAnnotationAndAssetExactly()
    {
        var asset = Asset();
        var image = Image(asset.Id);
        var command = new AddImageAnnotationCommand(asset, image);

        var applied = command.Apply(DocumentState.Empty);
        var reverted = command.Revert(applied);

        Assert.Same(image, Assert.Single(applied.Annotations));
        Assert.Same(asset, Assert.Single(applied.Assets));
        Assert.Empty(reverted.Annotations);
        Assert.Empty(reverted.Assets);
        Assert.True(command.EstimatedRetainedBytes >= asset.EncodedBytes.Length);
    }

    [Fact]
    public void DuplicateSharesAssetAndReorderRestoresPaintIndex()
    {
        var asset = Asset();
        var first = Image(asset.Id);
        var second = new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(20, 20, 5, 5),
        };
        var state = DocumentState.Empty.AddAsset(asset).AddAnnotation(first).AddAnnotation(second);
        var duplicate = new DuplicateAnnotationCommand(state, first.Id, Guid.NewGuid(), offset: 5f);
        var duplicated = duplicate.Apply(state);

        Assert.Equal(3, duplicated.Annotations.Count);
        Assert.Single(duplicated.Assets);
        Assert.True(duplicate.EstimatedRetainedBytes < asset.EncodedBytes.Length);

        var reorder = new ReorderAnnotationCommand(duplicated, duplicate.DuplicateId, 0);
        var reordered = reorder.Apply(duplicated);
        Assert.Equal(duplicate.DuplicateId, reordered.Annotations[0].Id);
        Assert.Equal(duplicated.Annotations, reorder.Revert(reordered).Annotations);
    }

    [Fact]
    public void RubberBandSelectsTopmostIntersectingUnlockedObject()
    {
        var bottom = new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(0, 0, 20, 20),
        };
        var top = bottom with
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(10, 10, 20, 20),
            RotationDegrees = 30f,
        };
        var locked = top with { Id = Guid.NewGuid(), IsLocked = true };
        var state = DocumentState.Empty.AddAnnotation(bottom).AddAnnotation(top).AddAnnotation(locked);

        Assert.Equal(top.Id, state.HitTest(new RectF(15, 15, 2, 2))?.Id);
    }

    [Fact]
    public void RubberBand_DoesNotIntersectADistantCollinearBoundsEdge()
    {
        var annotation = new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(0, 0, 10, 10),
        };

        Assert.False(AnnotationGeometry.Intersects(annotation, new RectF(20, 0, 5, 5)));
    }

    [Fact]
    public void ResizeAndRotate_PreserveRotationAndClampAtMinimumExtent()
    {
        var source = new RectangleAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(10, 20, 40, 20),
            RotationDegrees = 30f,
        };
        var east = SelectionGeometry.HandlePoint(source, SelectionHandle.East, 24f);
        var resized = SelectionGeometry.Resize(
            source, SelectionHandle.East, new AnnotationPoint(east.X + 20f, east.Y + 10f));
        var clamped = SelectionGeometry.Resize(
            source, SelectionHandle.East, new AnnotationPoint(-1000f, -1000f));
        var rotated = SelectionGeometry.Rotate(source, new AnnotationPoint(50f, 40f));

        Assert.Equal(source.RotationDegrees, resized.RotationDegrees);
        Assert.True(resized.Bounds.Width > source.Bounds.Width);
        Assert.Equal(SelectionGeometry.MinimumExtent, clamped.Bounds.Width);
        Assert.InRange(rotated.RotationDegrees, 0f, 360f);
    }

    private static RasterAsset Asset() => new()
    {
        Id = Guid.NewGuid(),
        EncodedBytes = Enumerable.Repeat((byte)0x5A, 1024).ToImmutableArray(),
        PixelSize = new PixelSize(8, 6),
        Format = "png",
    };

    private static ImageAnnotation Image(Guid assetId) => new()
    {
        Id = Guid.NewGuid(),
        AssetId = assetId,
        Bounds = new RectF(0, 0, 8, 6),
    };
}
