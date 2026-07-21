namespace EzyImageViewer.CodecProtocol;

public enum CodecOperation : ushort
{
    Probe = 1,
    Inspect = 2,
    Decode = 3,
    DiagnosticEcho = 0x100,
    DiagnosticSleep = 0x101,
    DiagnosticAllocate = 0x102,
    DiagnosticTryNetwork = 0x103,
    DiagnosticTryWriteOutsideTemp = 0x104,
}

public enum CodecFormat : ushort
{
    None = 0,
    Pdf = 1,
    Psd = 2,
}

public enum CodecInputTransport : ushort
{
    Inline = 1,
    InheritedReadHandle = 2,
}

public enum CodecResultCode : ushort
{
    Success = 0,
    InvalidRequest = 1,
    UnsupportedOperation = 2,
    UnsupportedFormat = 3,
    CorruptInput = 4,
    PasswordRequired = 5,
    ResourceLimitExceeded = 6,
    Canceled = 7,
    DeadlineExceeded = 8,
    AccessDenied = 9,
    CodecUnavailable = 10,
    InternalError = 11,
}

public sealed class CodecProtocolLimits
{
    public CodecProtocolLimits(
        long maxInputBytes,
        long maxPayloadBytes,
        int maxDiagnosticBytes,
        int maxDimension,
        int maxPageCount,
        long maxPixelCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxInputBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxPayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxDiagnosticBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxDimension);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPageCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxPixelCount);

        MaxInputBytes = maxInputBytes;
        MaxPayloadBytes = maxPayloadBytes;
        MaxDiagnosticBytes = maxDiagnosticBytes;
        MaxDimension = maxDimension;
        MaxPageCount = maxPageCount;
        MaxPixelCount = maxPixelCount;
    }

    public long MaxInputBytes { get; }

    public long MaxPayloadBytes { get; }

    public int MaxDiagnosticBytes { get; }

    public int MaxDimension { get; }

    public int MaxPageCount { get; }

    public long MaxPixelCount { get; }
}

public sealed record CodecRequest(
    Guid RequestId,
    ulong Nonce,
    CodecOperation Operation,
    CodecFormat Format,
    CodecInputTransport InputTransport,
    long InputLength,
    ulong InputHandle,
    int PageIndex,
    int TargetWidth,
    int TargetHeight);

public sealed record CodecResponse(
    Guid RequestId,
    ulong Nonce,
    CodecOperation Operation,
    CodecFormat Format,
    CodecResultCode Result,
    int Width,
    int Height,
    int Stride,
    int NativeWidth,
    int NativeHeight,
    int PageCount,
    long PayloadLength,
    string? Diagnostic);
