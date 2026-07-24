using EzyImageViewer.Capture.Snipping;
using Xunit;

namespace EzyImageViewer.Tests.Capture;

/// <summary>FR-CAP-005: 표식이 우선이며 해시는 다음 한 가지 경우만 보조.
/// 최근 내부 복사본을 바이트 그대로 즉시 다시 올린 경우만 감지.
/// 고리는 여러 창의 연속 복사를 흡수하고 TTL은 묵은 해시가 미래 이미지를 막지 않게 함.</summary>
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
        // A→B를 연달아 복사해도 표식 없이 다시 올라온 A는 내부 복사로 읽어야 함.
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
