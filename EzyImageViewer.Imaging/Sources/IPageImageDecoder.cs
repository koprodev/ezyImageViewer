using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Imaging.Sources;

internal interface IPageImageDecoder
{
    Task<DecodeResult> DecodePageAsync(
        Stream stream,
        int pageIndex,
        DecodeRequest request,
        CancellationToken cancellationToken);
}
