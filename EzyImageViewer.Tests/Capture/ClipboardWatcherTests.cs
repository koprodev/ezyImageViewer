using System.Runtime.InteropServices;
using EzyImageViewer.Capture.Snipping;
using Xunit;

namespace EzyImageViewer.Tests.Capture;

/// <summary>
/// FR-CAP-003/004 배관 검사. 메시지 전용 창이 직접 받은 메시지를 이벤트로 올림.
/// SendMessage가 현재 스레드에서 WndProc을 동기 호출하므로 펌프와 실제 클립보드 변경은 불필요.
/// CI가 사용자 클립보드를 만지는 사고도 방지.
/// </summary>
public sealed class ClipboardWatcherTests
{
    private const uint WmClipboardUpdate = 0x031D;
    private const uint WmHotkey = 0x0312;

    [Fact]
    public void ClipboardUpdateMessage_RaisesTheEvent()
    {
        using var watcher = new ClipboardWatcher();
        var raised = 0;
        watcher.ClipboardUpdated += () => raised++;

        SendMessageW(watcher.WindowHandle, WmClipboardUpdate, 0, 0);

        Assert.Equal(1, raised);
    }

    [Fact]
    public void HotkeyMessage_RaisesTheEvent_OnlyForOurId()
    {
        using var watcher = new ClipboardWatcher();
        var raised = 0;
        watcher.HotkeyPressed += () => raised++;

        SendMessageW(watcher.WindowHandle, WmHotkey, 1, 0);
        SendMessageW(watcher.WindowHandle, WmHotkey, 42, 0); // 남의 ID.

        Assert.Equal(1, raised);
    }

    [Fact]
    public void HotkeyRegistration_WorksAndUnregistersOnDispose()
    {
        // Ctrl+Alt+Shift+F24는 사실상 충돌 없음.
        // 잡은 채 두 번 등록해 실제 선점을, 해제 뒤 재등록해 반환을 확인.
        const uint modifiers = ClipboardWatcher.ModControl | ClipboardWatcher.ModAlt | ClipboardWatcher.ModShift;
        const uint f24 = 0x87;

        var first = new ClipboardWatcher();
        Assert.True(first.TryRegisterHotkey(modifiers, f24));
        using (var second = new ClipboardWatcher())
        {
            Assert.False(second.TryRegisterHotkey(modifiers, f24));
        }
        first.Dispose();

        using var third = new ClipboardWatcher();
        Assert.True(third.TryRegisterHotkey(modifiers, f24));
    }

    [Fact]
    public void HotkeyChange_ReleasesThePreviousChordAndOwnsTheReplacement()
    {
        const uint modifiers = ClipboardWatcher.ModControl
            | ClipboardWatcher.ModAlt | ClipboardWatcher.ModShift;
        const uint f23 = 0x86;
        const uint f24 = 0x87;
        using var watcher = new ClipboardWatcher();
        Assert.True(watcher.TryRegisterHotkey(modifiers, f23));

        Assert.True(watcher.TryChangeHotkey(modifiers, f24));

        using var oldProbe = new ClipboardWatcher();
        Assert.True(oldProbe.TryRegisterHotkey(modifiers, f23));
        using var newProbe = new ClipboardWatcher();
        Assert.False(newProbe.TryRegisterHotkey(modifiers, f24));
    }

    [Fact]
    public void HotkeyChange_ConflictRestoresThePreviousChord()
    {
        const uint modifiers = ClipboardWatcher.ModControl
            | ClipboardWatcher.ModAlt | ClipboardWatcher.ModShift;
        const uint f22 = 0x85;
        const uint f24 = 0x87;
        using var watcher = new ClipboardWatcher();
        using var blocker = new ClipboardWatcher();
        Assert.True(watcher.TryRegisterHotkey(modifiers, f22));
        Assert.True(blocker.TryRegisterHotkey(modifiers, f24));

        Assert.False(watcher.TryChangeHotkey(modifiers, f24));

        using var previousProbe = new ClipboardWatcher();
        Assert.False(previousProbe.TryRegisterHotkey(modifiers, f22));
    }

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);
}
