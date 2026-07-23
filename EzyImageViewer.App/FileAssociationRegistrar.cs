using System.Runtime.InteropServices;
using EzyImageViewer.Infrastructure;
using Microsoft.Win32;

namespace EzyImageViewer.App;

/// <summary>
/// Per-user (HKCU) "Open With" registration mirroring the Setup registry shape
/// (FileAssociationPolicy). Only this app's values are added or removed; other apps'
/// OpenWithProgids entries and the user's default-app choice are never touched.
/// </summary>
internal static class FileAssociationRegistrar
{
    private const int ShcneAssocChanged = 0x0800_0000;
    private const uint ShcnfIdList = 0x0000;

    public static IReadOnlySet<string> ReadRegisteredExtensions()
    {
        var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var extension in FileAssociationPolicy.SelectableExtensions)
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                FileAssociationPolicy.OpenWithProgidsKeyPath(extension));
            if (key?.GetValue(FileAssociationPolicy.ProgId) is not null)
                registered.Add(extension);
        }
        return registered;
    }

    public static void Apply(IReadOnlySet<string> desired)
    {
        foreach (var extension in desired)
        {
            if (!FileAssociationPolicy.IsSelectable(extension))
                throw new ArgumentException(
                    $"'{extension}' is not a selectable association extension.", nameof(desired));
        }

        if (desired.Count > 0)
            EnsureApplicationRegistration();
        foreach (var extension in FileAssociationPolicy.SelectableExtensions)
        {
            if (desired.Contains(extension))
                RegisterExtension(extension);
            else
                UnregisterExtension(extension);
        }
        // Never strip the shared ProgId/command while any extension still resolves to it as a
        // default (the experimental UserChoice writer may have made it one); a dangling
        // shell\open\command would break those double-clicks.
        if (desired.Count == 0 && !AnyExtensionUsesProgIdAsDefault())
            RemoveApplicationRegistration();
        SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
    }

    private static void EnsureApplicationRegistration()
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The executable path is unavailable.");
        using (var progId = Registry.CurrentUser.CreateSubKey(FileAssociationPolicy.ProgIdKeyPath))
        {
            progId.SetValue(string.Empty, FileAssociationPolicy.ProgIdDisplayName);
            using var icon = progId.CreateSubKey("DefaultIcon");
            icon.SetValue(string.Empty, $"{executable},0");
            using var command = progId.CreateSubKey(@"shell\open\command");
            command.SetValue(string.Empty, $"\"{executable}\" \"%1\"");
        }
        using (var capabilities = Registry.CurrentUser.CreateSubKey(
            FileAssociationPolicy.CapabilitiesKeyPath))
        {
            capabilities.SetValue(
                "ApplicationName", FileAssociationPolicy.RegisteredApplicationName);
            capabilities.SetValue(
                "ApplicationDescription", FileAssociationPolicy.ApplicationDescription);
        }
        using var registrations = Registry.CurrentUser.CreateSubKey(
            FileAssociationPolicy.RegisteredApplicationsKeyPath);
        registrations.SetValue(
            FileAssociationPolicy.RegisteredApplicationName,
            FileAssociationPolicy.CapabilitiesKeyPath);
    }

    private static bool AnyExtensionUsesProgIdAsDefault()
    {
        const string fileExts =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts";
        foreach (var extension in FileAssociationPolicy.SelectableExtensions)
        {
            using var userChoice = Registry.CurrentUser.OpenSubKey(
                $@"{fileExts}\{extension}\UserChoice");
            if (userChoice?.GetValue("ProgId") is string progId
                && string.Equals(progId, FileAssociationPolicy.ProgId, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static void RemoveApplicationRegistration()
    {
        Registry.CurrentUser.DeleteSubKeyTree(
            FileAssociationPolicy.ProgIdKeyPath, throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(
            FileAssociationPolicy.CapabilitiesKeyPath, throwOnMissingSubKey: false);
        using var registrations = Registry.CurrentUser.OpenSubKey(
            FileAssociationPolicy.RegisteredApplicationsKeyPath, writable: true);
        registrations?.DeleteValue(
            FileAssociationPolicy.RegisteredApplicationName, throwOnMissingValue: false);
    }

    private static void RegisterExtension(string extension)
    {
        using (var openWith = Registry.CurrentUser.CreateSubKey(
            FileAssociationPolicy.OpenWithProgidsKeyPath(extension)))
        {
            openWith.SetValue(FileAssociationPolicy.ProgId, string.Empty);
        }
        using var associations = Registry.CurrentUser.CreateSubKey(
            FileAssociationPolicy.FileAssociationsKeyPath);
        associations.SetValue(extension, FileAssociationPolicy.ProgId);
    }

    private static void UnregisterExtension(string extension)
    {
        using (var openWith = Registry.CurrentUser.OpenSubKey(
            FileAssociationPolicy.OpenWithProgidsKeyPath(extension), writable: true))
        {
            openWith?.DeleteValue(FileAssociationPolicy.ProgId, throwOnMissingValue: false);
        }
        using var associations = Registry.CurrentUser.OpenSubKey(
            FileAssociationPolicy.FileAssociationsKeyPath, writable: true);
        associations?.DeleteValue(extension, throwOnMissingValue: false);
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(
        int wEventId, uint uFlags, IntPtr dwItem1, IntPtr dwItem2);
}
