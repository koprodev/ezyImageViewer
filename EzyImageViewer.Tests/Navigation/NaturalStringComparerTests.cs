using EzyImageViewer.Core.Navigation;
using Xunit;

namespace EzyImageViewer.Tests.Navigation;

public class NaturalStringComparerTests
{
    private static int Compare(string x, string y) => NaturalStringComparer.Instance.Compare(x, y);

    [Theory]
    [InlineData("image2.png", "image10.png")]
    [InlineData("img002.png", "img010.png")]
    [InlineData("1.png", "02.png")]
    [InlineData("a.png", "b.png")]
    [InlineData("가1.png", "가2.png")]
    [InlineData("img10a.png", "img10b.png")]
    public void Compare_OrdersNaturally(string smaller, string larger)
    {
        Assert.True(Compare(smaller, larger) < 0, $"{smaller} should sort before {larger}");
        Assert.True(Compare(larger, smaller) > 0);
    }

    [Fact]
    public void Compare_IsCaseInsensitive()
    {
        Assert.Equal(0, Compare("Image1.PNG", "image1.png"));
    }

    [Fact]
    public void Compare_NumericTie_FewerLeadingZerosFirst()
    {
        Assert.True(Compare("7.png", "007.png") < 0);
    }

    [Fact]
    public void Compare_SortsFullListLikeExplorer()
    {
        List<string> files = ["image10.png", "image2.png", "image1.png", "album.png", "image10-b.png"];
        files.Sort((a, b) => Compare(a, b));
        Assert.Equal(["album.png", "image1.png", "image2.png", "image10-b.png", "image10.png"], files);
    }

    [Fact]
    public void Compare_HandlesNulls()
    {
        Assert.True(NaturalStringComparer.Instance.Compare(null, "a") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("a", null) > 0);
        Assert.Equal(0, NaturalStringComparer.Instance.Compare(null, null));
    }
}
