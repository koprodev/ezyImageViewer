namespace EzyImageViewer.Infrastructure;

public sealed class CaptureHotkeyUnavailableException : InvalidOperationException
{
    public CaptureHotkeyUnavailableException(CaptureHotkey requestedHotkey)
        : base("The requested global capture hotkey is unavailable.")
    {
        RequestedHotkey = requestedHotkey
            ?? throw new ArgumentNullException(nameof(requestedHotkey));
    }

    public CaptureHotkey RequestedHotkey { get; }
}
