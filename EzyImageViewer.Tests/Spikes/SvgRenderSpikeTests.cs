using SkiaSharp;
using Svg.Skia;
using Xunit;

namespace EzyImageViewer.Tests.Spikes;

/// <summary>M0-B 스파이크 5: Svg.Skia가 정적 SVG를 헤드리스 렌더. 파이프라인에 스크립트 엔진 없음.</summary>
public class SvgRenderSpikeTests
{
    [Fact]
    public void SvgSkia_RendersStaticSvg()
    {
        const string svgText =
            """<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100"><rect x="0" y="0" width="100" height="100" fill="#2266DD"/></svg>""";

        using var svg = new SKSvg();
        using var picture = svg.FromSvg(svgText);
        Assert.NotNull(picture);

        using var bitmap = new SKBitmap(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.DrawPicture(picture);

        var pixel = bitmap.GetPixel(50, 50);
        Assert.True(pixel.Blue > 180 && pixel.Red < 80, $"unexpected pixel {pixel}");
    }

    [Fact]
    public void SvgSkia_ScriptContentIsInertAndStillRenders()
    {
        // 스크립트 요소는 실행되지 않고(JS 엔진 없음) 도형 렌더도 깨지면 안 됨.
        const string svgText =
            """<svg xmlns="http://www.w3.org/2000/svg" width="10" height="10"><script>while(true){}</script><rect width="10" height="10" fill="#FF0000"/></svg>""";

        using var svg = new SKSvg();
        using var picture = svg.FromSvg(svgText);
        Assert.NotNull(picture);

        using var bitmap = new SKBitmap(10, 10, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.DrawPicture(picture);
        Assert.True(bitmap.GetPixel(5, 5).Red > 180);
    }
}
