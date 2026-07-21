using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class RecoverySourceFallbackPolicyTests
{
    [Fact]
    public void FullResolutionBoundedSingleFrame_CanUseRenderedFallback()
    {
        var size = new PixelSize(4_000, 3_000);

        Assert.True(RecoverySourceFallbackPolicy.CanEmbedRenderedBackground(
            DocumentSequenceKind.SingleFrame,
            isReducedPreview: false,
            size,
            size));
    }

    [Theory]
    [InlineData(DocumentSequenceKind.Pages, false, 100, 100, 100, 100)]
    [InlineData(DocumentSequenceKind.Animation, false, 100, 100, 100, 100)]
    [InlineData(DocumentSequenceKind.ScalableVector, false, 100, 100, 100, 100)]
    [InlineData(DocumentSequenceKind.SingleFrame, true, 100, 100, 100, 100)]
    [InlineData(DocumentSequenceKind.SingleFrame, false, 100, 100, 50, 50)]
    [InlineData(DocumentSequenceKind.SingleFrame, false, 10_000, 10_000, 10_000, 10_000)]
    public void FidelityOrMemoryRisk_RejectsRenderedFallback(
        DocumentSequenceKind kind,
        bool isReduced,
        int nativeWidth,
        int nativeHeight,
        int decodedWidth,
        int decodedHeight)
    {
        Assert.False(RecoverySourceFallbackPolicy.CanEmbedRenderedBackground(
            kind,
            isReduced,
            new PixelSize(nativeWidth, nativeHeight),
            new PixelSize(decodedWidth, decodedHeight)));
    }
}
