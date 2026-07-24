#if EZY_UNPACKAGED
using System.Runtime.InteropServices;
using System.Security.Principal;
using EzyImageViewer.Infrastructure;
using Microsoft.Win32;

namespace EzyImageViewer.App;

internal enum UserChoiceStatus
{
    /// <summary>이 앱이 확장자의 실제 기본 앱으로 검증됨.</summary>
    Set,
    /// <summary>쓰기 실패, 이전 기본 앱 복원 성공.</summary>
    Restored,
    /// <summary>쓰기 실패, 이전 기본 앱 복원도 실패.</summary>
    RestoreFailed,
    /// <summary>전역 UserChoiceLatest(HashVersion=1)가 고전 방식 쓰기를 차단.</summary>
    Unsupported,
    /// <summary>보호 상태를 못 읽어 차단으로 처리.</summary>
    DetectionFailed,
}

internal sealed record UserChoiceExtensionResult(string Extension, UserChoiceStatus Status);

internal sealed record UserChoiceOutcome(IReadOnlyList<UserChoiceExtensionResult> Results)
{
    public int SetCount => Results.Count(r => r.Status == UserChoiceStatus.Set);
    public int Total => Results.Count;
    public bool AllSet => Total > 0 && SetCount == Total;
    public bool Blocked =>
        Results.All(r => r.Status is UserChoiceStatus.Unsupported or UserChoiceStatus.DetectionFailed)
        && Total > 0;
    public bool AnyRestoreFailed => Results.Any(r => r.Status == UserChoiceStatus.RestoreFailed);
}

internal enum HashProtectionState
{
    Classic,
    UserChoiceLatest,
    DetectionFailed,
}

/// <summary>
/// 실험적 비패키지 전용 기본 앱 설정기. 사용자 UserChoice ProgId와 Hash를 쓰고
/// 공식 실제 기본값 조회로 매 확장자를 검증하며 실패 시 이전 값 복원.
/// </summary>
internal static class UserChoiceDefaultWriter
{
    private const string FileExtsKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";
    private const int MinuteRetryBudget = 5;

    public static HashProtectionState DetectHashProtection()
    {
        // 전역 HashVersion=1이면 고전 쓰기는 무효. 읽기 실패·이상 형식도 안전하게 차단.
        try
        {
            var sid = CurrentUserSid();
            using var key = RegistryKey
                .OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\SystemProtectedUserData\{sid}\AnyoneRead\AppDefaults");
            if (key is null)
                return HashProtectionState.Classic;
            var value = key.GetValue("HashVersion");
            if (value is null)
                return HashProtectionState.Classic;
            return value is int version && version >= 1
                ? HashProtectionState.UserChoiceLatest
                : HashProtectionState.Classic;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException
            or IOException)
        {
            return HashProtectionState.DetectionFailed;
        }
    }

    /// <summary>확장자별 기본 앱 등록. 하나씩 결과를 내고 실패하면 이전 기본값 복원.</summary>
    public static UserChoiceOutcome SetDefaults(IReadOnlyCollection<string> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        var protection = DetectHashProtection();
        if (protection != HashProtectionState.Classic)
        {
            var status = protection == HashProtectionState.UserChoiceLatest
                ? UserChoiceStatus.Unsupported
                : UserChoiceStatus.DetectionFailed;
            return new UserChoiceOutcome(
                [.. extensions.Select(ext => new UserChoiceExtensionResult(ext, status))]);
        }

        var sid = CurrentUserSid();
        var progId = FileAssociationPolicy.ProgId;
        var results = new List<UserChoiceExtensionResult>();
        var changed = false;
        foreach (var extension in extensions)
        {
            // 설정 저장마다 실행되므로 이미 이 앱인 확장자는 삭제·재작성 없이 유지.
            if (IsEffectiveDefault(extension, progId))
            {
                results.Add(new UserChoiceExtensionResult(extension, UserChoiceStatus.Set));
                continue;
            }
            var status = SetOne(extension, sid, progId, out var wrote);
            changed |= wrote;
            results.Add(new UserChoiceExtensionResult(extension, status));
        }
        if (changed)
            SHChangeNotify(0x0800_0000, 0x0000, IntPtr.Zero, IntPtr.Zero);
        return new UserChoiceOutcome(results);
    }

    private static UserChoiceStatus SetOne(
        string extension, string sid, string progId, out bool wrote)
    {
        wrote = false;
        // 쓰기 실패를 되돌릴 수 있게 이전 사용자 선택 확보.
        string? priorProgId = null;
        using (var prior = Registry.CurrentUser.OpenSubKey($@"{FileExtsKeyPath}\{extension}\UserChoice"))
            priorProgId = prior?.GetValue("ProgId") as string;

        try
        {
            using var fileExts = Registry.CurrentUser.CreateSubKey(FileExtsKeyPath);
            using var extKey = fileExts.CreateSubKey(extension);
            wrote = true;
            WriteUserChoice(extKey, extension, sid, progId);
            if (IsEffectiveDefault(extension, progId))
                return UserChoiceStatus.Set;
            return Restore(extKey, extension, sid, priorProgId)
                ? UserChoiceStatus.Restored
                : UserChoiceStatus.RestoreFailed;
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            try
            {
                using var fileExts = Registry.CurrentUser.CreateSubKey(FileExtsKeyPath);
                using var extKey = fileExts.CreateSubKey(extension);
                return Restore(extKey, extension, sid, priorProgId)
                    ? UserChoiceStatus.Restored
                    : UserChoiceStatus.RestoreFailed;
            }
            catch (Exception restoreEx) when (IsRegistryFailure(restoreEx))
            {
                return UserChoiceStatus.RestoreFailed;
            }
        }
    }

    /// <summary>이전 UserChoice 삭제 후 키의 최종 수정 분에 맞춘 ProgId·Hash 작성.</summary>
    private static void WriteUserChoice(
        RegistryKey extKey, string extension, string sid, string progId)
    {
        extKey.DeleteSubKey("UserChoice", throwOnMissingSubKey: false);
        using var userChoice = extKey.CreateSubKey("UserChoice");
        for (var attempt = 0; attempt < MinuteRetryBudget; attempt++)
        {
            userChoice.SetValue("ProgId", progId, RegistryValueKind.String);
            var stamp = LastWriteUtc(userChoice);
            var hash = UserChoiceHash.ComputeHash(extension, sid, progId, stamp);
            userChoice.SetValue("Hash", hash, RegistryValueKind.String);
            if (SameMinute(LastWriteUtc(userChoice), stamp))
                return;
        }
        throw new InvalidOperationException(
            "The UserChoice hash could not be stabilized across a minute boundary.");
    }

    /// <summary>이전 ProgId·Hash를 복원하거나 기존 선택이 없었다면 우리 값을 제거.</summary>
    private static bool Restore(
        RegistryKey extKey, string extension, string sid, string? priorProgId)
    {
        try
        {
            if (priorProgId is null)
            {
                extKey.DeleteSubKey("UserChoice", throwOnMissingSubKey: false);
                return true;
            }
            WriteUserChoice(extKey, extension, sid, priorProgId);
            return IsEffectiveDefault(extension, priorProgId);
        }
        catch (Exception ex) when (IsRegistryFailure(ex))
        {
            return false;
        }
    }

    /// <summary>공식 실제 기본값(AL_EFFECTIVE) 확인. 같은 EXE를 공유하는 ProgId도 구분.</summary>
    private static bool IsEffectiveDefault(string extension, string progId)
    {
        var comType = Type.GetTypeFromCLSID(
            new Guid("591209c7-767b-42b2-9fba-44ee4615f2c7"))
            ?? throw new InvalidOperationException("ApplicationAssociationRegistration is unavailable.");
        var registration = (IApplicationAssociationRegistration)Activator.CreateInstance(comType)!;
        try
        {
            var current = IntPtr.Zero;
            var hr = registration.QueryCurrentDefault(
                extension, AssociationType.FileExtension, AssociationLevel.Effective, out current);
            try
            {
                return hr == 0 && current != IntPtr.Zero && string.Equals(
                    Marshal.PtrToStringUni(current), progId, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                if (current != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(current);
            }
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            Marshal.FinalReleaseComObject(registration);
        }
    }

    private static bool IsRegistryFailure(Exception ex) =>
        ex is UnauthorizedAccessException or System.Security.SecurityException
            or IOException or InvalidOperationException or COMException;

    private static bool SameMinute(DateTime a, DateTime b) =>
        a.Year == b.Year && a.Month == b.Month && a.Day == b.Day
        && a.Hour == b.Hour && a.Minute == b.Minute;

    private static DateTime LastWriteUtc(RegistryKey key)
    {
        var handle = key.Handle.DangerousGetHandle();
        var lastWrite = default(FILETIME);
        var status = RegQueryInfoKeyW(
            handle, null, IntPtr.Zero, IntPtr.Zero, out _, IntPtr.Zero, IntPtr.Zero,
            out _, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref lastWrite);
        if (status != 0)
            throw new InvalidOperationException($"RegQueryInfoKey failed ({status}).");
        var fileTime = ((long)lastWrite.dwHighDateTime << 32) | (uint)lastWrite.dwLowDateTime;
        return DateTime.FromFileTimeUtc(fileTime);
    }

    private static string CurrentUserSid() =>
        WindowsIdentity.GetCurrent().User?.Value
        ?? throw new InvalidOperationException("The current user SID is unavailable.");

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegQueryInfoKeyW(
        IntPtr hKey, string? lpClass, IntPtr lpcchClass, IntPtr lpReserved,
        out uint lpcSubKeys, IntPtr lpcbMaxSubKeyLen, IntPtr lpcbMaxClassLen,
        out uint lpcValues, IntPtr lpcbMaxValueNameLen, IntPtr lpcbMaxValueLen,
        IntPtr lpcbSecurityDescriptor, ref FILETIME lpftLastWriteTime);

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public int dwLowDateTime;
        public int dwHighDateTime;
    }

    // 공식 ASSOCIATIONLEVEL: AL_MACHINE=0, AL_EFFECTIVE=1, AL_USER=2.
    private enum AssociationLevel
    {
        Machine = 0,
        Effective = 1,
        User = 2,
    }

    private enum AssociationType
    {
        FileExtension = 0,
    }

    [ComImport]
    [Guid("4e530b0a-e611-4c77-a3ac-9031d022281b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationAssociationRegistration
    {
        [PreserveSig]
        int QueryCurrentDefault(
            [MarshalAs(UnmanagedType.LPWStr)] string query,
            AssociationType queryType,
            AssociationLevel queryLevel,
            out IntPtr association);
    }
}
#endif
