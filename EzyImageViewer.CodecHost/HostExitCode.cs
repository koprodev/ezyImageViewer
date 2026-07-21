namespace EzyImageViewer.CodecHost;

internal static class HostExitCode
{
    public const int Success = 0;
    public const int InvalidArguments = 64;
    public const int MalformedProtocol = 65;
    public const int UnexpectedFailure = 70;
    public const int StandardIoFailure = 74;
}
