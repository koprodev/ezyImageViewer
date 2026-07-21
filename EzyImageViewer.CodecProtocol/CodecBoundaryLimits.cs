namespace EzyImageViewer.CodecProtocol;

/// <summary>Shared product/host limits for one captured codec response.</summary>
public static class CodecBoundaryLimits
{
    public const int MaxDiagnosticBytes = 1024;
    public const long MaxPayloadBytes = 192L * 1024 * 1024;
    public const int MaxStandardOutputBytes = checked(
        CodecWireProtocol.ResponseHeaderSize
        + MaxDiagnosticBytes
        + (int)MaxPayloadBytes);
}
