using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging.Skia;
using EzyImageViewer.Imaging.Svg;
using EzyImageViewer.Imaging.Wic;

namespace EzyImageViewer.Imaging.Sources;

internal static class DocumentFrameSourceFactory
{
    public static async Task<IDocumentFrameSource?> TryCreateAsync(
        ImageFormat format,
        IEncodedSource source,
        IImageDecoder selectedDecoder,
        IImageDecoder animationDecoder,
        InputLimits limits,
        CancellationToken cancellationToken)
    {
        if (format == ImageFormat.Svg && selectedDecoder is SvgImageDecoder svg)
            return new SvgDocumentFrameSource(source, svg);

            // WIC는 최적화된 GIF 부분 사각형을 독립 프레임으로 노출.
            // 필요한 프레임 연쇄는 Skia가 복원하고 최초·정적 GIF 해석은 WIC 유지.
        if (format == ImageFormat.Gif && animationDecoder is SkiaImageDecoder gifAnimation)
            return SkiaDocumentFrameSource.TryCreate(source, gifAnimation, limits);

        if (format is ImageFormat.Tiff or ImageFormat.Ico
            or ImageFormat.Avif or ImageFormat.Heif
            && selectedDecoder is WicImageDecoder wic)
        {
            return await WicDocumentFrameSource.TryCreateAsync(
                    source, wic, format, limits, cancellationToken)
                .ConfigureAwait(false);
        }

        if (format == ImageFormat.WebP && selectedDecoder is SkiaImageDecoder skia)
            return SkiaDocumentFrameSource.TryCreate(source, skia, limits);

        return null;
    }
}
