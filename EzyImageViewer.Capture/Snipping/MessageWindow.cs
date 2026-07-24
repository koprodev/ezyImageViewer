using System.Runtime.InteropServices;

namespace EzyImageViewer.Capture.Snipping;

/// <summary>클립보드와 전역 단축키 메시지를 받는 전용 Win32 창. UI 스레드에서만 다룬다.</summary>
internal sealed class MessageWindow : IDisposable
{
    /// <summary>반환값이 없으면 기본 창 프로시저로 넘긴다.</summary>
    public delegate nint? MessageHandler(uint message, nuint wParam, nint lParam);

    private readonly WndProc _procKeepAlive; // 네이티브 창이 쓰는 동안 델리게이트를 단단히 붙잡아 둔다.
    private readonly string _className;
    private readonly MessageHandler _handler;
    private readonly int _creationThreadId = Environment.CurrentManagedThreadId;
    private bool _disposed;

    public nint Handle { get; }

    public MessageWindow(MessageHandler handler)
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
            HwndMessage, 0, wndClass.hInstance, 0);
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
        // 다른 스레드에서 닫으면 창과 클래스가 샌다. 조용한 누수보다 시끄러운 실패가 낫다.
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
