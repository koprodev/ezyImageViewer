using System.Runtime.InteropServices;

namespace EzyImageViewer.Capture.Snipping;

/// <summary>Localized labels for the tray menu — the Capture layer carries no string resources.</summary>
public sealed record TrayIconStrings(string Tooltip, string WatchToggle, string Capture, string OpenWindow);

/// <summary>
/// Tray presence for the watch mode (FR-CAP-006): shows watch state, toggles it, and offers
/// capture/open shortcuts. Owns a hidden top-level window for the callback and popup menu
/// (a message-only window cannot take the foreground a popup menu needs to dismiss).
/// Create, use and dispose on the UI thread.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 1; // WM_APP + 1
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint NinSelect = 0x0400;     // VERSION_4: click/space on the icon
    private const uint NinKeySelect = 0x0401;  // VERSION_4: keyboard select (accessibility)
    private const int CmdWatchToggle = 1;
    private const int CmdCapture = 2;
    private const int CmdOpen = 3;

    private readonly MessageWindow _window;
    private readonly TrayIconStrings _strings;
    private readonly nint _icon;
    private readonly uint _taskbarCreatedMessage;
    private bool _watchEnabled;
    private bool _added;
    private bool _disposed;

    public event Action? WatchToggleRequested;
    public event Action? CaptureRequested;
    public event Action? OpenRequested;

    /// <summary>False when the shell refused the icon — the caller may surface it (FR-CAP-006).</summary>
    public bool IsVisible => _added;

    public TrayIcon(TrayIconStrings strings, string iconPath, bool watchEnabled)
    {
        ArgumentNullException.ThrowIfNull(strings);
        _strings = strings;
        _watchEnabled = watchEnabled;
        _window = new MessageWindow(OnMessage, messageOnly: false);
        _icon = LoadImageW(0, iconPath, ImageIcon, 16, 16, LrLoadFromFile);
        // Explorer restarts (crash, DPI change relaunch) recreate the taskbar; the icon must
        // re-add itself when this broadcast arrives.
        _taskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        AddIcon();
    }

    private void AddIcon()
    {
        var data = BuildData();
        data.uFlags = NifMessage | NifTip | (_icon != 0 ? NifIcon : 0);
        _added = Shell_NotifyIconW(NimAdd, ref data);
        if (!_added)
            return;
        // VERSION_4 callback semantics: richer events incl. keyboard selection (accessibility).
        var version = BuildData();
        version.uTimeoutOrVersion = NotifyIconVersion4;
        Shell_NotifyIconW(NimSetVersion, ref version);
    }

    public void SetWatchEnabled(bool enabled)
    {
        _watchEnabled = enabled;
        if (!_added)
            return;
        var data = BuildData();
        data.uFlags = NifTip;
        Shell_NotifyIconW(NimModify, ref data);
    }

    private NOTIFYICONDATAW BuildData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
        hWnd = _window.Handle,
        uID = 1,
        uCallbackMessage = CallbackMessage,
        hIcon = _icon,
        szTip = $"{_strings.Tooltip} · {_strings.WatchToggle}: {(_watchEnabled ? "ON" : "OFF")}",
    };

    private nint? OnMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == _taskbarCreatedMessage && !_disposed)
        {
            AddIcon();
            return 0;
        }
        if (message != CallbackMessage)
            return null;
        switch ((uint)(lParam & 0xFFFF))
        {
            case WmLButtonDblClk or NinSelect or NinKeySelect:
                OpenRequested?.Invoke();
                return 0;
            case WmRButtonUp or WmContextMenu:
                ShowMenu();
                return 0;
            default:
                return 0;
        }
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == 0)
            return;
        try
        {
            AppendMenuW(menu, MfString | (_watchEnabled ? MfChecked : 0), CmdWatchToggle, _strings.WatchToggle);
            AppendMenuW(menu, MfString, CmdCapture, _strings.Capture);
            AppendMenuW(menu, MfString, CmdOpen, _strings.OpenWindow);
            GetCursorPos(out var point);
            // Foreground + the post-menu no-op message are the documented dismiss dance.
            SetForegroundWindow(_window.Handle);
            var picked = TrackPopupMenuEx(
                menu, TpmReturnCmd | TpmRightButton, point.X, point.Y, _window.Handle, 0);
            PostMessageW(_window.Handle, 0, 0, 0);
            switch (picked)
            {
                case CmdWatchToggle: WatchToggleRequested?.Invoke(); break;
                case CmdCapture: CaptureRequested?.Invoke(); break;
                case CmdOpen: OpenRequested?.Invoke(); break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_added)
        {
            var data = BuildData();
            Shell_NotifyIconW(NimDelete, ref data);
        }
        if (_icon != 0)
            DestroyIcon(_icon);
        _window.Dispose();
    }

    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NotifyIconVersion4 = 4;
    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;
    private const uint ImageIcon = 1;
    private const uint LrLoadFromFile = 0x0010;
    private const uint MfString = 0x0000;
    private const uint MfChecked = 0x0008;
    private const uint TpmReturnCmd = 0x0100;
    private const uint TpmRightButton = 0x0002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint LoadImageW(nint hInst, string name, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(nint hIcon);

    [DllImport("user32.dll")]
    private static extern nint CreatePopupMenu();

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(nint hMenu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(nint hMenu, uint uFlags, nint uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(nint hMenu, uint uFlags, int x, int y, nint hWnd, nint lptpm);

    [DllImport("user32.dll")]
    private static extern bool PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);
}
