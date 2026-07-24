using System.IO.Compression;
using System.Xml;
using EzyImageViewer.Core.Imaging;
using SkiaSharp;
using Svg;
using Svg.Model;
using Svg.Model.Services;
using Svg.Skia;

namespace EzyImageViewer.Imaging.Svg;

/// <summary>스크립트·DTD·엔터티·외부 리소스를 모두 막은 정적 SVG 렌더러.</summary>
public sealed class SvgImageDecoder : IImageDecoder
{
    internal const long MaxExpandedBytes = 64L * 1024 * 1024;
    internal const int MaxElementCount = 100_000;

    public Task<DecodeResult> DecodeAsync(Stream stream, DecodeRequest request, CancellationToken cancellationToken)
        => Task.Run(() => Decode(stream, request, cancellationToken), cancellationToken);

    private static DecodeResult Decode(Stream stream, DecodeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var expanded = ReadExpanded(stream, cancellationToken);
        RejectDocumentTypeDeclaration(expanded);
        ValidateXml(expanded, cancellationToken);
        expanded.Position = 0;

        var loadOptions = new SvgDocumentLoadOptions
        {
            ProcessingMode = SvgProcessingMode.SecureStatic,
            ExternalResources = SvgExternalResourcePolicy.Disabled,
            PreserveUnknownElements = false,
        };
        var parameters = new SvgParameters(null, null, null, loadOptions);

        global::Svg.SvgDocument? sourceDocument;
        try
        {
            sourceDocument = SvgService.Open(expanded, parameters, captureCompatibilityStyleState: false);
        }
        catch (XmlException ex)
        {
            throw new CorruptImageException($"SVG XML is invalid ({ex.Message}).", ex);
        }
        catch (Exception ex) when (ex is not ImageRejectedException and not OperationCanceledException)
        {
            throw new CorruptImageException($"SVG could not be parsed ({ex.Message}).", ex);
        }

        if (sourceDocument is null)
            throw new CorruptImageException("SVG does not contain a renderable document.");

        using var svg = new SKSvg();
        svg.Settings.EnableJavaScript = false;
        svg.Settings.EnableBrokenImagePlaceholders = false;
        using var picture = svg.FromSvgDocument(sourceDocument)
            ?? throw new UnsupportedFormatException("SVG does not contain renderable static content.");

        var bounds = picture.CullRect;
        var nativeWidth = ToNativeDimension(bounds.Width, "width");
        var nativeHeight = ToNativeDimension(bounds.Height, "height");
        if (nativeWidth < 1 || nativeHeight < 1)
            throw new UnsupportedFormatException("SVG has no positive viewport or render bounds.");

        var nativeMax = Math.Max(nativeWidth, nativeHeight);
        var preferredMax = request.PreferredMaxDimension is { } preferred
            ? Math.Clamp(preferred, 1, request.Limits.MaxDimension)
            : nativeMax;
        var desiredScale = (double)preferredMax / nativeMax;
        var desiredWidth = Math.Max(1, checked((int)Math.Ceiling(nativeWidth * desiredScale)));
        var desiredHeight = Math.Max(1, checked((int)Math.Ceiling(nativeHeight * desiredScale)));
        var plan = request.Limits.PlanDimensions(desiredWidth, desiredHeight);
        if (plan.Action == DecodeAction.Reject)
            throw new SecurityLimitExceededException(plan.RejectReason!);

        var scale = plan.Action == DecodeAction.DecodeScaled
            ? (float)plan.TargetMaxDimension / Math.Max(desiredWidth, desiredHeight) * (float)desiredScale
            : (float)desiredScale;
        var width = Math.Max(1, checked((int)Math.Ceiling(nativeWidth * scale)));
        var height = Math.Max(1, checked((int)Math.Ceiling(nativeHeight * scale)));

        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Scale(scale);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
        }

        var buffer = bitmap.Bytes;
        var hasAlpha = PixelAnalysis.HasTransparency(buffer, bitmap.RowBytes, width, height);
        return new DecodeResult(
            new DecodedFrame(buffer, width, height, bitmap.RowBytes, hasAlpha),
            width < nativeWidth || height < nativeHeight,
            new PixelSize(nativeWidth, nativeHeight));
    }

    private static int ToNativeDimension(float value, string name)
    {
        if (float.IsNaN(value))
            throw new CorruptImageException($"SVG {name} is not a number.");
        if (float.IsInfinity(value) || value > int.MaxValue)
            throw new SecurityLimitExceededException($"SVG {name} exceeds the supported security limit.");
        return value <= 0 ? 0 : (int)Math.Ceiling(value);
    }

    private static MemoryStream ReadExpanded(Stream input, CancellationToken cancellationToken)
    {
        var output = new MemoryStream();
        Stream source = input;
        GZipStream? gzip = null;
        if (IsGZip(input))
        {
            gzip = new GZipStream(input, CompressionMode.Decompress, leaveOpen: true);
            source = gzip;
        }

        try
        {
            var buffer = new byte[81920];
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = source.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;
                if (output.Length + read > MaxExpandedBytes)
                    throw new SecurityLimitExceededException(
                        $"Expanded SVG exceeds the {MaxExpandedBytes:N0} byte security limit.");
                output.Write(buffer, 0, read);
            }
        }
        catch (InvalidDataException ex)
        {
            output.Dispose();
            throw new CorruptImageException("SVGZ compression data is invalid.", ex);
        }
        finally
        {
            gzip?.Dispose();
        }

        output.Position = 0;
        return output;
    }

    private static bool IsGZip(Stream stream)
    {
        if (!stream.CanSeek)
            return false;
        var origin = stream.Position;
        var first = stream.ReadByte();
        var second = stream.ReadByte();
        stream.Position = origin;
        return first == 0x1F && second == 0x8B;
    }

    private static void ValidateXml(Stream stream, CancellationToken cancellationToken)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaxExpandedBytes,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
        };

        try
        {
            using var reader = XmlReader.Create(stream, settings);
            var elementCount = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (reader.NodeType == XmlNodeType.Element && ++elementCount > MaxElementCount)
                    throw new SecurityLimitExceededException(
                        $"SVG exceeds the {MaxElementCount:N0} element security limit.");
            }
        }
        catch (XmlException ex)
        {
            throw new CorruptImageException($"SVG XML is invalid or unsafe ({ex.Message}).", ex);
        }
        finally
        {
            stream.Position = 0;
        }
    }

    private static void RejectDocumentTypeDeclaration(Stream stream)
    {
        var probeLength = checked((int)Math.Min(stream.Length, MaxExpandedBytes));
        var probe = new byte[probeLength];
        _ = stream.Read(probe, 0, probe.Length);
        stream.Position = 0;
        var xml = System.Text.Encoding.UTF8.GetString(probe);
        if (xml.Contains("<!DOCTYPE", StringComparison.OrdinalIgnoreCase)
            || xml.Contains("<!ENTITY", StringComparison.OrdinalIgnoreCase))
        {
            throw new SecurityLimitExceededException("SVG DTD and entity declarations are disabled.");
        }
    }
}
