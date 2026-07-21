using EzyImageViewer.CodecProtocol;

namespace EzyImageViewer.CodecHost;

internal static class CodecHostPolicy
{
    public const long MaxInputBytes = 512L * 1024 * 1024;
    public const int MaxDiagnosticBytes = CodecBoundaryLimits.MaxDiagnosticBytes;
    // One BGRA frame may consume at most half the 384 MiB display budget; the other half is
    // reserved for the render snapshot (InputLimits.DisplayBytesPerPixel = 8).
    public const long MaxPayloadBytes = CodecBoundaryLimits.MaxPayloadBytes;
    public const int MaxDimension = 65_500;
    public const int MaxPageCount = 10_000;
    public const long MaxPixelCount = 500_000_000;

    public static CodecProtocolLimits ProtocolLimits { get; } = new(
        MaxInputBytes,
        MaxPayloadBytes,
        MaxDiagnosticBytes,
        MaxDimension,
        MaxPageCount,
        MaxPixelCount);
}
