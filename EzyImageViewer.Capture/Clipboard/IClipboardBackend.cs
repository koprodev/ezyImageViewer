namespace EzyImageViewer.Capture.Clipboard;

/// <summary>Owned snapshot taken at ingress: plain decodable bytes, never a live view or OS handle.</summary>
public sealed record ClipboardImagePayload(byte[] Bytes, string Format)
{
    public const string Png = "png";
    public const string Bmp = "bmp";
}

/// <summary>
/// Abstraction over the OS clipboard so ingest logic is unit-testable with fakes; the real
/// backend is exercised by opt-in interactive tests only (CI must not touch the user clipboard).
/// </summary>
public interface IClipboardBackend
{
    /// <summary>Returns null when no image content is available. Enforces <paramref name="maxBytes"/> during copy.</summary>
    Task<ClipboardImagePayload?> TryGetImageAsync(long maxBytes, CancellationToken cancellationToken);
}

/// <summary>Egress side (FR-OUT-001), separate so read-only fakes stay untouched.</summary>
public interface IClipboardImageWriter
{
    /// <summary>Publishes an encoded PNG as both the PNG format (alpha-preserving) and the standard
    /// bitmap format, tagged with the internal-copy marker (FR-CAP-005). UI-thread only, like the
    /// reader.</summary>
    Task SetImagePngAsync(byte[] pngBytes, CancellationToken cancellationToken);

    /// <summary>True while the current clipboard content carries the app's internal-copy marker —
    /// the primary FR-CAP-005 signal for the capture watcher.</summary>
    bool CurrentContentHasInternalMarker();
}
