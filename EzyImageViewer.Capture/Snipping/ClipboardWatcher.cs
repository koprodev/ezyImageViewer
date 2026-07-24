using System.Runtime.InteropServices;

namespace EzyImageViewer.Capture.Snipping;

/// <summary>
/// 클립보드 변경과 전역 단축키 전달(FR-CAP-003/004, M0-B③).
/// AddClipboardFormatListener가 WM_CLIPBOARDUPDATE를 메시지 전용 창으로 곧장 보냄.
/// 패키지 여부와 무관. UI 스레드에서 만들고 해제하며 이벤트도 그곳에서 발생.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const uint WmClipboardUpdate = 0x031D;
    private const uint WmHotkey = 0x0312;
    private const int HotkeyId = 1;

    private readonly MessageWindow _window;
    private bool _hotkeyRegistered;
    private uint _hotkeyModifiers;
    private uint _hotkeyVirtualKey;
    private bool _disposed;

    public event Action? ClipboardUpdated;
    public event Action? HotkeyPressed;

    /// <summary>메시지 전달 창. 진단과 메시지 주입 테스트에도 사용.</summary>
    public nint WindowHandle => _window.Handle;
    public bool HotkeyRegistered => _hotkeyRegistered;

    public ClipboardWatcher()
    {
        _window = new MessageWindow(OnMessage);
        if (!AddClipboardFormatListener(_window.Handle))
        {
            _window.Dispose();
            throw new InvalidOperationException(
                $"Clipboard listener registration failed ({Marshal.GetLastPInvokeError()}).");
        }
    }

    /// <summary>FR-CAP-004: 다른 앱이 조합을 선점했으면 false. 안내는 호출자 몫.</summary>
    public bool TryRegisterHotkey(uint modifiers, uint virtualKey)
    {
        if (_hotkeyRegistered)
            return _hotkeyModifiers == modifiers && _hotkeyVirtualKey == virtualKey;
        _hotkeyRegistered = RegisterHotKey(_window.Handle, HotkeyId, modifiers | ModNoRepeat, virtualKey);
        if (_hotkeyRegistered)
        {
            _hotkeyModifiers = modifiers;
            _hotkeyVirtualKey = virtualKey;
        }
        return _hotkeyRegistered;
    }

    /// <summary>활성 단축키 교체. 새 조합을 못 쓰면 이전 조합 복구.
    /// false면 요청한 조합을 설치하지 못한 것.</summary>
    public bool TryChangeHotkey(uint modifiers, uint virtualKey)
    {
        if (!_hotkeyRegistered)
            return TryRegisterHotkey(modifiers, virtualKey);
        if (_hotkeyModifiers == modifiers && _hotkeyVirtualKey == virtualKey)
            return true;

        var previousModifiers = _hotkeyModifiers;
        var previousVirtualKey = _hotkeyVirtualKey;
        _ = UnregisterHotKey(_window.Handle, HotkeyId);
        _hotkeyRegistered = false;
        if (TryRegisterHotkey(modifiers, virtualKey))
            return true;

        _ = TryRegisterHotkey(previousModifiers, previousVirtualKey);
        return false;
    }

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;

    private nint? OnMessage(uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case WmClipboardUpdate:
                ClipboardUpdated?.Invoke();
                return 0;
            case WmHotkey when (int)wParam == HotkeyId:
                HotkeyPressed?.Invoke();
                return 0;
            default:
                return null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_hotkeyRegistered)
            _ = UnregisterHotKey(_window.Handle, HotkeyId);
        RemoveClipboardFormatListener(_window.Handle);
        _window.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint hWnd, int id);
}
