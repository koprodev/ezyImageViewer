using System.Runtime.InteropServices;

namespace EzyImageViewer.Capture.Snipping;

/// <summary>
/// Clipboard-change and global-hotkey delivery (FR-CAP-003/004, M0-B③ design):
/// AddClipboardFormatListener posts WM_CLIPBOARDUPDATE straight to our message-only window —
/// identical behavior packaged or unpackaged. Create and dispose on the UI thread; events fire
/// on it (the message loop is the UI thread's).
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

    /// <summary>The delivery window — diagnostics and message-injection tests.</summary>
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

    /// <summary>FR-CAP-004: false when another app owns the combination — the caller reports it.</summary>
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

    /// <summary>Replaces the active binding and restores the previous one if the requested chord
    /// is unavailable. False means the requested chord was not installed.</summary>
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
