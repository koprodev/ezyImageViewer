using System.Buffers.Binary;
using EzyImageViewer.CodecProtocol;
using Xunit;

namespace EzyImageViewer.Tests.Codec;

public sealed class CodecProtocolTests
{
    private static readonly CodecProtocolLimits Limits = new(
        maxInputBytes: 1024 * 1024,
        maxPayloadBytes: 4 * 1024 * 1024,
        maxDiagnosticBytes: 1024,
        maxDimension: 4096,
        maxPageCount: 1000,
        maxPixelCount: 4_000_000);

    [Fact]
    public void BoundaryLimits_PreserveExact192MiBRasterAndIncludeWireEnvelope()
    {
        Assert.Equal(checked(8192L * 6144 * 4), CodecBoundaryLimits.MaxPayloadBytes);
        Assert.Equal(
            CodecWireProtocol.ResponseHeaderSize
                + CodecBoundaryLimits.MaxDiagnosticBytes
                + CodecBoundaryLimits.MaxPayloadBytes,
            (long)CodecBoundaryLimits.MaxStandardOutputBytes);
    }

    [Fact]
    public async Task InlineRequest_RoundTripsLittleEndianHeaderAndBody()
    {
        var input = new byte[] { 1, 3, 5, 7, 9 };
        var request = Request(
            CodecOperation.DiagnosticEcho,
            CodecFormat.None,
            CodecInputTransport.Inline,
            input.Length);
        await using var wire = new MemoryStream();

        await CodecWireProtocol.WriteRequestAsync(
            wire, request, new MemoryStream(input), Limits, CancellationToken.None);

        var bytes = wire.ToArray();
        Assert.Equal("EZCP"u8.ToArray(), bytes[..4]);
        Assert.Equal(CodecWireProtocol.CurrentVersion, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(
            (ulong)(CodecWireProtocol.RequestHeaderSize + input.Length),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(8)));
        Assert.Equal((long)input.Length, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(48)));

        wire.Position = 0;
        var parsed = await CodecWireProtocol.ReadRequestAsync(wire, Limits, CancellationToken.None);
        Assert.Equal(request, parsed);
        await using var copied = new MemoryStream();
        await CodecWireProtocol.CopyInlineInputAsync(
            wire, copied, parsed, Limits, CancellationToken.None);
        Assert.Equal(input, copied.ToArray());
    }

    [Fact]
    public async Task InheritedHandleRequest_HasNoInlineBody()
    {
        var request = Request(
            CodecOperation.Inspect,
            CodecFormat.Pdf,
            CodecInputTransport.InheritedReadHandle,
            inputLength: 123,
            inputHandle: 0x1234);
        await using var wire = new MemoryStream();

        await CodecWireProtocol.WriteRequestAsync(
            wire, request, inlineInput: null, Limits, CancellationToken.None);

        Assert.Equal(CodecWireProtocol.RequestHeaderSize, wire.Length);
        wire.Position = 0;
        Assert.Equal(request, await CodecWireProtocol.ReadRequestAsync(wire, Limits, CancellationToken.None));
    }

    [Fact]
    public async Task DecodeResponse_RoundTripsMetadataDiagnosticAndStreamedPayload()
    {
        var pixels = Enumerable.Range(0, 24).Select(value => (byte)value).ToArray();
        var response = new CodecResponse(
            Guid.Parse("0f55f2d5-4f83-43df-b83d-2a6d10d16ac8"),
            0x0102030405060708,
            CodecOperation.Decode,
            CodecFormat.Psd,
            CodecResultCode.Success,
            Width: 3,
            Height: 2,
            Stride: 12,
            NativeWidth: 300,
            NativeHeight: 200,
            PageCount: 1,
            PayloadLength: pixels.Length,
            Diagnostic: "composite");
        await using var wire = new MemoryStream();

        await CodecWireProtocol.WriteResponseAsync(
            wire, response, new MemoryStream(pixels), Limits, CancellationToken.None);

        var bytes = wire.ToArray();
        Assert.Equal("EZCP"u8.ToArray(), bytes[..4]);
        Assert.Equal((long)pixels.Length, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(76)));
        wire.Position = 0;
        var parsed = await CodecWireProtocol.ReadResponseAsync(wire, Limits, CancellationToken.None);
        Assert.Equal(response, parsed);
        await using var copied = new MemoryStream();
        await CodecWireProtocol.CopyResponsePayloadAsync(
            wire, copied, parsed, Limits, CancellationToken.None);
        Assert.Equal(pixels, copied.ToArray());
    }

    [Fact]
    public async Task UnicodeDiagnostic_IsStrictUtf8AndBounded()
    {
        var response = EmptyResponse(CodecResultCode.InternalError, "디코더 오류");
        await using var wire = new MemoryStream();

        await CodecWireProtocol.WriteResponseAsync(
            wire, response, payload: null, Limits, CancellationToken.None);
        wire.Position = 0;

        var parsed = await CodecWireProtocol.ReadResponseAsync(wire, Limits, CancellationToken.None);
        Assert.Equal("디코더 오류", parsed.Diagnostic);
    }

    [Fact]
    public async Task TruncatedRequestHeader_IsRejected()
    {
        await using var wire = new MemoryStream(new byte[CodecWireProtocol.RequestHeaderSize - 1]);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            CodecWireProtocol.ReadRequestAsync(wire, Limits, CancellationToken.None));
    }

    [Fact]
    public async Task TruncatedInlineInput_IsRejectedWhileStreaming()
    {
        var request = Request(
            CodecOperation.DiagnosticEcho,
            CodecFormat.None,
            CodecInputTransport.Inline,
            inputLength: 4);
        await using var wire = new MemoryStream();
        await CodecWireProtocol.WriteRequestAsync(
            wire, request, new MemoryStream([1, 2, 3, 4]), Limits, CancellationToken.None);
        wire.SetLength(wire.Length - 1);
        wire.Position = 0;
        var parsed = await CodecWireProtocol.ReadRequestAsync(wire, Limits, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() => CodecWireProtocol.CopyInlineInputAsync(
            wire, new MemoryStream(), parsed, Limits, CancellationToken.None));
    }

    [Fact]
    public async Task TruncatedResponsePayload_IsRejectedWhileStreaming()
    {
        var response = new CodecResponse(
            Guid.NewGuid(), 42, CodecOperation.DiagnosticEcho, CodecFormat.None,
            CodecResultCode.Success, 0, 0, 0, 0, 0, 0, 3, null);
        await using var wire = new MemoryStream();
        await CodecWireProtocol.WriteResponseAsync(
            wire, response, new MemoryStream([1, 2, 3]), Limits, CancellationToken.None);
        wire.SetLength(wire.Length - 1);
        wire.Position = 0;
        var parsed = await CodecWireProtocol.ReadResponseAsync(wire, Limits, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidDataException>(() => CodecWireProtocol.CopyResponsePayloadAsync(
            wire, new MemoryStream(), parsed, Limits, CancellationToken.None));
    }

    [Theory]
    [InlineData(0, 0x00)]
    [InlineData(4, 0x02)]
    [InlineData(6, 0x7F)]
    [InlineData(40, 0xFF)]
    [InlineData(42, 0xFF)]
    [InlineData(44, 0xFF)]
    [InlineData(46, 0x01)]
    [InlineData(76, 0x01)]
    public async Task InvalidRequestHeaderFields_AreRejected(int offset, byte value)
    {
        var bytes = await RequestBytesAsync(Request(
            CodecOperation.DiagnosticEcho,
            CodecFormat.None,
            CodecInputTransport.Inline,
            inputLength: 0));
        bytes[offset] = value;

        await Assert.ThrowsAsync<InvalidDataException>(() => CodecWireProtocol.ReadRequestAsync(
            new MemoryStream(bytes), Limits, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownResultCode_IsRejectedBeforePayloadRead()
    {
        var bytes = await ResponseBytesAsync(EmptyResponse(CodecResultCode.InternalError, "failure"));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(44), ushort.MaxValue);

        await Assert.ThrowsAsync<InvalidDataException>(() => CodecWireProtocol.ReadResponseAsync(
            new MemoryStream(bytes), Limits, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedInput_IsRejectedBeforeBodyRead()
    {
        var request = Request(
            CodecOperation.DiagnosticEcho,
            CodecFormat.None,
            CodecInputTransport.Inline,
            inputLength: Limits.MaxInputBytes + 1);

        await Assert.ThrowsAsync<InvalidDataException>(() => CodecWireProtocol.WriteRequestAsync(
            new MemoryStream(), request, Stream.Null, Limits, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedPayloadLength_IsRejectedBeforeAllocation()
    {
        var bytes = await ResponseBytesAsync(EmptyResponse(CodecResultCode.InternalError, "failure"));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(76), Limits.MaxPayloadBytes + 1);

        await Assert.ThrowsAsync<InvalidDataException>(() => CodecWireProtocol.ReadResponseAsync(
            new MemoryStream(bytes), Limits, CancellationToken.None));
    }

    [Fact]
    public async Task OversizedDiagnosticLength_IsRejectedBeforeAllocation()
    {
        var bytes = await ResponseBytesAsync(EmptyResponse(CodecResultCode.InternalError, "failure"));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(72), Limits.MaxDiagnosticBytes + 1);

        await Assert.ThrowsAsync<InvalidDataException>(() => CodecWireProtocol.ReadResponseAsync(
            new MemoryStream(bytes), Limits, CancellationToken.None));
    }

    [Fact]
    public async Task InvalidUtf8Diagnostic_IsRejected()
    {
        var bytes = await ResponseBytesAsync(EmptyResponse(CodecResultCode.InternalError, "x"));
        bytes[CodecWireProtocol.ResponseHeaderSize] = 0xFF;

        await Assert.ThrowsAsync<InvalidDataException>(() => CodecWireProtocol.ReadResponseAsync(
            new MemoryStream(bytes), Limits, CancellationToken.None));
    }

    [Fact]
    public void InheritedTransport_RequiresNonZeroHandleAndLength()
    {
        Assert.Throws<InvalidDataException>(() => CodecWireProtocol.ValidateRequest(
            Request(CodecOperation.Inspect, CodecFormat.Pdf, CodecInputTransport.InheritedReadHandle, 10),
            Limits));
        Assert.Throws<InvalidDataException>(() => CodecWireProtocol.ValidateRequest(
            Request(CodecOperation.Inspect, CodecFormat.Pdf, CodecInputTransport.InheritedReadHandle, 0, 10),
            Limits));
    }

    [Fact]
    public void DimensionsAndStride_RejectWraparoundShapes()
    {
        var permissive = new CodecProtocolLimits(
            long.MaxValue, long.MaxValue, 1024, int.MaxValue, int.MaxValue, long.MaxValue);
        var response = new CodecResponse(
            Guid.NewGuid(), 7, CodecOperation.Decode, CodecFormat.Pdf,
            CodecResultCode.Success, int.MaxValue, 1, int.MaxValue,
            int.MaxValue, 1, 1, int.MaxValue, null);

        Assert.Throws<InvalidDataException>(() => CodecWireProtocol.ValidateResponse(response, permissive));
    }

    [Fact]
    public void DecodePayload_MustExactlyMatchStrideTimesHeight()
    {
        var response = new CodecResponse(
            Guid.NewGuid(), 7, CodecOperation.Decode, CodecFormat.Pdf,
            CodecResultCode.Success, 2, 2, 8, 20, 20, 1, 15, null);

        Assert.Throws<InvalidDataException>(() => CodecWireProtocol.ValidateResponse(response, Limits));
    }

    private static CodecRequest Request(
        CodecOperation operation,
        CodecFormat format,
        CodecInputTransport transport,
        long inputLength,
        ulong inputHandle = 0) => new(
            Guid.Parse("17da3ef6-c08d-4c4d-a1be-8cb48e3ef43c"),
            0xFFEEDDCCBBAA9988,
            operation,
            format,
            transport,
            inputLength,
            inputHandle,
            operation == CodecOperation.Decode ? 0 : -1,
            0,
            0);

    private static CodecResponse EmptyResponse(CodecResultCode result, string? diagnostic) => new(
        Guid.Parse("17da3ef6-c08d-4c4d-a1be-8cb48e3ef43c"),
        0xFFEEDDCCBBAA9988,
        CodecOperation.Probe,
        CodecFormat.None,
        result,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        diagnostic);

    private static async Task<byte[]> RequestBytesAsync(CodecRequest request)
    {
        await using var wire = new MemoryStream();
        await CodecWireProtocol.WriteRequestAsync(
            wire, request, inlineInput: null, Limits, CancellationToken.None);
        return wire.ToArray();
    }

    private static async Task<byte[]> ResponseBytesAsync(CodecResponse response)
    {
        await using var wire = new MemoryStream();
        await CodecWireProtocol.WriteResponseAsync(
            wire, response, payload: null, Limits, CancellationToken.None);
        return wire.ToArray();
    }
}
