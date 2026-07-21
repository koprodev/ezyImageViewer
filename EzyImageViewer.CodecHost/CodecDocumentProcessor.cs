using System.Buffers.Binary;
using System.Runtime.InteropServices;
using EzyImageViewer.CodecProtocol;
using ImageMagick;
using PDFtoImage;
using PDFtoImage.Exceptions;
using SkiaSharp;

namespace EzyImageViewer.CodecHost;

internal static class CodecDocumentProcessor
{
    private const string PdfDiagnostic = "pdf-page";
    private const string PsdDiagnostic = "psd-composite";

    public static CodecHostResponse Process(CodecRequest request, Stream input)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(input);
        if (!input.CanSeek)
            return Error(request, CodecResultCode.InvalidRequest, "input-not-seekable");

        try
        {
            return request.Format switch
            {
                CodecFormat.Pdf => ProcessPdf(request, input),
                CodecFormat.Psd => ProcessPsd(request, input),
                _ => Error(request, CodecResultCode.UnsupportedFormat, "format-not-implemented"),
            };
        }
        catch (CodecDocumentException ex)
        {
            return Error(request, ex.Result, ex.Diagnostic);
        }
        catch (PdfPasswordProtectedException)
        {
            return Error(request, CodecResultCode.PasswordRequired, "pdf-password-required");
        }
        catch (PdfUnsupportedSecuritySchemeException)
        {
            return Error(request, CodecResultCode.PasswordRequired, "pdf-security-unsupported");
        }
        catch (PdfException)
        {
            return Error(request, CodecResultCode.CorruptInput, "pdf-invalid");
        }
        catch (MagickResourceLimitErrorException)
        {
            return Error(request, CodecResultCode.ResourceLimitExceeded, "psd-resource-limit");
        }
        catch (MagickException)
        {
            return Error(request, CodecResultCode.CorruptInput, "psd-invalid");
        }
        catch (OutOfMemoryException)
        {
            return Error(request, CodecResultCode.ResourceLimitExceeded, "codec-memory-limit");
        }
        catch (EndOfStreamException)
        {
            return Error(request, CodecResultCode.CorruptInput, "document-truncated");
        }
        catch (OverflowException)
        {
            return Error(request, CodecResultCode.ResourceLimitExceeded, "document-size-overflow");
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
        {
            return Error(request, CodecResultCode.CodecUnavailable, "native-codec-unavailable");
        }
    }

    private static CodecHostResponse ProcessPdf(CodecRequest request, Stream input)
    {
        RequireSignature(input, "%PDF-"u8, "pdf-signature");
        input.Position = 0;
        var pageCount = Conversion.GetPageCount(input, leaveOpen: true, password: null);
        ValidatePageCount(pageCount);

        var metadataPage = request.Operation == CodecOperation.Decode ? request.PageIndex : 0;
        if (metadataPage < 0 || metadataPage >= pageCount)
            throw new CodecDocumentException(CodecResultCode.InvalidRequest, "pdf-page-out-of-range");

        input.Position = 0;
        var pageSize = Conversion.GetPageSize(input, metadataPage, leaveOpen: true, password: null);
        var nativeWidth = ToBoundedDimension(pageSize.Width, "pdf-native-width");
        var nativeHeight = ToBoundedDimension(pageSize.Height, "pdf-native-height");
        ValidateRasterShape(nativeWidth, nativeHeight, requirePayloadBudget: false);

        if (request.Operation == CodecOperation.Inspect)
            return Success(request, nativeWidth, nativeHeight, pageCount, PdfDiagnostic);
        if (request.Operation != CodecOperation.Decode)
            return Error(request, CodecResultCode.UnsupportedOperation, "operation-not-implemented");

        var (targetWidth, targetHeight) = ResolveTarget(request, nativeWidth, nativeHeight);
        var options = new RenderOptions
        {
            Width = targetWidth,
            Height = targetHeight,
            WithAspectRatio = false,
            UseTiling = true,
            BackgroundColor = SKColors.Transparent,
        };

        input.Position = 0;
        using var rendered = Conversion.ToImage(
            input, metadataPage, leaveOpen: true, password: null, options);
        using var bgra = rendered.Copy(SKColorType.Bgra8888)
            ?? throw new CodecDocumentException(
                CodecResultCode.InternalError,
                "pdf-bgra-conversion-failed");
        if (bgra.Width != targetWidth || bgra.Height != targetHeight)
            throw new CodecDocumentException(CodecResultCode.InternalError, "pdf-size-mismatch");

        var stride = bgra.RowBytes;
        var payloadLength = ValidatePayloadShape(bgra.Width, bgra.Height, stride);
        var payload = new byte[payloadLength];
        Marshal.Copy(bgra.GetPixels(), payload, 0, payload.Length);
        if (bgra.AlphaType == SKAlphaType.Unpremul)
            PremultiplyBgra(payload);
        else if (bgra.AlphaType is not (SKAlphaType.Premul or SKAlphaType.Opaque))
            throw new CodecDocumentException(CodecResultCode.InternalError, "pdf-alpha-type-invalid");
        return Success(
            request,
            bgra.Width,
            bgra.Height,
            stride,
            nativeWidth,
            nativeHeight,
            pageCount,
            payload,
            PdfDiagnostic);
    }

    private static CodecHostResponse ProcessPsd(CodecRequest request, Stream input)
    {
        var (headerWidth, headerHeight, channels, depth, colorMode) = ReadPsdHeader(input);
        var diagnostic = GetPsdDiagnostic(colorMode);
        ValidateRasterShape(headerWidth, headerHeight, requirePayloadBudget: false);
        ValidatePsdCompositeSection(input, headerWidth, headerHeight, channels, depth);
        if (request.Operation == CodecOperation.Decode && request.PageIndex != 0)
            throw new CodecDocumentException(CodecResultCode.InvalidRequest, "psd-page-out-of-range");

        ConfigureMagickLimits();
        var settings = new MagickReadSettings
        {
            Format = MagickFormat.Psd,
            FrameIndex = 0,
            FrameCount = 1,
        };

        input.Position = 0;
        var info = new MagickImageInfo(input, settings);
        if (info.Format != MagickFormat.Psd)
            throw new CodecDocumentException(CodecResultCode.CorruptInput, "psd-format-mismatch");
        var nativeWidth = ToBoundedDimension(info.Width, "psd-native-width");
        var nativeHeight = ToBoundedDimension(info.Height, "psd-native-height");
        if (nativeWidth != headerWidth || nativeHeight != headerHeight)
            throw new CodecDocumentException(CodecResultCode.CorruptInput, "psd-size-mismatch");

        if (request.Operation == CodecOperation.Inspect)
            return Success(request, nativeWidth, nativeHeight, pageCount: 1, diagnostic);
        if (request.Operation != CodecOperation.Decode)
            return Error(request, CodecResultCode.UnsupportedOperation, "operation-not-implemented");

        var (targetWidth, targetHeight) = ResolveTarget(request, nativeWidth, nativeHeight);
        input.Position = 0;
        using var image = new MagickImage(input, settings);
        if (image.Format != MagickFormat.Psd)
            throw new CodecDocumentException(CodecResultCode.CorruptInput, "psd-format-mismatch");
        if ((int)image.Width != nativeWidth || (int)image.Height != nativeHeight)
            throw new CodecDocumentException(CodecResultCode.CorruptInput, "psd-size-mismatch");

        image.ColorSpace = ColorSpace.sRGB;
        if (targetWidth != nativeWidth || targetHeight != nativeHeight)
            image.Resize((uint)targetWidth, (uint)targetHeight);
        if ((int)image.Width != targetWidth || (int)image.Height != targetHeight)
            throw new CodecDocumentException(CodecResultCode.InternalError, "psd-size-mismatch");

        var stride = checked(targetWidth * 4);
        _ = ValidatePayloadShape(targetWidth, targetHeight, stride);
        using var pixels = image.GetPixels();
        var payload = pixels.ToByteArray(PixelMapping.BGRA)
            ?? throw new CodecDocumentException(
                CodecResultCode.InternalError,
                "psd-payload-unavailable");
        if (payload.Length != checked(stride * targetHeight))
            throw new CodecDocumentException(CodecResultCode.InternalError, "psd-payload-mismatch");
        PremultiplyBgra(payload);
        return Success(
            request,
            targetWidth,
            targetHeight,
            stride,
            nativeWidth,
            nativeHeight,
            pageCount: 1,
            payload,
            diagnostic);
    }

    private static (int Width, int Height, int Channels, int Depth, int ColorMode) ReadPsdHeader(
        Stream input)
    {
        Span<byte> header = stackalloc byte[26];
        input.Position = 0;
        input.ReadExactly(header);
        if (!header[..4].SequenceEqual("8BPS"u8)
            || BinaryPrimitives.ReadUInt16BigEndian(header[4..]) != 1
            || header.Slice(6, 6).IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new CodecDocumentException(CodecResultCode.CorruptInput, "psd-signature");
        }

        var channels = BinaryPrimitives.ReadUInt16BigEndian(header[12..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(header[14..]);
        var width = BinaryPrimitives.ReadUInt32BigEndian(header[18..]);
        var depth = BinaryPrimitives.ReadUInt16BigEndian(header[22..]);
        var colorMode = BinaryPrimitives.ReadUInt16BigEndian(header[24..]);
        if (channels is < 1 or > 56
            || depth is not (1 or 8 or 16 or 32)
            || colorMode is not (0 or 1 or 2 or 3 or 4 or 7 or 8 or 9)
            || width == 0 || height == 0
            || width > int.MaxValue || height > int.MaxValue)
        {
            throw new CodecDocumentException(CodecResultCode.CorruptInput, "psd-header");
        }
        return ((int)width, (int)height, channels, depth, colorMode);
    }

    private static string GetPsdDiagnostic(int colorMode) => colorMode switch
    {
        4 => "psd-composite-cmyk-to-srgb",
        7 => "psd-composite-multichannel-spot-to-srgb",
        8 => "psd-composite-duotone-spot-to-srgb",
        9 => "psd-composite-lab-to-srgb",
        _ => PsdDiagnostic,
    };

    private static void ValidatePsdCompositeSection(
        Stream input,
        int width,
        int height,
        int channels,
        int depth)
    {
        input.Position = 26;
        SkipPsdSection(input);
        SkipPsdSection(input);
        SkipPsdSection(input);
        if (input.Length - input.Position < sizeof(ushort))
            throw new CodecDocumentException(CodecResultCode.CorruptInput, "psd-composite-missing");

        Span<byte> compressionBytes = stackalloc byte[sizeof(ushort)];
        input.ReadExactly(compressionBytes);
        var compression = BinaryPrimitives.ReadUInt16BigEndian(compressionBytes);
        var remaining = input.Length - input.Position;
        long minimumPayload = compression switch
        {
            0 => checked((long)channels * height * ((checked((long)width * depth) + 7) / 8)),
            1 => checked((long)channels * height * sizeof(ushort) + 1),
            2 or 3 => 1,
            _ => throw new CodecDocumentException(
                CodecResultCode.CorruptInput,
                "psd-compression-invalid"),
        };
        if (remaining < minimumPayload)
            throw new CodecDocumentException(CodecResultCode.CorruptInput, "psd-composite-missing");
    }

    private static void SkipPsdSection(Stream input)
    {
        Span<byte> lengthBytes = stackalloc byte[sizeof(uint)];
        input.ReadExactly(lengthBytes);
        var length = BinaryPrimitives.ReadUInt32BigEndian(lengthBytes);
        if (length > input.Length - input.Position)
            throw new CodecDocumentException(CodecResultCode.CorruptInput, "psd-section-truncated");
        input.Position += length;
    }

    private static void RequireSignature(Stream input, ReadOnlySpan<byte> signature, string diagnostic)
    {
        Span<byte> actual = stackalloc byte[signature.Length];
        input.Position = 0;
        input.ReadExactly(actual);
        if (!actual.SequenceEqual(signature))
            throw new CodecDocumentException(CodecResultCode.CorruptInput, diagnostic);
    }

    private static (int Width, int Height) ResolveTarget(
        CodecRequest request,
        int nativeWidth,
        int nativeHeight)
    {
        var scale = request.TargetWidth == 0
            ? 1d
            : Math.Min(
                (double)request.TargetWidth / nativeWidth,
                (double)request.TargetHeight / nativeHeight);
        scale = Math.Min(scale, (double)CodecHostPolicy.MaxDimension / nativeWidth);
        scale = Math.Min(scale, (double)CodecHostPolicy.MaxDimension / nativeHeight);
        var maximumPixels = Math.Min(
            CodecHostPolicy.MaxPixelCount,
            CodecHostPolicy.MaxPayloadBytes / 4);
        var nativePixels = checked((double)nativeWidth * nativeHeight);
        scale = Math.Min(scale, Math.Sqrt(maximumPixels / nativePixels));
        if (!double.IsFinite(scale) || scale <= 0)
            throw new CodecDocumentException(CodecResultCode.ResourceLimitExceeded, "image-scale-limit");

        var width = Math.Max(1, checked((int)Math.Floor(nativeWidth * scale)));
        var height = Math.Max(1, checked((int)Math.Floor(nativeHeight * scale)));
        if (request.TargetWidth != 0)
        {
            width = Math.Min(width, request.TargetWidth);
            height = Math.Min(height, request.TargetHeight);
        }
        ValidateRasterShape(width, height, requirePayloadBudget: true);
        return (width, height);
    }

    private static void PremultiplyBgra(Span<byte> pixels)
    {
        for (var offset = 0; offset < pixels.Length; offset += 4)
        {
            var alpha = pixels[offset + 3];
            pixels[offset] = (byte)((pixels[offset] * alpha + 127) / 255);
            pixels[offset + 1] = (byte)((pixels[offset + 1] * alpha + 127) / 255);
            pixels[offset + 2] = (byte)((pixels[offset + 2] * alpha + 127) / 255);
        }
    }

    private static int ToBoundedDimension(double value, string diagnostic)
    {
        if (!double.IsFinite(value) || value <= 0 || value > int.MaxValue)
            throw new CodecDocumentException(CodecResultCode.ResourceLimitExceeded, diagnostic);
        return checked((int)Math.Ceiling(value));
    }

    private static void ValidatePageCount(int pageCount)
    {
        if (pageCount <= 0)
            throw new CodecDocumentException(CodecResultCode.CorruptInput, "pdf-page-count");
        if (pageCount > CodecHostPolicy.MaxPageCount)
            throw new CodecDocumentException(CodecResultCode.ResourceLimitExceeded, "pdf-page-limit");
    }

    private static void ValidateRasterShape(int width, int height, bool requirePayloadBudget)
    {
        if (width <= 0 || height <= 0
            || width > CodecHostPolicy.MaxDimension
            || height > CodecHostPolicy.MaxDimension)
        {
            throw new CodecDocumentException(
                CodecResultCode.ResourceLimitExceeded,
                "image-dimension-limit");
        }

        var pixels = checked((long)width * height);
        if (pixels > CodecHostPolicy.MaxPixelCount
            || (requirePayloadBudget && pixels > CodecHostPolicy.MaxPayloadBytes / 4))
        {
            throw new CodecDocumentException(
                CodecResultCode.ResourceLimitExceeded,
                "image-pixel-limit");
        }
    }

    private static int ValidatePayloadShape(int width, int height, int stride)
    {
        if (stride < checked(width * 4) || (stride & 3) != 0)
            throw new CodecDocumentException(CodecResultCode.InternalError, "image-stride-invalid");
        var length = checked((long)stride * height);
        if (length > CodecHostPolicy.MaxPayloadBytes || length > int.MaxValue)
            throw new CodecDocumentException(
                CodecResultCode.ResourceLimitExceeded,
                "image-payload-limit");
        return (int)length;
    }

    private static void ConfigureMagickLimits()
    {
        ResourceLimits.Width = (ulong)CodecHostPolicy.MaxDimension;
        ResourceLimits.Height = (ulong)CodecHostPolicy.MaxDimension;
        ResourceLimits.Area = (ulong)(CodecHostPolicy.MaxPayloadBytes / 4);
        ResourceLimits.Memory = 256UL * 1024 * 1024;
        ResourceLimits.MaxMemoryRequest = 64UL * 1024 * 1024;
        ResourceLimits.MaxProfileSize = 16UL * 1024 * 1024;
        ResourceLimits.Disk = 256UL * 1024 * 1024;
        ResourceLimits.ListLength = (ulong)CodecHostPolicy.MaxPageCount;
        ResourceLimits.Thread = 1;
        ResourceLimits.Time = 30;
    }

    private static CodecHostResponse Success(
        CodecRequest request,
        int nativeWidth,
        int nativeHeight,
        int pageCount,
        string diagnostic) => Success(
            request,
            width: 0,
            height: 0,
            stride: 0,
            nativeWidth,
            nativeHeight,
            pageCount,
            payload: [],
            diagnostic);

    private static CodecHostResponse Success(
        CodecRequest request,
        int width,
        int height,
        int stride,
        int nativeWidth,
        int nativeHeight,
        int pageCount,
        byte[] payload,
        string diagnostic)
    {
        var header = new CodecResponse(
            request.RequestId,
            request.Nonce,
            request.Operation,
            request.Format,
            CodecResultCode.Success,
            width,
            height,
            stride,
            nativeWidth,
            nativeHeight,
            pageCount,
            payload.Length,
            diagnostic);
        return new CodecHostResponse(header, payload);
    }

    private static CodecHostResponse Error(
        CodecRequest request,
        CodecResultCode result,
        string diagnostic)
    {
        var header = new CodecResponse(
            request.RequestId,
            request.Nonce,
            request.Operation,
            request.Format,
            result,
            Width: 0,
            Height: 0,
            Stride: 0,
            NativeWidth: 0,
            NativeHeight: 0,
            PageCount: 0,
            PayloadLength: 0,
            diagnostic);
        return new CodecHostResponse(header, []);
    }

    private sealed class CodecDocumentException(
        CodecResultCode result,
        string diagnostic) : Exception
    {
        public CodecResultCode Result { get; } = result;

        public string Diagnostic { get; } = diagnostic;
    }
}
