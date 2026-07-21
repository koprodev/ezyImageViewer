using EzyImageViewer.CodecProtocol;

namespace EzyImageViewer.CodecHost;

internal static class CodecTransportGuard
{
    public static bool IsInline(CodecRequest request) =>
        request.InputTransport == CodecInputTransport.Inline;
}
