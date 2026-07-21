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
        DecodeResult initialResult,
        InputLimits limits,
        CancellationToken cancellationToken)
    {
        if (format == ImageFormat.Pdf && selectedDecoder is IPageImageDecoder pdf)
        {
            return new PdfDocumentFrameSource(
                source,
                pdf,
                initialResult.FrameCount,
                limits);
        }

        if (format == ImageFormat.Svg && selectedDecoder is SvgImageDecoder svg)
            return new SvgDocumentFrameSource(source, svg);

        // WIC exposes optimized GIF sub-rectangles as independent frames. Skia reconstructs the
        // required-frame chain, while the product's initial/static GIF decode remains on WIC.
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
