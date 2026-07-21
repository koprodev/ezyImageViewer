using EzyImageViewer.Capture.Clipboard;
using Windows.ApplicationModel.DataTransfer;

namespace EzyImageViewer.Capture.Snipping;

/// <summary>
/// Redeems a Snipping Tool file-access-token into an owned in-memory payload. Tokens are
/// one-shot by OS contract (redeeming consumes them), so a failed read cannot be retried —
/// callers surface the failure instead.
/// </summary>
public static class CaptureTokenReader
{
    public static async Task<ClipboardImagePayload?> RedeemAsync(
        string token, long maxBytes, CancellationToken cancellationToken)
    {
        var file = await SharedStorageAccessManager.RedeemTokenForFileAsync(token)
            .AsTask(cancellationToken).ConfigureAwait(false);
        using var stream = await file.OpenStreamForReadAsync().ConfigureAwait(false);
        return await ReadWithinBudgetAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Budget gate (inclusive at maxBytes) kept separate from WinRT so the boundary is
    /// unit-testable; null = empty or over budget.</summary>
    public static async Task<ClipboardImagePayload?> ReadWithinBudgetAsync(
        Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.Length <= 0 || stream.Length > maxBytes)
            return null;

        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        // Snipping Tool produces PNG screenshots; anything else is left to sniffing at open.
        return new ClipboardImagePayload(bytes, ClipboardImagePayload.Png);
    }
}
