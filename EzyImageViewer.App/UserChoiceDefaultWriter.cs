#if EZY_UNPACKAGED
using System.Runtime.InteropServices;
using System.Security.Principal;
using EzyImageViewer.Infrastructure;
using Microsoft.Win32;

namespace EzyImageViewer.App;

internal enum UserChoiceStatus
{
    /// <summary>This app is now the verified effective default for the extension.</summary>
    Set,
    /// <summary>The write failed but the prior default was put back.</summary>
    Restored,
    /// <summary>The write failed and the prior default could not be restored (may be lost).</summary>
    RestoreFailed,
    /// <summary>A global UserChoiceLatest (HashVersion=1) makes classic writes inert.</summary>
    Unsupported,
    /// <summary>Protection state could not be read; treated as blocked (fail-closed).</summary>
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
/// EXPERIMENTAL, unpackaged-only: sets this app as the Windows default for image extensions by
/// writing the per-user UserChoice ProgId + Hash (<see cref="UserChoiceHash"/>). Microsoft does
/// not support this and reserves the right to block it (UCPD.sys, UserChoiceLatest); every write
/// is verified with the official effective-default query and each extension reports its own
/// outcome, restoring the prior default on failure.
/// </summary>
internal static class UserChoiceDefaultWriter
{
    private const string FileExtsKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";
    private const int MinuteRetryBudget = 5;

    public static HashProtectionState DetectHashProtection()
    {
        // A global HashVersion=1 (UserChoiceLatest) makes classic UserChoice writes inert. Any
        // read failure or unexpected type is treated as blocked, never as "classic works".
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

    /// <summary>Registers this app as the default for each extension; one key at a time, each with
    /// its own outcome. A failed extension is restored to its prior default and never throws.</summary>
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
        // Snapshot the prior user choice so a failed write can be undone.
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

    /// <summary>Deletes the prior UserChoice (the only way Explorer accepts a fresh hash) and
    /// writes ProgId + a hash bound to the key's own last-write minute, retrying across a minute
    /// boundary so the stored hash always matches the final write minute.</summary>
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

    /// <summary>Puts the extension back to its prior state: rewrite a valid hash for the prior
    /// ProgId, or (if there was no prior user choice) remove ours so Windows falls back.</summary>
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

    /// <summary>Official effective-default check (AL_EFFECTIVE); distinguishes ProgIds that share
    /// one exe, which AssocQueryString(ASSOCSTR_EXECUTABLE) cannot.</summary>
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

    // Official ASSOCIATIONLEVEL: AL_MACHINE=0, AL_EFFECTIVE=1, AL_USER=2.
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
