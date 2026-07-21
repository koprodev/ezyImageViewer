using EzyImageViewer.CodecProtocol;
using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging.Sources;

namespace EzyImageViewer.Imaging.Codecs;

internal sealed class IsolatedCodecImageDecoder(
    IIsolatedDocumentCodecClient client,
    CodecFormat format) : IImageDecoder, IPageImageDecoder
{
    private readonly IIsolatedDocumentCodecClient _client = client
        ?? throw new ArgumentNullException(nameof(client));
    private readonly CodecFormat _format = format is CodecFormat.Pdf or CodecFormat.Psd
        ? format
        : throw new ArgumentOutOfRangeException(nameof(format));

    public Task<DecodeResult> DecodeAsync(
        Stream stream,
        DecodeRequest request,
        CancellationToken cancellationToken) =>
        DecodePageAsync(stream, 0, request, cancellationToken);

    public async Task<DecodeResult> DecodePageAsync(
        Stream stream,
        int pageIndex,
        DecodeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var firstTarget = request.PreferredMaxDimension is { } preferred
            ? Math.Clamp(preferred, 1, request.Limits.MaxDimension)
            : 0;
        var decoded = await _client.DecodeAsync(
                stream,
                _format,
                pageIndex,
                firstTarget,
                cancellationToken)
            .ConfigureAwait(false);
        var target = ValidateAndResolveTarget(decoded, request);
        if (target > 0 && Math.Max(decoded.Width, decoded.Height) > target)
        {
            var firstMetadata = (decoded.NativeSize, decoded.PageCount);
            decoded = await _client.DecodeAsync(
                    stream,
                    _format,
                    pageIndex,
                    target,
                    cancellationToken)
                .ConfigureAwait(false);
            if ((decoded.NativeSize, decoded.PageCount) != firstMetadata)
                throw new CodecUnavailableException("The isolated codec returned inconsistent document metadata.");
            _ = ValidateAndResolveTarget(decoded, request);
        }

        var expectedPixelLength = checked(decoded.Stride * decoded.Height);
        if (decoded.PixelLength != expectedPixelLength
            || decoded.Pixels.Length < decoded.PixelLength)
        {
            throw new InvalidDataException(
                "The isolated codec returned an inconsistent pixel-buffer length.");
        }
        var hasAlpha = PixelAnalysis.HasTransparency(
            decoded.Pixels,
            decoded.Stride,
            decoded.Width,
            decoded.Height);
        return new DecodeResult(
            new DecodedFrame(
                decoded.Pixels,
                decoded.PixelLength,
                decoded.Width,
                decoded.Height,
                decoded.Stride,
                hasAlpha),
            decoded.Width < decoded.NativeSize.Width || decoded.Height < decoded.NativeSize.Height,
            decoded.NativeSize,
            decoded.PageCount,
            MapHostDiagnostic(decoded.Diagnostic));
    }

    private IReadOnlyList<DocumentDiagnostic> MapHostDiagnostic(string? diagnostic)
    {
        if (_format != CodecFormat.Psd || diagnostic is null)
            return [];

        return diagnostic switch
        {
            "psd-composite-cmyk-to-srgb" =>
            [
                new DocumentDiagnostic(
                    "PSD_CMYK_TO_SRGB",
                    DocumentDiagnosticSeverity.Information,
                    "PSD CMYK color was converted to sRGB for display."),
            ],
            "psd-composite-lab-to-srgb" =>
            [
                new DocumentDiagnostic(
                    "PSD_LAB_TO_SRGB",
                    DocumentDiagnosticSeverity.Information,
                    "PSD Lab color was converted to sRGB for display."),
            ],
            "psd-composite-multichannel-spot-to-srgb"
                or "psd-composite-duotone-spot-to-srgb" =>
            [
                new DocumentDiagnostic(
                    "PSD_SPOT_TO_SRGB",
                    DocumentDiagnosticSeverity.Warning,
                    "PSD spot colors were converted to sRGB for display and may differ from the source."),
            ],
            _ => [],
        };
    }

    private static int ValidateAndResolveTarget(
        IsolatedCodecDecodedImage decoded,
        DecodeRequest request)
    {
        if (decoded.PageCount <= 0 || decoded.PageCount > request.Limits.MaxFrameCount)
            throw new SecurityLimitExceededException("The document page count exceeds the configured limit.");

        var nativeMax = Math.Max(decoded.NativeSize.Width, decoded.NativeSize.Height);
        var desiredMax = request.PreferredMaxDimension is { } preferred
            ? Math.Clamp(preferred, 1, request.Limits.MaxDimension)
            : nativeMax;
        var desiredScale = (double)desiredMax / nativeMax;
        var desiredWidth = Math.Max(
            1,
            checked((int)Math.Ceiling(decoded.NativeSize.Width * desiredScale)));
        var desiredHeight = Math.Max(
            1,
            checked((int)Math.Ceiling(decoded.NativeSize.Height * desiredScale)));
        var plan = request.Limits.PlanDimensions(desiredWidth, desiredHeight);
        if (plan.Action == DecodeAction.Reject)
            throw new SecurityLimitExceededException(plan.RejectReason!);
        return plan.Action == DecodeAction.DecodeScaled
            ? plan.TargetMaxDimension
            : desiredMax;
    }
}
