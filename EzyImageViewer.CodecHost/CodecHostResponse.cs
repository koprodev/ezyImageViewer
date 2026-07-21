using EzyImageViewer.CodecProtocol;

namespace EzyImageViewer.CodecHost;

internal sealed record CodecHostResponse(CodecResponse Header, byte[] Payload)
{
    public Stream? OpenPayloadStream() =>
        Payload.Length == 0 ? null : new MemoryStream(Payload, writable: false);
}
