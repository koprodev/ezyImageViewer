namespace EzyImageViewer.Capture.Snipping;

public enum CaptureDecision
{
    /// <summary>Internal echo or watch disabled: nothing happens.</summary>
    Ignore,
    /// <summary>The user asked for a capture (button/hotkey): open the result directly.</summary>
    AutoOpen,
    /// <summary>Unsolicited capture while watching (Win+Shift+S): offer to open (FR-CAP-003).</summary>
    Notify,
}

/// <summary>
/// Capture ingestion policy (FR-CAP-003/005/006). Pure state: an app-initiated capture "arms" a
/// window of time in which the next external clipboard image auto-opens; outside it, images only
/// raise a notification, and only while watching is enabled. Internal echoes always lose.
/// </summary>
public sealed class CaptureFlow
{
    /// <summary>Generous because the user is drawing a snip; an abandoned overlay simply times out.</summary>
    public static readonly TimeSpan ArmWindow = TimeSpan.FromSeconds(60);

    private DateTimeOffset _armedUntil = DateTimeOffset.MinValue;

    /// <summary>FR-CAP-006 watch toggle. On by default: detecting Win+Shift+S is a required
    /// behavior, and the notification is non-intrusive.</summary>
    public bool WatchEnabled { get; set; } = true;

    public bool IsArmed(DateTimeOffset now) => now <= _armedUntil;

    public void Arm(DateTimeOffset now) => _armedUntil = now + ArmWindow;

    public void Disarm() => _armedUntil = DateTimeOffset.MinValue;

    /// <summary>True only for an arm that timed out unconsumed — the restore watchdog must not
    /// re-activate a window after a consumed (disarmed) or never-armed flow.</summary>
    public bool ArmExpiredUnconsumed(DateTimeOffset now) =>
        _armedUntil != DateTimeOffset.MinValue && now > _armedUntil;

    public CaptureDecision OnClipboardImage(bool isInternalEcho, DateTimeOffset now)
    {
        if (isInternalEcho)
            return CaptureDecision.Ignore; // FR-CAP-005: our own copy is never a capture
        if (IsArmed(now))
        {
            Disarm(); // one capture per request; the next image is unsolicited again
            return CaptureDecision.AutoOpen;
        }
        return WatchEnabled ? CaptureDecision.Notify : CaptureDecision.Ignore;
    }
}
