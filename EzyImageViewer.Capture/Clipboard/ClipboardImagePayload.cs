namespace EzyImageViewer.Capture.Clipboard;

/// <summary>OS 핸들 대신 해독 가능한 바이트만 소유하는 클립보드 스냅샷.</summary>
public sealed record ClipboardImagePayload(byte[] Bytes, string Format)
{
    public const string Png = "png";
    public const string Bmp = "bmp";
}
