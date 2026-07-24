using System.Security.AccessControl;
using System.Security;
using System.Security.Principal;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace EzyImageViewer.Infrastructure;

public interface IAppDataPaths
{
    string RootDirectory { get; }
    string SettingsFile { get; }
    string RecentFilesFile { get; }
    string LogsDirectory { get; }
    string RecoveryDirectory { get; }
    string RecoveryQuarantineDirectory { get; }
    string CrashMarkersDirectory { get; }
    string StartupHealthFile { get; }
}

public sealed class AppDataPaths : IAppDataPaths
{
    public AppDataPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
        SettingsFile = UnderRoot("settings.json");
        RecentFilesFile = UnderRoot("recent-files.json");
        LogsDirectory = UnderRoot("logs");
        RecoveryDirectory = UnderRoot("recovery");
        RecoveryQuarantineDirectory = UnderRoot("recovery-quarantine");
        CrashMarkersDirectory = UnderRoot("crash-markers");
        StartupHealthFile = UnderRoot("startup-health.json");
    }

    public string RootDirectory { get; }
    public string SettingsFile { get; }
    public string RecentFilesFile { get; }
    public string LogsDirectory { get; }
    public string RecoveryDirectory { get; }
    public string RecoveryQuarantineDirectory { get; }
    public string CrashMarkersDirectory { get; }
    public string StartupHealthFile { get; }

    public static AppDataPaths CreateDefault()
    {
        var localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var paths = new AppDataPaths(Path.Combine(localAppData, "ezyImageViewer"));
        AppDataSecurity.EnsureProtected(paths);
        return paths;
    }

    private string UnderRoot(string name)
    {
        var path = Path.GetFullPath(Path.Combine(RootDirectory, name));
        var relative = Path.GetRelativePath(RootDirectory, path);
        if (relative.StartsWith("..", StringComparison.Ordinal)
            || Path.IsPathRooted(relative))
            throw new InvalidOperationException("An application-data path escaped its root.");
        return path;
    }
}

public sealed class AppDataProtectionException : IOException
{
    public AppDataProtectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>현재 사용자·SYSTEM 전용 DACL 아래 로컬 데이터 트리 생성·이전.</summary>
public static class AppDataSecurity
{
    public static void EnsureProtected(IAppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (!OperatingSystem.IsWindows())
            return;

        EnsureProtectedWindows(paths);
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureProtectedWindows(IAppDataPaths paths)
    {
        const int attemptCount = 3;
        for (var attempt = 0; attempt < attemptCount; attempt++)
        {
            try
            {
                EnsureProtectedWindowsCore(paths);
                return;
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or SecurityException
                or IdentityNotMappedException
                or PlatformNotSupportedException)
            {
                if (attempt + 1 < attemptCount && IsRetryable(ex))
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(50 * (attempt + 1)));
                    continue;
                }

                throw new AppDataProtectionException(
                    "The application-data directory could not be protected.", ex);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureProtectedWindowsCore(IAppDataPaths paths)
    {
        var root = Path.GetFullPath(paths.RootDirectory);
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var currentUser = identity.User
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (Directory.Exists(root))
            PreflightTree(root);
        CreateOrProtectDirectory(root, currentUser, system);

        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                directory, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("The application-data tree contains a reparse point.");

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        ProtectDirectory(entry, currentUser, system);
                        pending.Push(entry);
                    }
                    else
                    {
                        ProtectFile(entry, currentUser, system);
                    }
                }
                catch (IOException ex) when (SkipTransientEntry(entry, ex))
                {
                }
            }
        }

        VerifyDirectory(root, currentUser, system);
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                directory, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                    {
                        VerifyDirectory(entry, currentUser, system);
                        pending.Push(entry);
                    }
                    else
                    {
                        VerifyFile(entry, currentUser, system);
                    }
                }
                catch (IOException ex) when (SkipTransientEntry(entry, ex))
                {
                }
            }
        }
    }

    private static bool IsTransientSharingViolation(Exception exception)
    {
        var errorCode = exception.HResult & 0xFFFF;
        return errorCode is 32 or 33;
    }

    /// <summary>동시 인스턴스가 막 만든 상속 DACL을 보면 전체 검증을 재시도해 다시 보호.</summary>
    private static bool IsRetryable(Exception exception) =>
        IsTransientSharingViolation(exception) || exception is UnauthorizedAccessException;

    /// <summary>
    /// 동시 생성·이름 변경 중 항목 때문에 전체 트리를 실패시키지 않음.
    /// 원자 쓰기 임시 파일의 공유 위반과 순회 전 사라진 항목만 건너뜀.
    /// </summary>
    private static bool SkipTransientEntry(string path, Exception exception)
    {
        if (exception is FileNotFoundException or DirectoryNotFoundException)
            return true;
        return IsTransientSharingViolation(exception)
            && AtomicFileWriter.IsTempFileName(Path.GetFileName(path));
    }

    public static AppDataPaths CreateProtectedEphemeral()
    {
        if (!OperatingSystem.IsWindows())
        {
            var portable = new AppDataPaths(Path.Combine(
                Path.GetTempPath(), "ezyImageViewer-fail-closed", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(portable.RootDirectory);
            return portable;
        }

        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ezyImageViewer-fail-closed",
            $"{Environment.ProcessId}-{Guid.NewGuid():N}");
        var paths = new AppDataPaths(root);
        EnsureProtected(paths);
        return paths;
    }

    [SupportedOSPlatform("windows")]
    private static void CreateOrProtectDirectory(
        string path,
        SecurityIdentifier currentUser,
        SecurityIdentifier system)
    {
        if (Directory.Exists(path))
        {
            RejectReparsePoint(path);
            ProtectDirectory(path, currentUser, system);
            return;
        }

        var security = CreateDirectorySecurity(currentUser, system);
        FileSystemAclExtensions.CreateDirectory(security, path);
        RejectReparsePoint(path);
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectDirectory(
        string path,
        SecurityIdentifier currentUser,
        SecurityIdentifier system)
    {
        RejectReparsePoint(path);
        EnsureCurrentOwner(
            new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner),
            currentUser);
        var security = CreateDirectorySecurity(currentUser, system);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectFile(
        string path,
        SecurityIdentifier currentUser,
        SecurityIdentifier system)
    {
        RejectReparsePoint(path);
        RejectMultipleHardLinks(path);
        EnsureCurrentOwner(
            new FileInfo(path).GetAccessControl(AccessControlSections.Owner),
            currentUser);
        new FileInfo(path).SetAccessControl(CreateFileSecurity(currentUser, system));
    }

    [SupportedOSPlatform("windows")]
    private static FileSecurity CreateFileSecurity(
        SecurityIdentifier currentUser,
        SecurityIdentifier system)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            system,
            FileSystemRights.FullControl,
            AccessControlType.Allow));
        return security;
    }

    /// <summary>앱 데이터 파일 생성 때 즉시 적용할 명시적 ACL.</summary>
    [SupportedOSPlatform("windows")]
    internal static FileSecurity CreateProtectedFileSecurityForCurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var currentUser = identity.User
            ?? throw new InvalidOperationException("The current Windows user SID is unavailable.");
        return CreateFileSecurity(
            currentUser, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null));
    }

    [SupportedOSPlatform("windows")]
    private static DirectorySecurity CreateDirectorySecurity(
        SecurityIdentifier currentUser,
        SecurityIdentifier system)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(currentUser);
        const InheritanceFlags inheritance =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            currentUser,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            system,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        return security;
    }

    [SupportedOSPlatform("windows")]
    private static void EnsureCurrentOwner(
        FileSystemSecurity security,
        SecurityIdentifier currentUser)
    {
        if (!currentUser.Equals(security.GetOwner(typeof(SecurityIdentifier))))
            throw new UnauthorizedAccessException("The application-data owner is unexpected.");
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyDirectory(
        string path,
        SecurityIdentifier currentUser,
        SecurityIdentifier system)
    {
        RejectReparsePoint(path);
        VerifySecurity(
            new DirectoryInfo(path).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner),
            currentUser,
            system,
            isDirectory: true);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifyFile(
        string path,
        SecurityIdentifier currentUser,
        SecurityIdentifier system)
    {
        RejectReparsePoint(path);
        RejectMultipleHardLinks(path);
        VerifySecurity(
            new FileInfo(path).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner),
            currentUser,
            system,
            isDirectory: false);
    }

    [SupportedOSPlatform("windows")]
    private static void VerifySecurity(
        FileSystemSecurity security,
        SecurityIdentifier currentUser,
        SecurityIdentifier system,
        bool isDirectory)
    {
        if (!security.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("The application-data DACL is not protected.");
        if (!currentUser.Equals(security.GetOwner(typeof(SecurityIdentifier))))
            throw new UnauthorizedAccessException("The application-data owner is unexpected.");

        var allowed = new HashSet<SecurityIdentifier> { currentUser, system };
        var fullControl = new HashSet<SecurityIdentifier>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier)))
        {
            var sid = (SecurityIdentifier)rule.IdentityReference;
            var expectedInheritance = isDirectory
                ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit
                : InheritanceFlags.None;
            if (!allowed.Contains(sid)
                || rule.AccessControlType != AccessControlType.Allow
                || rule.IsInherited
                || rule.InheritanceFlags != expectedInheritance
                || rule.PropagationFlags != PropagationFlags.None)
                throw new UnauthorizedAccessException("The application-data DACL grants an unexpected principal.");
            if ((rule.FileSystemRights & FileSystemRights.FullControl)
                == FileSystemRights.FullControl)
                fullControl.Add(sid);
        }

        if (!fullControl.SetEquals(allowed))
            throw new UnauthorizedAccessException("The application-data DACL is missing required full control.");
    }

    [SupportedOSPlatform("windows")]
    private static void PreflightTree(string root)
    {
        RejectReparsePoint(root);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(
                directory, "*", SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("The application-data tree contains a reparse point.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                }
                else
                {
                    try
                    {
                        RejectMultipleHardLinks(entry);
                    }
                    catch (IOException ex) when (SkipTransientEntry(entry, ex))
                    {
                    }
                }
            }
        }
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The application-data root cannot be a reparse point.");
    }

    [SupportedOSPlatform("windows")]
    private static void RejectMultipleHardLinks(string path)
    {
        using SafeFileHandle handle = File.OpenHandle(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var information))
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            throw new IOException("The application-data file identity could not be verified.", error);
        }
        if (information.NumberOfLinks != 1)
            throw new IOException("The application-data tree contains a hard-linked file.");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
