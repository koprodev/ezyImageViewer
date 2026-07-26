using System.Runtime.InteropServices;

namespace EzyImageViewer.Infrastructure;

public enum FileDeleteOutcome
{
    Recycled,
    PermanentlyDeleted,
    Canceled,
    Failed,
}

public readonly record struct FileDeleteResult(FileDeleteOutcome Outcome, int ErrorCode)
{
    public bool Succeeded => Outcome is FileDeleteOutcome.Recycled
        or FileDeleteOutcome.PermanentlyDeleted;
}

/// <summary>
/// 파일을 휴지통으로 보내는 셸 연동. 네트워크 드라이브처럼 휴지통이 없는 위치에서는
/// Windows가 조용히 완전 삭제로 바꾸므로, 물어보기 전에 어느 쪽인지 미리 알아낸다.
/// </summary>
public static class ShellFileOperations
{
    private const uint FoDelete = 0x0003;
    private const ushort FofSilent = 0x0004;
    private const ushort FofNoConfirmation = 0x0010;
    private const ushort FofAllowUndo = 0x0040;
    private const ushort FofNoErrorUi = 0x0400;

    /// <summary>이 경로가 실제로 휴지통을 거치는가. 확인 문구를 정직하게 쓰기 위한 사전 조회.</summary>
    public static bool CanRecycle(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root))
                return false;
            // UNC 경로는 휴지통이 없다. 드라이브 문자라도 조회에 실패하면 없는 것으로 본다.
            if (root.StartsWith(@"\\", StringComparison.Ordinal))
                return false;
            var info = new ShQueryRecycleBinInfo
            {
                cbSize = Marshal.SizeOf<ShQueryRecycleBinInfo>(),
            };
            return SHQueryRecycleBin(root, ref info) == 0;
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException
            or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>휴지통으로 보낸다. 휴지통이 없는 위치면 Windows가 완전 삭제로 처리한다.</summary>
    public static FileDeleteResult MoveToRecycleBin(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var recyclable = CanRecycle(fullPath);

        var operation = new ShFileOpStruct
        {
            hwnd = IntPtr.Zero,
            wFunc = FoDelete,
            // 셸은 목록의 끝을 널 두 개로 판단한다. 마샬러가 하나를 붙이므로 하나를 더 넣는다.
            pFrom = fullPath + "\0",
            pTo = null,
            fFlags = FofAllowUndo | FofNoConfirmation | FofNoErrorUi | FofSilent,
            fAnyOperationsAborted = 0,
            hNameMappings = IntPtr.Zero,
            lpszProgressTitle = null,
        };

        var code = SHFileOperationW(ref operation);
        if (operation.fAnyOperationsAborted != 0)
            return new FileDeleteResult(FileDeleteOutcome.Canceled, 0);
        if (code != 0)
            return new FileDeleteResult(FileDeleteOutcome.Failed, code);
        return new FileDeleteResult(
            recyclable ? FileDeleteOutcome.Recycled : FileDeleteOutcome.PermanentlyDeleted,
            0);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOpStruct
    {
        public IntPtr hwnd;
        public uint wFunc;
        [MarshalAs(UnmanagedType.LPWStr)] public string pFrom;
        [MarshalAs(UnmanagedType.LPWStr)] public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszProgressTitle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShQueryRecycleBinInfo
    {
        public int cbSize;
        public long i64Size;
        public long i64NumItems;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int SHFileOperationW(ref ShFileOpStruct fileOp);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHQueryRecycleBin(string rootPath, ref ShQueryRecycleBinInfo info);
}
