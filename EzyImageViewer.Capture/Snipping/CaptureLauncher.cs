namespace EzyImageViewer.Capture.Snipping;

/// <summary>
/// FR-CAP-001: opens the Windows snipping overlay. Packaged (Q7=b) the official protocol is
/// used — the capture comes back through the redirect-uri callback. Unpackaged (dev loop) the
/// bare legacy URI stays as a BEST-EFFORT interim ([21차] 필수 1): a failed launch guides the
/// user to Win+Shift+S, whose result the armed clipboard pipeline still auto-opens.
/// </summary>
public static class CaptureLauncher
{
    public const string SnippingUri = "ms-screenclip:";

    /// <summary>Official capture request; Launcher.LaunchUriAsync is mandatory — only it carries
    /// the caller's package identity, which Snipping Tool uses to route the response.</summary>
    public static async Task<bool> LaunchOfficialAsync(string correlationId)
    {
        try
        {
            return await Windows.System.Launcher.LaunchUriAsync(
                SnipProtocol.BuildImageCaptureUri(correlationId));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UriFormatException)
        {
            return false;
        }
    }

    public static async Task<bool> LaunchSnippingAsync()
    {
        try
        {
            return await Windows.System.Launcher.LaunchUriAsync(new Uri(SnippingUri));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UriFormatException)
        {
            return false;
        }
    }

    /// <summary>Non-intrusive support probe (no overlay flashes) — unattended evidence for the
    /// best-effort contract on the current OS build.</summary>
    public static async Task<bool> IsSnippingAvailableAsync()
    {
        try
        {
            var status = await Windows.System.Launcher.QueryUriSupportAsync(
                new Uri(SnippingUri), Windows.System.LaunchQuerySupportType.Uri);
            return status == Windows.System.LaunchQuerySupportStatus.Available;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }
}
