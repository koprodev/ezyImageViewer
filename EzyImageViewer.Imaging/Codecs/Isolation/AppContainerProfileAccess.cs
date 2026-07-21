using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.InteropServices;
using static EzyImageViewer.Imaging.Codecs.Isolation.IsolationNativeMethods;

namespace EzyImageViewer.Imaging.Codecs.Isolation;

internal sealed record AppContainerProfileInfo(
    SecurityIdentifier Sid,
    string LocalAppDataPath,
    string TempPath);

/// <summary>Resolves an AppContainer identity and manages ACLs for classic test profiles only.</summary>
internal static class AppContainerProfileAccess
{
    internal static SafeSidHandle OpenIdentitySid(IsolatedCodecProcessPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();

        return policy.ProfileSource switch
        {
            AppContainerProfileSource.Classic => OpenOrCreateClassicProfile(policy),
            AppContainerProfileSource.ExistingPackage => OpenExistingPackageIdentity(policy),
            _ => throw new ArgumentOutOfRangeException(nameof(policy)),
        };
    }

    private static SafeSidHandle OpenOrCreateClassicProfile(IsolatedCodecProcessPolicy policy)
    {
        using var capabilitySet = AppContainerCapabilitySet.Create(policy.Capabilities);
        var result = CreateAppContainerProfile(
            policy.AppContainerName,
            policy.AppContainerDisplayName,
            policy.AppContainerDescription,
            capabilitySet.Attributes,
            capabilitySet.Count,
            out var sid);
        if (result == ErrorAlreadyExistsHResult)
        {
            result = DeriveAppContainerSidFromAppContainerName(policy.AppContainerName, out sid);
        }
        if (result < 0)
            System.Runtime.InteropServices.Marshal.ThrowExceptionForHR(result);
        if (sid == IntPtr.Zero)
            throw new InvalidOperationException("AppContainer profile returned an empty SID.");
        return new SafeSidHandle(sid);
    }

    private static SafeSidHandle OpenExistingPackageIdentity(IsolatedCodecProcessPolicy policy)
        => OpenExistingPackageIdentity(policy.AppContainerName);

    private static SafeSidHandle OpenExistingPackageIdentity(string appContainerName)
    {
        var result = DeriveAppContainerSidFromAppContainerName(appContainerName, out var sid);
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
        if (sid == IntPtr.Zero)
            throw new InvalidOperationException("AppContainer package identity returned an empty SID.");
        return new SafeSidHandle(sid);
    }

    internal static AppContainerProfileInfo EnsureClassicProfileReadAndExecute(
        IsolatedCodecProcessPolicy policy,
        string directoryPath)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        if (policy.ProfileSource != AppContainerProfileSource.Classic)
        {
            throw new InvalidOperationException(
                "File-system ACL changes are restricted to classic AppContainer profiles.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        if (!Path.IsPathFullyQualified(directoryPath))
            throw new ArgumentException("The host directory path must be absolute.", nameof(directoryPath));

        var directory = new DirectoryInfo(directoryPath);
        if (!directory.Exists)
            throw new DirectoryNotFoundException(directory.FullName);

        using var sidHandle = OpenIdentitySid(policy);
        var profile = GetProfileInfo(sidHandle, ensureTemp: true);
        var security = directory.GetAccessControl(AccessControlSections.Access);
        var rule = new FileSystemAccessRule(
            profile.Sid,
            FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow);
        security.AddAccessRule(rule);
        directory.SetAccessControl(security);
        return profile;
    }

    internal static AppContainerProfileInfo GetProfileInfo(IsolatedCodecProcessPolicy policy)
    {
        using var sidHandle = OpenIdentitySid(policy);
        return GetProfileInfo(
            sidHandle,
            ensureTemp: policy.ProfileSource == AppContainerProfileSource.Classic);
    }

    internal static AppContainerProfileInfo GetExistingPackageProfileInfo(string packageFamilyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFamilyName);
        using var sidHandle = OpenExistingPackageIdentity(packageFamilyName);
        return GetProfileInfo(sidHandle, ensureTemp: false);
    }

    private static AppContainerProfileInfo GetProfileInfo(
        SafeSidHandle sidHandle,
        bool ensureTemp)
    {
        var sid = new SecurityIdentifier(sidHandle.DangerousGetHandle());
        var result = GetAppContainerFolderPath(sid.Value, out var pathPointer);
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
        if (pathPointer == IntPtr.Zero)
            throw new InvalidOperationException("AppContainer profile returned an empty data path.");

        string localAppData;
        try
        {
            localAppData = Marshal.PtrToStringUni(pathPointer)
                ?? throw new InvalidOperationException("AppContainer data path is invalid.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }

        var tempPath = Path.Combine(localAppData, "Temp");
        if (ensureTemp)
            Directory.CreateDirectory(tempPath);
        return new AppContainerProfileInfo(sid, localAppData, tempPath);
    }
}
