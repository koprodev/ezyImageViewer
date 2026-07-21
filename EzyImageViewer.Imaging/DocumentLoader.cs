using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using System.IO.Compression;
using EzyImageViewer.CodecProtocol;
using EzyImageViewer.Imaging.Codecs;
using EzyImageViewer.Imaging.Skia;
using EzyImageViewer.Imaging.Sources;
using EzyImageViewer.Imaging.Svg;
using EzyImageViewer.Imaging.Wic;

namespace EzyImageViewer.Imaging;

/// <summary>
/// Sniff-first document loading with a fixed dispatch table (§8.5): a decode failure is final for
/// that format — the file is never re-interpreted by another decoder.
/// </summary>
public sealed class DocumentLoader
{
    private readonly IImageDecoder _wic;
    private readonly IImageDecoder _skia;
    private readonly IImageDecoder _svg;
    private readonly IWicCodecCatalog _wicCodecs;
    private readonly IImageDecoder? _pdf;
    private readonly IImageDecoder? _psd;
    private readonly string? _isolatedCodecVersion;
    private readonly InputLimits _limits;

    public DocumentLoader(InputLimits? limits = null)
        : this(
            limits,
            new WicImageDecoder(),
            new SkiaImageDecoder(),
            new SvgImageDecoder(),
            new WicCodecCatalog(),
            isolatedCodec: null)
    {
    }

    public DocumentLoader(
        InputLimits? limits,
        IsolatedCodecHostConfiguration isolatedCodecHost)
        : this(
            limits,
            new WicImageDecoder(),
            new SkiaImageDecoder(),
            new SvgImageDecoder(),
            new WicCodecCatalog(),
            new IsolatedDocumentCodecClient(isolatedCodecHost))
    {
    }

    internal DocumentLoader(InputLimits? limits, IImageDecoder wic, IImageDecoder skia)
        : this(limits, wic, skia, new SvgImageDecoder(), new WicCodecCatalog(), isolatedCodec: null)
    {
    }

    internal DocumentLoader(
        InputLimits? limits,
        IImageDecoder wic,
        IImageDecoder skia,
        IImageDecoder svg,
        IWicCodecCatalog wicCodecs)
        : this(limits, wic, skia, svg, wicCodecs, isolatedCodec: null)
    {
    }

    internal DocumentLoader(
        InputLimits? limits,
        IImageDecoder wic,
        IImageDecoder skia,
        IImageDecoder svg,
        IWicCodecCatalog wicCodecs,
        IIsolatedDocumentCodecClient? isolatedCodec)
    {
        _limits = limits ?? InputLimits.Default;
        _wic = wic;
        _skia = skia;
        _svg = svg;
        _wicCodecs = wicCodecs;
        _pdf = isolatedCodec is null
            ? null
            : new IsolatedCodecImageDecoder(isolatedCodec, CodecFormat.Pdf);
        _psd = isolatedCodec is null
            ? null
            : new IsolatedCodecImageDecoder(isolatedCodec, CodecFormat.Psd);
        _isolatedCodecVersion = isolatedCodec?.RendererVersion;
    }

    public async Task<ImageDocument> LoadFileAsync(string path, CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(path);
        if (!fileInfo.Exists)
            throw new FileNotFoundException("Image file not found.", path);

        var sizePlan = _limits.PlanFileSize(fileInfo.Length);
        if (sizePlan.Action == DecodeAction.Reject)
            throw new SecurityLimitExceededException(sizePlan.RejectReason!);

        var encodedSource = new FileEncodedSource(path, fileInfo.Length, fileInfo.LastWriteTimeUtc);
        try
        {
            await using var stream = encodedSource.OpenRead();
            return await LoadStreamAsync(
                stream,
                encodedSource,
                DocumentSource.FromFile(path),
                Path.GetExtension(path),
                cancellationToken,
                fileInfo.LastWriteTimeUtc).ConfigureAwait(false);
        }
        catch
        {
            encodedSource.Dispose();
            throw;
        }
    }

    public async Task<ImageDocument> LoadMemoryAsync(
        ReadOnlyMemory<byte> bytes, DocumentSource source, CancellationToken cancellationToken)
    {
        // Memory ingress honors the same byte cap as files (clipboard payloads are untrusted too).
        var sizePlan = _limits.PlanFileSize(bytes.Length);
        if (sizePlan.Action == DecodeAction.Reject)
            throw new SecurityLimitExceededException(sizePlan.RejectReason!);

        var encodedSource = new MemoryEncodedSource(bytes.ToArray());
        try
        {
            using var stream = encodedSource.OpenRead();
            return await LoadStreamAsync(
                stream, encodedSource, source, extension: null, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            encodedSource.Dispose();
            throw;
        }
    }

    private async Task<ImageDocument> LoadStreamAsync(
        Stream stream,
        IEncodedSource encodedSource,
        DocumentSource source,
        string? extension,
        CancellationToken cancellationToken,
        DateTime sourceLastWriteUtc = default)
    {
        var header = new byte[256];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken)
            .ConfigureAwait(false);
        stream.Position = 0;

        var sniff = FormatSniffer.Sniff(header.AsSpan(0, read));
        if (sniff.Format == ImageFormat.Unknown && IsGZipHeader(header, read))
            sniff = await SniffCompressedSvgAsync(stream, cancellationToken).ConfigureAwait(false);
        var diagnostics = new List<DocumentDiagnostic>();
        if (!FormatSniffer.ExtensionMatches(sniff.Format, extension))
            diagnostics.Add(new DocumentDiagnostic(
                "FORMAT_EXTENSION_MISMATCH",
                DocumentDiagnosticSeverity.Warning,
                $"Extension '{extension}' does not match the detected format {sniff.Format}."));

        var selection = sniff.Status switch
        {
            SniffStatus.Supported => Dispatch(sniff.Format),
            SniffStatus.Conditional => DispatchConditional(sniff.Format),
            SniffStatus.KnownButUnsupported => DispatchKnownFormat(sniff.Format),
            SniffStatus.CorruptOrTruncated => throw new CorruptImageException(
                "The file is truncated or is not image data."),
            _ => throw new UnsupportedFormatException("Unrecognized image format."),
        };
        var decoder = selection.Decoder;

        DecodeResult result;
        try
        {
            result = await decoder.DecodeAsync(stream, new DecodeRequest(_limits), cancellationToken).ConfigureAwait(false);
        }
        catch (OutOfMemoryException) when (stream.CanSeek)
        {
            // One low-resolution retry, then the failure propagates (NFR-PERF-008).
            stream.Position = 0;
            var reduced = _limits with
            {
                DisplayByteBudget = Math.Max(
                    8L * 1_000_000, _limits.DisplayByteBudget / 4),
            };
            result = await decoder.DecodeAsync(stream, new DecodeRequest(reduced), cancellationToken).ConfigureAwait(false);
            result = result with { IsReduced = true };
            diagnostics.Add(new DocumentDiagnostic(
                "REDUCED_PREVIEW_LOW_MEMORY",
                DocumentDiagnosticSeverity.Warning,
                "Low memory: opened as a reduced preview."));
        }

        if (result.Diagnostics is { Count: > 0 } decodeDiagnostics)
            diagnostics.AddRange(decodeDiagnostics);

        IDocumentFrameSource? frameSource = null;
        try
        {
            frameSource = await DocumentFrameSourceFactory.TryCreateAsync(
                    sniff.Format,
                    encodedSource,
                    decoder,
                    _skia,
                    result,
                    _limits,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            result.Frame.Dispose();
            throw;
        }

        if (frameSource is null)
            encodedSource.Dispose();

        return new ImageDocument
        {
            Frame = result.Frame,
            Source = source,
            NativeSize = result.NativeSize,
            Format = sniff.Format,
            SourceFileBytes = stream.CanSeek ? stream.Length : 0,
            SourceLastWriteUtc = sourceLastWriteUtc,
            IsReducedPreview = result.IsReduced,
            Renderer = sniff.Format == ImageFormat.Gif && frameSource is SkiaDocumentFrameSource
                ? DescribeGifAnimationRenderer()
                : selection.Renderer,
            DiagnosticEntries = diagnostics,
            FrameSource = frameSource,
        };
    }

    private DecoderSelection Dispatch(ImageFormat format) => format switch
    {
        ImageFormat.Png or ImageFormat.Jpeg or ImageFormat.Gif or ImageFormat.Bmp
            or ImageFormat.Tiff or ImageFormat.Ico => new(_wic, DescribeRenderer(format, _wic)),
        ImageFormat.WebP => new(_skia, DescribeRenderer(format, _skia)),
        ImageFormat.Svg => new(_svg, DescribeRenderer(format, _svg)),
        _ => throw new UnsupportedFormatException($"No decoder mapped for {format}."),
    };

    private DecoderSelection DispatchConditional(ImageFormat format)
    {
        if (!_wicCodecs.TryGetRenderer(format, out var renderer))
            throw new CodecUnavailableException(
                $"{format} requires a compatible Windows system codec.");
        return new DecoderSelection(_wic, renderer);
    }

    private DecoderSelection DispatchKnownFormat(ImageFormat format)
    {
        var decoder = format switch
        {
            ImageFormat.Pdf => _pdf,
            ImageFormat.Psd => _psd,
            _ => null,
        };
        if (decoder is null)
        {
            throw new UnsupportedFormatException(
                $"{format} display is planned for a later milestone.");
        }

        return new DecoderSelection(
            decoder,
            new DocumentRendererInfo(
                $"Isolated CodecHost ({format.ToString().ToUpperInvariant()})",
                _isolatedCodecVersion ?? "Unknown"));
    }

    private static DocumentRendererInfo DescribeRenderer(ImageFormat format, IImageDecoder decoder)
    {
        if (format == ImageFormat.WebP)
        {
            var version = typeof(SkiaSharp.SKCodec).Assembly.GetName().Version?.ToString() ?? "Unknown";
            return new DocumentRendererInfo("SkiaSharp", version);
        }

        if (format == ImageFormat.Svg)
        {
            var version = typeof(SvgImageDecoder).Assembly
                .GetReferencedAssemblies()
                .FirstOrDefault(assembly => assembly.Name == "Svg.Skia")?.Version?.ToString() ?? "Unknown";
            return new DocumentRendererInfo("Svg.Skia (secure static)", version);
        }

        if (decoder is WicImageDecoder)
            return new DocumentRendererInfo("Windows Imaging Component", Environment.OSVersion.Version.ToString());

        var assembly = decoder.GetType().Assembly.GetName();
        return new DocumentRendererInfo(decoder.GetType().Name, assembly.Version?.ToString() ?? "Unknown");
    }

    private static DocumentRendererInfo DescribeGifAnimationRenderer()
    {
        var skiaVersion = typeof(SkiaSharp.SKCodec).Assembly.GetName().Version?.ToString() ?? "Unknown";
        return new DocumentRendererInfo(
            "Windows Imaging Component + SkiaSharp animation",
            Environment.OSVersion.Version + " / SkiaSharp " + skiaVersion);
    }

    private readonly record struct DecoderSelection(IImageDecoder Decoder, DocumentRendererInfo Renderer);

    private static bool IsGZipHeader(byte[] header, int length) =>
        length >= 2 && header[0] == 0x1F && header[1] == 0x8B;

    private static async Task<SniffResult> SniffCompressedSvgAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        try
        {
            stream.Position = 0;
            using var gzip = new GZipStream(stream, CompressionMode.Decompress, leaveOpen: true);
            var expandedHeader = new byte[256];
            var expanded = await gzip.ReadAtLeastAsync(
                    expandedHeader,
                    expandedHeader.Length,
                    throwOnEndOfStream: false,
                    cancellationToken)
                .ConfigureAwait(false);
            var inner = FormatSniffer.Sniff(expandedHeader.AsSpan(0, expanded));
            return inner.Format == ImageFormat.Svg
                ? inner
                : new SniffResult(SniffStatus.Unknown, ImageFormat.Unknown);
        }
        catch (InvalidDataException ex)
        {
            throw new CorruptImageException("Compressed input is invalid.", ex);
        }
        finally
        {
            stream.Position = 0;
        }
    }
}
