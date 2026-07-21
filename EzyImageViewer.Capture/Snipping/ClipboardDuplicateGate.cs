using System.Security.Cryptography;

namespace EzyImageViewer.Capture.Snipping;

/// <summary>
/// FR-CAP-005 duplicate detection (M0-B③ design, [21차] 보완 3): the custom clipboard marker is
/// the primary signal; the hash backup covers only the IMMEDIATE byte-exact re-post of a recent
/// internal copy (a consumer that drops foreign formats and re-posts the same PNG bytes).
/// A ring of recent hashes absorbs multi-window copy bursts, and a short TTL prevents a stale
/// hash from suppressing an unrelated future image that happens to match. Re-encoding consumers
/// are out of contract — the bytes differ and only the marker (or the user) can tell.
/// </summary>
public sealed class ClipboardDuplicateGate
{
    public static readonly TimeSpan HashTtl = TimeSpan.FromMinutes(5);
    private const int RingSize = 8;

    private readonly Queue<(byte[] Hash, DateTimeOffset At)> _recent = new();

    public void NoteInternalCopy(ReadOnlySpan<byte> payload, DateTimeOffset now)
    {
        _recent.Enqueue((SHA256.HashData(payload), now));
        while (_recent.Count > RingSize)
            _recent.Dequeue();
    }

    public bool IsInternalEcho(ReadOnlySpan<byte> payload, bool hasInternalMarker, DateTimeOffset now)
    {
        if (hasInternalMarker)
            return true;
        while (_recent.TryPeek(out var oldest) && now - oldest.At > HashTtl)
            _recent.Dequeue();
        var hash = SHA256.HashData(payload);
        foreach (var (candidate, _) in _recent)
        {
            if (hash.AsSpan().SequenceEqual(candidate))
                return true;
        }
        return false;
    }
}
