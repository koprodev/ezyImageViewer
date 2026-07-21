using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace EzyImageViewer.CodecProtocol;

public static class CodecWireProtocol
{
    public const uint Magic = 0x50435A45; // "EZCP" in little-endian byte order.
    public const ushort CurrentVersion = 1;
    public const int RequestHeaderSize = 80;
    public const int ResponseHeaderSize = 88;

    private const ushort RequestMessageKind = 1;
    private const ushort ResponseMessageKind = 2;
    private const int CopyBufferSize = 64 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static async Task WriteRequestAsync(
        Stream destination,
        CodecRequest request,
        Stream? inlineInput,
        CodecProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(limits);
        ValidateRequest(request, limits);

        if (request.InputTransport == CodecInputTransport.Inline)
        {
            if (request.InputLength > 0 && inlineInput is null)
                throw new InvalidDataException("Inline input stream is missing.");
        }
        else if (inlineInput is not null)
        {
            throw new InvalidDataException("Inherited-handle requests cannot contain inline input.");
        }

        var bodyLength = request.InputTransport == CodecInputTransport.Inline
            ? request.InputLength
            : 0;
        var header = new byte[RequestHeaderSize];
        WriteCommonHeader(header, RequestMessageKind, CheckedFrameLength(RequestHeaderSize, bodyLength, 0));
        WriteGuid(header.AsSpan(16, 16), request.RequestId);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), request.Nonce);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(40), (ushort)request.Operation);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(42), (ushort)request.Format);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(44), (ushort)request.InputTransport);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(48), request.InputLength);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(56), request.InputHandle);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(64), request.PageIndex);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(68), request.TargetWidth);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(72), request.TargetHeight);

        await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (bodyLength > 0)
        {
            await CopyExactlyAsync(
                inlineInput!, destination, bodyLength, limits.MaxInputBytes, "request input", cancellationToken)
                .ConfigureAwait(false);
        }
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads and validates request metadata. Inline input remains at the current stream position.</summary>
    public static async Task<CodecRequest> ReadRequestAsync(
        Stream source,
        CodecProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        var header = new byte[RequestHeaderSize];
        await ReadExactlyAsync(source, header, "request header", cancellationToken).ConfigureAwait(false);
        var frameLength = ReadCommonHeader(header, RequestMessageKind);
        RequireZero(header.AsSpan(46, 2), "request flags");
        RequireZero(header.AsSpan(76, 4), "request reserved bytes");

        var request = new CodecRequest(
            ReadGuid(header.AsSpan(16, 16)),
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32)),
            (CodecOperation)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(40)),
            (CodecFormat)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(42)),
            (CodecInputTransport)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(44)),
            BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(48)),
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(56)),
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(64)),
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(68)),
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(72)));
        ValidateRequest(request, limits);

        var bodyLength = request.InputTransport == CodecInputTransport.Inline
            ? request.InputLength
            : 0;
        if (frameLength != CheckedFrameLength(RequestHeaderSize, bodyLength, 0))
            throw new InvalidDataException("Request frame length does not match its transport and input length.");
        return request;
    }

    public static Task CopyInlineInputAsync(
        Stream source,
        Stream destination,
        CodecRequest request,
        CodecProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(limits);
        ValidateRequest(request, limits);
        if (request.InputTransport != CodecInputTransport.Inline)
            throw new InvalidOperationException("The request uses an inherited input handle.");
        return CopyExactlyAsync(
            source, destination, request.InputLength, limits.MaxInputBytes, "request input", cancellationToken);
    }

    public static async Task WriteResponseAsync(
        Stream destination,
        CodecResponse response,
        Stream? payload,
        CodecProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(limits);
        ValidateResponse(response, limits);
        if (response.PayloadLength > 0 && payload is null)
            throw new InvalidDataException("Response payload stream is missing.");
        if (response.PayloadLength == 0 && payload is not null)
            throw new InvalidDataException("A zero-length response cannot contain a payload stream.");

        var diagnosticBytes = EncodeDiagnostic(response.Diagnostic, limits);
        var header = new byte[ResponseHeaderSize];
        WriteCommonHeader(
            header,
            ResponseMessageKind,
            CheckedFrameLength(ResponseHeaderSize, response.PayloadLength, diagnosticBytes.Length));
        WriteGuid(header.AsSpan(16, 16), response.RequestId);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(32), response.Nonce);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(40), (ushort)response.Operation);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(42), (ushort)response.Format);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(44), (ushort)response.Result);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(48), response.Width);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(52), response.Height);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(56), response.Stride);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(60), response.NativeWidth);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(64), response.NativeHeight);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(68), response.PageCount);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(72), diagnosticBytes.Length);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(76), response.PayloadLength);

        await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (diagnosticBytes.Length > 0)
            await destination.WriteAsync(diagnosticBytes, cancellationToken).ConfigureAwait(false);
        if (response.PayloadLength > 0)
        {
            await CopyExactlyAsync(
                payload!, destination, response.PayloadLength, limits.MaxPayloadBytes, "response payload", cancellationToken)
                .ConfigureAwait(false);
        }
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads response metadata and its bounded diagnostic. Payload remains at the current stream position.</summary>
    public static async Task<CodecResponse> ReadResponseAsync(
        Stream source,
        CodecProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(limits);
        var header = new byte[ResponseHeaderSize];
        await ReadExactlyAsync(source, header, "response header", cancellationToken).ConfigureAwait(false);
        var frameLength = ReadCommonHeader(header, ResponseMessageKind);
        RequireZero(header.AsSpan(46, 2), "response flags");
        RequireZero(header.AsSpan(84, 4), "response reserved bytes");

        var diagnosticLength = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(72));
        var payloadLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(76));
        if (diagnosticLength < 0 || diagnosticLength > limits.MaxDiagnosticBytes)
            throw new InvalidDataException("Response diagnostic length exceeds the configured limit.");
        if (payloadLength < 0 || payloadLength > limits.MaxPayloadBytes)
            throw new InvalidDataException("Response payload length exceeds the configured limit.");
        if (frameLength != CheckedFrameLength(ResponseHeaderSize, payloadLength, diagnosticLength))
            throw new InvalidDataException("Response frame length does not match its body lengths.");

        var diagnosticBytes = new byte[diagnosticLength];
        if (diagnosticLength > 0)
        {
            await ReadExactlyAsync(source, diagnosticBytes, "response diagnostic", cancellationToken)
                .ConfigureAwait(false);
        }

        string? diagnostic = null;
        if (diagnosticLength > 0)
        {
            try
            {
                diagnostic = StrictUtf8.GetString(diagnosticBytes);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException("Response diagnostic is not valid UTF-8.", ex);
            }
        }

        var response = new CodecResponse(
            ReadGuid(header.AsSpan(16, 16)),
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32)),
            (CodecOperation)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(40)),
            (CodecFormat)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(42)),
            (CodecResultCode)BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(44)),
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(48)),
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(52)),
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(56)),
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(60)),
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(64)),
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(68)),
            payloadLength,
            diagnostic);
        ValidateResponse(response, limits);
        return response;
    }

    public static Task CopyResponsePayloadAsync(
        Stream source,
        Stream destination,
        CodecResponse response,
        CodecProtocolLimits limits,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(limits);
        ValidateResponse(response, limits);
        return CopyExactlyAsync(
            source, destination, response.PayloadLength, limits.MaxPayloadBytes, "response payload", cancellationToken);
    }

    public static void ValidateRequest(CodecRequest request, CodecProtocolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(limits);
        ValidateCorrelation(request.RequestId, request.Nonce);
        if (!IsKnown(request.Operation))
            throw new InvalidDataException("Unknown codec operation.");
        if (!IsKnown(request.Format))
            throw new InvalidDataException("Unknown codec format.");
        if (!IsKnown(request.InputTransport))
            throw new InvalidDataException("Unknown input transport.");
        if (request.InputLength < 0 || request.InputLength > limits.MaxInputBytes)
            throw new InvalidDataException("Request input length exceeds the configured limit.");

        if (request.InputTransport == CodecInputTransport.Inline)
        {
            if (request.InputHandle != 0)
                throw new InvalidDataException("Inline input must not carry a native handle.");
        }
        else
        {
            if (request.InputHandle == 0 || request.InputLength == 0)
                throw new InvalidDataException("Inherited input requires a non-zero handle and length.");
        }

        ValidateRequestMetadata(request, limits);
    }

    public static void ValidateResponse(CodecResponse response, CodecProtocolLimits limits)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(limits);
        ValidateCorrelation(response.RequestId, response.Nonce);
        if (!IsKnown(response.Operation))
            throw new InvalidDataException("Unknown codec operation.");
        if (!IsKnown(response.Format))
            throw new InvalidDataException("Unknown codec format.");
        if (!IsKnown(response.Result))
            throw new InvalidDataException("Unknown codec result code.");
        if (response.PayloadLength < 0 || response.PayloadLength > limits.MaxPayloadBytes)
            throw new InvalidDataException("Response payload length exceeds the configured limit.");
        _ = EncodeDiagnostic(response.Diagnostic, limits);

        if (response.Result != CodecResultCode.Success)
        {
            RequireEmptyImageMetadata(response);
            return;
        }

        switch (response.Operation)
        {
            case CodecOperation.Probe:
            case CodecOperation.DiagnosticSleep:
            case CodecOperation.DiagnosticAllocate:
            case CodecOperation.DiagnosticTryNetwork:
            case CodecOperation.DiagnosticTryWriteOutsideTemp:
            case CodecOperation.DiagnosticEcho:
                RequireFormat(response.Format, CodecFormat.None);
                RequireEmptyImageDimensions(response);
                break;
            case CodecOperation.Inspect:
                RequireDocumentFormat(response.Format);
                ValidateInspectMetadata(response, limits);
                break;
            case CodecOperation.Decode:
                RequireDocumentFormat(response.Format);
                ValidateDecodeMetadata(response, limits);
                break;
            default:
                throw new InvalidDataException("Unknown codec operation.");
        }
    }

    private static void ValidateRequestMetadata(CodecRequest request, CodecProtocolLimits limits)
    {
        switch (request.Operation)
        {
            case CodecOperation.Probe:
                RequireFormat(request.Format, CodecFormat.None);
                RequireDiagnosticRequest(request, expectedInputLength: 0);
                break;
            case CodecOperation.Inspect:
                RequireDocumentFormat(request.Format);
                if (request.InputLength == 0 || request.PageIndex != -1)
                    throw new InvalidDataException("Inspect requires input and page index -1.");
                ValidateTarget(request.TargetWidth, request.TargetHeight, allowZero: true, limits);
                break;
            case CodecOperation.Decode:
                RequireDocumentFormat(request.Format);
                if (request.InputLength == 0 || request.PageIndex < 0 || request.PageIndex >= limits.MaxPageCount)
                    throw new InvalidDataException("Decode input or page index is invalid.");
                ValidateTarget(request.TargetWidth, request.TargetHeight, allowZero: true, limits);
                break;
            case CodecOperation.DiagnosticEcho:
                RequireFormat(request.Format, CodecFormat.None);
                RequireDiagnosticRequest(request, expectedInputLength: null);
                break;
            case CodecOperation.DiagnosticSleep:
            case CodecOperation.DiagnosticTryNetwork:
                RequireFormat(request.Format, CodecFormat.None);
                RequireDiagnosticRequest(request, expectedInputLength: sizeof(int));
                break;
            case CodecOperation.DiagnosticAllocate:
                RequireFormat(request.Format, CodecFormat.None);
                RequireDiagnosticRequest(request, expectedInputLength: sizeof(long));
                break;
            case CodecOperation.DiagnosticTryWriteOutsideTemp:
                RequireFormat(request.Format, CodecFormat.None);
                RequireDiagnosticRequest(request, expectedInputLength: 0);
                break;
            default:
                throw new InvalidDataException("Unknown codec operation.");
        }
    }

    private static void RequireDiagnosticRequest(CodecRequest request, long? expectedInputLength)
    {
        if (request.InputTransport != CodecInputTransport.Inline || request.InputHandle != 0
            || request.PageIndex != -1 || request.TargetWidth != 0 || request.TargetHeight != 0)
        {
            throw new InvalidDataException("Diagnostic requests require inline input and empty image metadata.");
        }
        if (expectedInputLength is not null && request.InputLength != expectedInputLength.Value)
            throw new InvalidDataException("Diagnostic request input length is invalid.");
    }

    private static void ValidateInspectMetadata(CodecResponse response, CodecProtocolLimits limits)
    {
        if (response.Width != 0 || response.Height != 0 || response.Stride != 0 || response.PayloadLength != 0)
            throw new InvalidDataException("Inspect responses cannot contain raster payload.");
        ValidateDocumentMetadata(response.NativeWidth, response.NativeHeight, response.PageCount, limits);
    }

    private static void ValidateDecodeMetadata(CodecResponse response, CodecProtocolLimits limits)
    {
        ValidateDimensions(response.Width, response.Height, limits);
        ValidateDocumentMetadata(response.NativeWidth, response.NativeHeight, response.PageCount, limits);
        long minimumStride = checked((long)response.Width * 4);
        if (response.Stride < minimumStride || (response.Stride & 3) != 0)
            throw new InvalidDataException("Response stride is invalid for BGRA32 pixels.");
        long expectedPayload = checked((long)response.Stride * response.Height);
        if (response.PayloadLength != expectedPayload)
            throw new InvalidDataException("Response payload length does not match stride and height.");
    }

    private static void ValidateDocumentMetadata(
        int nativeWidth,
        int nativeHeight,
        int pageCount,
        CodecProtocolLimits limits)
    {
        ValidateDimensions(nativeWidth, nativeHeight, limits);
        if (pageCount <= 0 || pageCount > limits.MaxPageCount)
            throw new InvalidDataException("Response page count exceeds the configured limit.");
    }

    private static void ValidateTarget(int width, int height, bool allowZero, CodecProtocolLimits limits)
    {
        if (width == 0 && height == 0 && allowZero)
            return;
        ValidateDimensions(width, height, limits);
    }

    private static void ValidateDimensions(int width, int height, CodecProtocolLimits limits)
    {
        if (width <= 0 || height <= 0 || width > limits.MaxDimension || height > limits.MaxDimension)
            throw new InvalidDataException("Image dimensions exceed the configured limit.");
        long pixels = checked((long)width * height);
        if (pixels > limits.MaxPixelCount)
            throw new InvalidDataException("Image pixel count exceeds the configured limit.");
    }

    private static void RequireEmptyImageMetadata(CodecResponse response)
    {
        RequireEmptyImageDimensions(response);
        if (response.PayloadLength != 0)
            throw new InvalidDataException("This response cannot contain a payload.");
    }

    private static void RequireEmptyImageDimensions(CodecResponse response)
    {
        if (response.Width != 0 || response.Height != 0 || response.Stride != 0
            || response.NativeWidth != 0 || response.NativeHeight != 0 || response.PageCount != 0)
        {
            throw new InvalidDataException("This response cannot contain image metadata.");
        }
    }

    private static void RequireDocumentFormat(CodecFormat format)
    {
        if (format is not (CodecFormat.Pdf or CodecFormat.Psd))
            throw new InvalidDataException("The operation requires a document codec format.");
    }

    private static void RequireFormat(CodecFormat actual, CodecFormat expected)
    {
        if (actual != expected)
            throw new InvalidDataException($"Operation requires codec format {expected}.");
    }

    private static void ValidateCorrelation(Guid requestId, ulong nonce)
    {
        if (requestId == Guid.Empty || nonce == 0)
            throw new InvalidDataException("Request correlation values must be non-zero.");
    }

    private static byte[] EncodeDiagnostic(string? diagnostic, CodecProtocolLimits limits)
    {
        if (string.IsNullOrEmpty(diagnostic))
            return [];
        var bytes = StrictUtf8.GetBytes(diagnostic);
        if (bytes.Length > limits.MaxDiagnosticBytes)
            throw new InvalidDataException("Response diagnostic exceeds the configured limit.");
        return bytes;
    }

    private static async Task CopyExactlyAsync(
        Stream source,
        Stream destination,
        long length,
        long maximumLength,
        string fieldName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (length < 0 || length > maximumLength)
            throw new InvalidDataException($"{fieldName} exceeds the configured limit.");

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        try
        {
            long remaining = length;
            while (remaining > 0)
            {
                var requested = (int)Math.Min(remaining, buffer.Length);
                var read = await source.ReadAsync(buffer.AsMemory(0, requested), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    throw new InvalidDataException($"Truncated {fieldName}.");
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task ReadExactlyAsync(
        Stream source,
        Memory<byte> destination,
        string fieldName,
        CancellationToken cancellationToken)
    {
        try
        {
            await source.ReadExactlyAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch (EndOfStreamException ex)
        {
            throw new InvalidDataException($"Truncated {fieldName}.", ex);
        }
    }

    private static void WriteCommonHeader(Span<byte> header, ushort kind, ulong frameLength)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], CurrentVersion);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], kind);
        BinaryPrimitives.WriteUInt64LittleEndian(header[8..], frameLength);
    }

    private static ulong ReadCommonHeader(ReadOnlySpan<byte> header, ushort expectedKind)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic)
            throw new InvalidDataException("Codec frame magic is invalid.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[4..]) != CurrentVersion)
            throw new InvalidDataException("Codec protocol version is unsupported.");
        if (BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) != expectedKind)
            throw new InvalidDataException("Codec message kind is invalid.");
        return BinaryPrimitives.ReadUInt64LittleEndian(header[8..]);
    }

    private static ulong CheckedFrameLength(int headerLength, long payloadLength, int diagnosticLength)
    {
        if (payloadLength < 0 || diagnosticLength < 0)
            throw new InvalidDataException("Codec frame body length is negative.");
        try
        {
            return checked((ulong)headerLength + (ulong)payloadLength + (uint)diagnosticLength);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("Codec frame length overflowed.", ex);
        }
    }

    private static void WriteGuid(Span<byte> destination, Guid value)
    {
        if (!value.TryWriteBytes(destination, bigEndian: false, out var written) || written != 16)
            throw new InvalidOperationException("Request identifier could not be encoded.");
    }

    private static Guid ReadGuid(ReadOnlySpan<byte> source) => new(source, bigEndian: false);

    private static void RequireZero(ReadOnlySpan<byte> bytes, string fieldName)
    {
        foreach (var value in bytes)
        {
            if (value != 0)
                throw new InvalidDataException($"Codec {fieldName} must be zero.");
        }
    }

    private static bool IsKnown(CodecOperation value) => value is
        CodecOperation.Probe or CodecOperation.Inspect or CodecOperation.Decode
        or CodecOperation.DiagnosticEcho or CodecOperation.DiagnosticSleep
        or CodecOperation.DiagnosticAllocate or CodecOperation.DiagnosticTryNetwork
        or CodecOperation.DiagnosticTryWriteOutsideTemp;

    private static bool IsKnown(CodecFormat value) => value is
        CodecFormat.None or CodecFormat.Pdf or CodecFormat.Psd;

    private static bool IsKnown(CodecInputTransport value) => value is
        CodecInputTransport.Inline or CodecInputTransport.InheritedReadHandle;

    private static bool IsKnown(CodecResultCode value) => value is
        CodecResultCode.Success or CodecResultCode.InvalidRequest
        or CodecResultCode.UnsupportedOperation or CodecResultCode.UnsupportedFormat
        or CodecResultCode.CorruptInput or CodecResultCode.PasswordRequired
        or CodecResultCode.ResourceLimitExceeded or CodecResultCode.Canceled
        or CodecResultCode.DeadlineExceeded or CodecResultCode.AccessDenied
        or CodecResultCode.CodecUnavailable or CodecResultCode.InternalError;
}
