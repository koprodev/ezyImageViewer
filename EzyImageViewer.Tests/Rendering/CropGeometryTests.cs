using EzyImageViewer.Rendering;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

/// <summary>
/// FR-EDIT-001 fixed-ratio semantics: the ratio must survive the canvas edge. Clamping the axes
/// independently after fitting the ratio silently produced non-ratio crops — the regression that
/// motivated solving constraint and containment together.
/// </summary>
public class CropGeometryTests
{
    private const float Square = 1f;

    [Fact]
    public void FreeRatio_ClampsEachAxisToTheCanvas()
    {
        var rect = CropGeometry.Constrain((90f, 50f), (150f, 110f), null, 100f, 100f);
        Assert.Equal(90f, rect.X);
        Assert.Equal(50f, rect.Y);
        Assert.Equal(10f, rect.Width);
        Assert.Equal(50f, rect.Height);
    }

    [Fact]
    public void FixedRatio_SurvivesTheCanvasEdge()
    {
        // The regression case: anchor (90,50), dragging 1:1 to (150,110) — only 10px remain to the
        // right, so the square shrinks to 10×10 instead of degrading to 10×50.
        var rect = CropGeometry.Constrain((90f, 50f), (150f, 110f), Square, 100f, 100f);
        Assert.Equal(rect.Width, rect.Height);
        Assert.Equal(10f, rect.Width);
        Assert.Equal(90f, rect.X);
        Assert.Equal(50f, rect.Y);
    }

    [Theory]
    [InlineData(1f, -200f, -200f)]   // up-left
    [InlineData(1f, 300f, -200f)]    // up-right
    [InlineData(1f, -200f, 300f)]    // down-left
    [InlineData(1f, 300f, 300f)]     // down-right
    [InlineData(4f / 3f, 300f, 300f)]
    [InlineData(16f / 9f, -200f, 300f)]
    public void FixedRatio_HoldsInEveryDirection_EvenDraggedFarOutside(float ratio, float px, float py)
    {
        var rect = CropGeometry.Constrain((50f, 50f), (px, py), ratio, 100f, 100f);

        Assert.Equal(ratio, rect.Width / rect.Height, 3);
        Assert.True(rect.X >= 0f && rect.Y >= 0f);
        Assert.True(rect.Right <= 100f && rect.Bottom <= 100f);
    }

    [Fact]
    public void WideRatio_LimitedByHeight_ShrinksTheWidthWithIt()
    {
        // 16:9 from (0,90) downward: only 10px of height remain, so width follows to 17.78.
        var rect = CropGeometry.Constrain((0f, 90f), (100f, 200f), 16f / 9f, 100f, 100f);
        Assert.Equal(10f, rect.Height);
        Assert.Equal(160f / 9f, rect.Width, 2);
        Assert.True(rect.Right <= 100f);
    }

    [Fact]
    public void AnchorOnTheEdge_DraggedOutward_IsDegenerate()
    {
        var rect = CropGeometry.Constrain((100f, 50f), (150f, 80f), Square, 100f, 100f);
        Assert.Equal(0f, rect.Width);
        Assert.Equal(0f, rect.Height);
    }

    [Fact]
    public void InteriorDrag_IsUntouched()
    {
        var rect = CropGeometry.Constrain((10f, 20f), (70f, 50f), null, 100f, 100f);
        Assert.Equal(new EzyImageViewer.Core.Documents.Layers.RectF(10f, 20f, 60f, 30f), rect);
    }
}
