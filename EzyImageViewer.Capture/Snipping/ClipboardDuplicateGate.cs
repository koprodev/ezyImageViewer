using System.Security.Cryptography;

namespace EzyImageViewer.Capture.Snipping;

/// <summary>
/// FR-CAP-005 중복 감지(M0-B③). 사용자 클립보드 표식이 1순위.
/// 해시 보조 신호는 최근 내부 복사본을 바이트 그대로 즉시 다시 올린 경우만 담당.
/// 최근 해시 고리는 여러 창의 연속 복사를 흡수하고 짧은 TTL은 먼 훗날의 우연한 일치를 방지.
/// 재인코딩된 데이터는 바이트가 달라 표식이나 사용자만 구분 가능.
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
