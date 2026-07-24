using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Rendering;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

/// <summary>
/// M6 내보내기 계약(FR-OUT-003~005).
/// 평탄화기는 축소 미리보기와 원본 해상도에서 문서 좌표를 유지하고 인코더는 알파·품질 계약 준수.
/// Skia 재인코딩은 원본 메타데이터를 옮기지 않음(FR-OUT-008 기본 제거).
/// </summary>
public sealed class ExportPipelineTests
{
    private static readonly SKColor Red = new(0xFF, 0x00, 0x00, 0xFF);
    private static readonly SKColor Green = new(0x00, 0xFF, 0x00, 0xFF);

    private static SKImage HalvesImage(int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(
            width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var paint = new SKPaint();
        paint.Color = Red;
        surface.Canvas.DrawRect(SKRect.Create(0, 0, width / 2f, height), paint);
        paint.Color = Green;
        surface.Canvas.DrawRect(SKRect.Create(width / 2f, 0, width / 2f, height), paint);
        return surface.Snapshot();
    }

    [Fact]
    public void Flatten_KeepsDocumentCoordinates_AcrossReducedAndFullFrames()
    {
        // 같은 16×16 원본 문서를 전체 프레임과 8×8 축소 미리보기로 각각 처리.
        // 어느 쪽이든 가리기 주석이 같은 출력 픽셀에 놓여야 함(M6 정렬 계약).
        var native = new PixelSize(16, 16);
        var state = DocumentState.Empty.AddAnnotation(new ProtectionAnnotation
        {
            Id = Guid.NewGuid(),
            Bounds = new RectF(4, 4, 8, 8),
            Kind = ProtectionKind.Mask,
            MaskArgb = 0xFF11_2233,
        });
        using var fullFrame = HalvesImage(16, 16);
        using var reducedFrame = HalvesImage(8, 8);

        using var full = DocumentFlattener.Flatten(fullFrame, native, state);
        using var reduced = DocumentFlattener.Flatten(reducedFrame, native, state);
        using var fullBitmap = SKBitmap.FromImage(full);
        using var reducedBitmap = SKBitmap.FromImage(reduced);

        Assert.Equal(16, full.Width);
        Assert.Equal(16, reduced.Width);
        var mask = new SKColor(0x11, 0x22, 0x33, 0xFF);
        foreach (var (x, y) in new[] { (4, 4), (8, 8), (11, 11) })
        {
            Assert.Equal(mask, fullBitmap.GetPixel(x, y));
            Assert.Equal(mask, reducedBitmap.GetPixel(x, y));
        }
        Assert.Equal(Red, fullBitmap.GetPixel(1, 1));
        Assert.Equal(Red, reducedBitmap.GetPixel(1, 1));
        Assert.Equal(Green, fullBitmap.GetPixel(14, 14));
        Assert.Equal(Green, reducedBitmap.GetPixel(14, 14));
    }

    private static SKImage ImageWithTransparentLeftHalf()
    {
        using var surface = SKSurface.Create(new SKImageInfo(
            8, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = Red };
        surface.Canvas.DrawRect(SKRect.Create(4, 0, 4, 8), paint);
        return surface.Snapshot();
    }

    [Fact]
    public void Png_KeepsTransparency()
    {
        using var image = ImageWithTransparentLeftHalf();
        var bytes = ImageExporter.Encode(image, ExportFormat.Png);
        using var decoded = SKBitmap.Decode(bytes);

        Assert.Equal(0, decoded.GetPixel(1, 4).Alpha);
        Assert.Equal(Red, decoded.GetPixel(6, 4));
    }

    [Fact]
    public void Jpeg_CompositesTransparencyOverTheBackgroundColor()
    {
        using var image = ImageWithTransparentLeftHalf();
        var bytes = ImageExporter.Encode(image, ExportFormat.Jpeg, new ExportOptions
        {
            Quality = 95,
            JpegBackgroundArgb = 0xFFFF_FFFF,
        });
        using var decoded = SKBitmap.Decode(bytes);

        var background = decoded.GetPixel(1, 4);
        Assert.Equal(0xFF, background.Alpha);
        Assert.True(background.Red > 240 && background.Green > 240 && background.Blue > 240,
            $"transparent area should flatten to white, got {background}");
    }

    [Fact]
    public void Preflight_RefusesAnOutputBeyondTheByteBudget_BeforeAnyAllocation()
    {
        // 크기 변경은 원본 픽셀 수보다 큰 출력을 합법적으로 요구할 수 있음.
        // 40000×20000 × 4B/px ≈ 3.2GB로 2GiB 평탄화 예산 초과. 시작 전에 실패해야 함.
        var native = new PixelSize(16, 16);
        var huge = new DocumentState
        {
            Transform = BackgroundTransform.Identity.Append(new ResizeOp(new PixelSize(40_000, 20_000))),
        };
        using var frame = HalvesImage(16, 16);

        var ex = Assert.Throws<InvalidOperationException>(
            () => DocumentFlattener.PreflightOutputSize(huge, native));
        Assert.Contains("byte budget", ex.Message);
        Assert.Throws<InvalidOperationException>(
            () => DocumentFlattener.Flatten(frame, native, huge));

        // 항등 파이프라인은 통과하고 실제 출력 크기를 보고.
        Assert.Equal(native, DocumentFlattener.PreflightOutputSize(DocumentState.Empty, native));
    }

    [Fact]
    public void WebP_LossyDecodes_AndLosslessRoundTripsExactly()
    {
        using var image = HalvesImage(16, 16);
        var lossy = ImageExporter.Encode(image, ExportFormat.WebP, new ExportOptions { Quality = 80 });
        using var lossyDecoded = SKBitmap.Decode(lossy);
        Assert.Equal(16, lossyDecoded.Width);

        var lossless = ImageExporter.Encode(
            image, ExportFormat.WebP, new ExportOptions { WebPLossless = true });
        using var losslessDecoded = SKBitmap.Decode(lossless);
        Assert.Equal(Red, losslessDecoded.GetPixel(2, 8));
        Assert.Equal(Green, losslessDecoded.GetPixel(13, 8));
    }
}
