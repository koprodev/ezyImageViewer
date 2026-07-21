using System.Runtime.InteropServices;

namespace EzyImageViewer.Capture.Snipping;

/// <summary>
/// Message-only Win32 window (HWND_MESSAGE): the delivery target for clipboard-listener,
/// hotkey and tray callback messages, which are posted directly (not broadcast). Create and
/// dispose on a thread that pumps messages — in the app that is the UI thread.
/// </summary>
internal sealed class MessageWindow : IDisposable
{
    /// <summary>Return a value to consume the message; null falls through to DefWindowProc.</summary>
    public delegate nint? MessageHandler(uint message, nuint wParam, nint lParam);

    private readonly WndProc _procKeepAlive; // the native class must never see a collected delegate
    private readonly string _className;
    private readonly MessageHandler _handler;
    private readonly int _creationThreadId = Environment.CurrentManagedThreadId;
    private bool _disposed;

    public nint Handle { get; }

    /// <summary>messageOnly=false makes a hidden top-level window instead — required where the
    /// window must be able to take foreground (tray popup menus dismiss through it).</summary>
    public MessageWindow(MessageHandler handler, bool messageOnly = true)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
        _procKeepAlive = HandleMessage;
        _className = "EzyImageViewer.MessageWindow." + Guid.NewGuid().ToString("N");
        var wndClass = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_procKeepAlive),
            hInstance = GetModuleHandleW(null),
            lpszClassName = _className,
        };
        if (RegisterClassExW(in wndClass) == 0)
            throw new InvalidOperationException($"Message window class registration failed ({Marshal.GetLastPInvokeError()}).");
        Handle = CreateWindowExW(0, _className, string.Empty, 0, 0, 0, 0, 0,
            messageOnly ? HwndMessage : 0, 0, wndClass.hInstance, 0);
        if (Handle == 0)
        {
            UnregisterClassW(_className, wndClass.hInstance);
            throw new InvalidOperationException($"Message window creation failed ({Marshal.GetLastPInvokeError()}).");
        }
    }

    private nint HandleMessage(nint hwnd, uint message, nuint wParam, nint lParam) =>
        _handler(message, wParam, lParam) ?? DefWindowProcW(hwnd, message, wParam, lParam);

    public void Dispose()
    {
        if (_disposed)
            return;
        // DestroyWindow only works from the creating thread; a cross-thread dispose would leak
        // the window and the class silently — fail loud instead.
        if (Environment.CurrentManagedThreadId != _creationThreadId)
            throw new InvalidOperationException("MessageWindow must be disposed on its creation thread.");
        _disposed = true;
        DestroyWindow(Handle);
        UnregisterClassW(_className, GetModuleHandleW(null));
    }

    private const nint HwndMessage = -3;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(in WNDCLASSEXW lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string lpClassName, nint hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int width, int height, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint hWnd, uint msg, nuint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? lpModuleName);
}
