using EzyImageViewer.Capture.Snipping;
using Xunit;

namespace EzyImageViewer.Tests.Capture;

/// <summary>FR-CAP-005 ([21차] 보완 3): the marker is primary; the hash backup covers only the
/// immediate byte-exact re-post of a RECENT internal copy — a ring absorbs multi-window bursts
/// and a TTL prevents a stale hash from suppressing an unrelated future image.</summary>
public sealed class ClipboardDuplicateGateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Marker_WinsRegardlessOfContent()
    {
        var gate = new ClipboardDuplicateGate();

        Assert.True(gate.IsInternalEcho([1, 2, 3], hasInternalMarker: true, T0));
    }

    [Fact]
    public void HashBackup_RecognizesRecentCopies_WithoutTheMarker()
    {
        var gate = new ClipboardDuplicateGate();
        gate.NoteInternalCopy([10, 20, 30, 40], T0);

        Assert.True(gate.IsInternalEcho([10, 20, 30, 40], hasInternalMarker: false, T0));
        Assert.False(gate.IsInternalEcho([10, 20, 30, 41], hasInternalMarker: false, T0));
    }

    [Fact]
    public void Ring_AbsorbsAMultiWindowBurst_NotJustTheLastCopy()
    {
        // A→B copies in quick succession: a marker-less re-post of A must still read as internal.
        var gate = new ClipboardDuplicateGate();
        gate.NoteInternalCopy([1, 1, 1], T0);
        gate.NoteInternalCopy([2, 2, 2], T0.AddSeconds(1));

        Assert.True(gate.IsInternalEcho([1, 1, 1], hasInternalMarker: false, T0.AddSeconds(2)));
        Assert.True(gate.IsInternalEcho([2, 2, 2], hasInternalMarker: false, T0.AddSeconds(2)));
    }

    [Fact]
    public void Ttl_ExpiresStaleHashes_SoAFutureIdenticalImageIsNotSuppressedForever()
    {
        var gate = new ClipboardDuplicateGate();
        gate.NoteInternalCopy([7, 7, 7], T0);

        var later = T0 + ClipboardDuplicateGate.HashTtl + TimeSpan.FromSeconds(1);
        Assert.False(gate.IsInternalEcho([7, 7, 7], hasInternalMarker: false, later));
    }

    [Fact]
    public void UnknownContent_WithoutMarker_IsNotAnEcho()
    {
        var gate = new ClipboardDuplicateGate();

        Assert.False(gate.IsInternalEcho([9, 9, 9], hasInternalMarker: false, T0));
    }
}
