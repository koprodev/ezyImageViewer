using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.AccessControl;
using System.Security.Principal;
using EzyImageViewer.Imaging.Codecs.Isolation;
using Windows.Management.Core;

namespace EzyImageViewer.Imaging.Codecs;

internal interface ICodecPackageDataResetter
{
    Task ClearAsync(
        string packageFamilyName,
        AppContainerProfileInfo profile,
        CancellationToken cancellationToken);
}

internal sealed class ApplicationDataCodecPackageDataResetter : ICodecPackageDataResetter
{
    private static readonly TimeSpan ClearDeadline = TimeSpan.FromSeconds(10);
    private const int FileNotFoundHResult = unchecked((int)0x80070002);
    private const byte CriticalAceFlag = 0x20;
    private const byte PackageAceFlags =
        (byte)((byte)AceFlags.ObjectInherit | (byte)AceFlags.ContainerInherit | CriticalAceFlag);
    private const byte SystemMandatoryLabelAceType = 0x11;
    private const uint MandatoryLabelNoWriteUp = 0x00000001;
    private const uint LabelSecurityInformation = 0x00000010;
    private const int SeFileObject = 1;

    private readonly Func<string, CancellationToken, Task> _clearApplicationDataAsync;
    private readonly Func<string, AppContainerProfileInfo> _resolveExpectedProfile;

    public ApplicationDataCodecPackageDataResetter()
        : this(
            ClearApplicationDataAsync,
            AppContainerProfileAccess.GetExistingPackageProfileInfo)
    {
    }

    internal ApplicationDataCodecPackageDataResetter(
        Func<string, CancellationToken, Task> clearApplicationDataAsync,
        Func<string, AppContainerProfileInfo> resolveExpectedProfile)
    {
        _clearApplicationDataAsync = clearApplicationDataAsync
            ?? throw new ArgumentNullException(nameof(clearApplicationDataAsync));
        _resolveExpectedProfile = resolveExpectedProfile
            ?? throw new ArgumentNullException(nameof(resolveExpectedProfile));
    }

    public async Task ClearAsync(
        string packageFamilyName,
        AppContainerProfileInfo profile,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageFamilyName);
        ArgumentNullException.ThrowIfNull(profile);
        using var deadline = new CancellationTokenSource(ClearDeadline);
        ValidateExpectedProfile(
            _resolveExpectedProfile(packageFamilyName),
            profile);
        var scope = ResolveProfileRootScope(packageFamilyName, profile);

        using var operation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        Exception? applicationDataFailure = null;
        try
        {
            await _clearApplicationDataAsync(packageFamilyName, operation.Token)
                .WaitAsync(operation.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsMissingFrameworkApplicationData(ex))
        {
            // Framework-only packages can lack the ApplicationData state files.
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            applicationDataFailure = new OperationCanceledException(
                "The codec package data reset was canceled.",
                ex,
                cancellationToken);
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
        {
            applicationDataFailure = new TimeoutException(
                $"The codec package data reset exceeded {ClearDeadline}.", ex);
        }
        catch (Exception ex)
        {
            applicationDataFailure = ex;
        }

        Exception? profileFailure = null;
        try
        {
            ClearProfile(scope, profile.Sid, deadline.Token);
        }
        catch (OperationCanceledException ex) when (deadline.IsCancellationRequested)
        {
            profileFailure = new TimeoutException(
                $"The codec package data reset exceeded {ClearDeadline}.", ex);
        }
        catch (Exception ex)
        {
            profileFailure = ex;
        }

        if (profileFailure is not null)
        {
            if (applicationDataFailure is not null)
            {
                throw new AggregateException(
                    "ApplicationData and AppContainer profile cleanup both failed.",
                    applicationDataFailure,
                    profileFailure);
            }

            ExceptionDispatchInfo.Capture(profileFailure).Throw();
        }

        if (applicationDataFailure is not null)
            ExceptionDispatchInfo.Capture(applicationDataFailure).Throw();
    }

    private static async Task ClearApplicationDataAsync(
        string packageFamilyName,
        CancellationToken cancellationToken) =>
        await ApplicationDataManager
            .CreateForPackageFamily(packageFamilyName)
            .ClearAsync()
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

    private static bool IsMissingFrameworkApplicationData(Exception exception) =>
        exception is FileNotFoundException || exception.HResult == FileNotFoundHResult;

    private static void ClearProfile(
        ProfileRootScope scope,
        SecurityIdentifier packageSid,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (TryGetPhysicalRootAttributes(scope.RootPath, out _))
        {
            foreach (var entry in new DirectoryInfo(scope.RootPath)
                         .EnumerateFileSystemInfos()
                         .ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeleteEntryWithoutFollowingReparsePoints(
                    scope.RootPath,
                    entry,
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.SetAttributes(scope.RootPath, FileAttributes.Normal);
            Directory.Delete(scope.RootPath, recursive: false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (TryGetPhysicalRootAttributes(scope.RootPath, out _))
            throw new IOException("The old AppContainer profile root was not removed.");

        var root = Directory.CreateDirectory(scope.RootPath);
        File.SetAttributes(scope.RootPath, FileAttributes.Normal);
        ApplyCanonicalPackageDacl(root, packageSid);
        ApplyLowMandatoryLabel(scope.RootPath);

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(scope.TempPath);
        VerifyProfilePostcondition(scope, packageSid, cancellationToken);
    }

    private static ProfileRootScope ResolveProfileRootScope(
        string packageFamilyName,
        AppContainerProfileInfo profile)
    {
        var rootPath = NormalizeProfileRoot(profile.LocalAppDataPath);
        var root = new DirectoryInfo(rootPath);
        if (!string.Equals(root.Name, "AC", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The AppContainer profile root must be the AC directory.");

        var packageParent = root.Parent
            ?? throw new InvalidDataException("The AppContainer profile has no package parent.");
        if (!string.Equals(packageParent.Name, packageFamilyName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The AppContainer profile parent does not match the package family name.");
        }

        var packagesRoot = packageParent.Parent
            ?? throw new InvalidDataException("The AppContainer package parent has no Packages root.");
        if (!string.Equals(packagesRoot.Name, "Packages", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The AppContainer profile is outside a Packages root.");

        var expectedRootPath = Path.Combine(packageParent.FullName, "AC");
        if (!string.Equals(rootPath, expectedRootPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The AppContainer profile root is outside its package parent.");
        var expectedPackageParentPath = Path.Combine(packagesRoot.FullName, packageFamilyName);
        if (!string.Equals(
                packageParent.FullName,
                expectedPackageParentPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The AppContainer package parent is not the expected direct Packages child.");
        }

        var tempPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile.TempPath));
        var expectedTempPath = Path.Combine(rootPath, "Temp");
        if (!string.Equals(tempPath, expectedTempPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The AppContainer temporary path must be the profile's direct Temp child.");
        }

        ValidatePackageSid(packageFamilyName, profile.Sid);
        EnsurePhysicalDirectory(packagesRoot, "Packages root");
        if (!TryGetFileSystemAttributes(packageParent.FullName, out _))
            Directory.CreateDirectory(packageParent.FullName);

        EnsurePhysicalDirectory(packagesRoot, "Packages root");
        EnsurePhysicalDirectory(packageParent, "package parent");
        if (!string.Equals(
                packageParent.FullName,
                Path.Combine(packagesRoot.FullName, packageFamilyName),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "The created AppContainer package parent escaped its validated scope.");
        }

        var parentSecurity = packageParent.GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        var scope = new ProfileRootScope(
            rootPath,
            tempPath,
            packageParent,
            parentSecurity.GetSecurityDescriptorBinaryForm(),
            packageParent.Attributes,
            parentSecurity.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
                ?? throw new IOException("The package parent owner SID is unavailable."));
        _ = TryGetPhysicalRootAttributes(rootPath, out _);
        VerifyParentUnchanged(scope);
        return scope;
    }

    private static void ValidateExpectedProfile(
        AppContainerProfileInfo expected,
        AppContainerProfileInfo provided)
    {
        ArgumentNullException.ThrowIfNull(expected);
        if (!expected.Sid.Equals(provided.Sid)
            || !string.Equals(
                NormalizeProfileRoot(expected.LocalAppDataPath),
                NormalizeProfileRoot(provided.LocalAppDataPath),
                StringComparison.OrdinalIgnoreCase)
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(expected.TempPath)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(provided.TempPath)),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The AppContainer profile does not match the OS-derived package profile.");
        }
    }

    private static string NormalizeProfileRoot(string profilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profilePath);
        if (!Path.IsPathFullyQualified(profilePath))
            throw new ArgumentException("The AppContainer profile path must be absolute.", nameof(profilePath));

        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(profilePath));
        var pathRoot = Path.TrimEndingDirectorySeparator(
            Path.GetPathRoot(fullPath)
                ?? throw new ArgumentException("The AppContainer profile path has no root.", nameof(profilePath)));
        if (string.Equals(fullPath, pathRoot, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("A volume root cannot be used as an AppContainer profile.", nameof(profilePath));
        return fullPath;
    }

    private static void EnsurePhysicalDirectory(DirectoryInfo directory, string description)
    {
        if (!TryGetFileSystemAttributes(directory.FullName, out var attributes))
            throw new DirectoryNotFoundException(directory.FullName);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException($"The AppContainer {description} must be a physical directory.");
        }
        directory.Refresh();
    }

    private static bool TryGetFileSystemAttributes(
        string path,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }
    }

    private static bool TryGetPhysicalRootAttributes(
        string rootPath,
        out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(rootPath);
        }
        catch (FileNotFoundException)
        {
            attributes = default;
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            attributes = default;
            return false;
        }

        if ((attributes & FileAttributes.Directory) == 0)
            throw new IOException("The AppContainer profile root is not a directory.");
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The AppContainer profile root cannot be a reparse point.");
        return true;
    }

    private static void ValidatePackageSid(
        string packageFamilyName,
        SecurityIdentifier expectedSid)
    {
        var result = IsolationNativeMethods.DeriveAppContainerSidFromAppContainerName(
            packageFamilyName,
            out var sidPointer);
        if (result < 0)
            Marshal.ThrowExceptionForHR(result);
        if (sidPointer == IntPtr.Zero)
            throw new InvalidOperationException("The package family name returned an empty SID.");

        using var sidHandle = new IsolationNativeMethods.SafeSidHandle(sidPointer);
        var derivedSid = new SecurityIdentifier(sidPointer);
        if (!derivedSid.Equals(expectedSid))
            throw new InvalidDataException("The AppContainer SID does not match the package family name.");
    }

    private static void DeleteEntryWithoutFollowingReparsePoints(
        string rootPath,
        FileSystemInfo entry,
        CancellationToken cancellationToken)
    {
        var pending = new Stack<(FileSystemInfo Entry, bool DeleteAfterChildren)>();
        pending.Push((entry, false));

        while (pending.TryPop(out var frame))
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureEntryIsWithinProfile(rootPath, frame.Entry.FullName);
            frame.Entry.Refresh();
            var attributes = frame.Entry.Attributes;
            var isDirectory = (attributes & FileAttributes.Directory) != 0
                || frame.Entry is DirectoryInfo;

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if (isDirectory)
                    Directory.Delete(frame.Entry.FullName, recursive: false);
                else
                    File.Delete(frame.Entry.FullName);
                continue;
            }

            if (!isDirectory)
            {
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(
                        frame.Entry.FullName,
                        attributes & ~FileAttributes.ReadOnly);
                }

                File.Delete(frame.Entry.FullName);
                continue;
            }

            if (frame.DeleteAfterChildren)
            {
                File.SetAttributes(frame.Entry.FullName, FileAttributes.Normal);
                Directory.Delete(frame.Entry.FullName, recursive: false);
                continue;
            }

            pending.Push((frame.Entry, true));
            foreach (var child in new DirectoryInfo(frame.Entry.FullName)
                         .EnumerateFileSystemInfos()
                         .ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureEntryIsWithinProfile(rootPath, child.FullName);
                pending.Push((child, false));
            }
        }
    }

    private static void EnsureEntryIsWithinProfile(string rootPath, string entryPath)
    {
        var fullEntryPath = Path.GetFullPath(entryPath);
        var rootPrefix = string.Concat(rootPath, Path.DirectorySeparatorChar);
        if (!fullEntryPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException(
                "An AppContainer profile entry resolved outside the profile root.");
        }
    }

    private static void ApplyCanonicalPackageDacl(
        DirectoryInfo root,
        SecurityIdentifier packageSid)
    {
        var security = root.GetAccessControl(AccessControlSections.Access);
        var descriptor = new RawSecurityDescriptor(
            security.GetSecurityDescriptorBinaryForm(),
            0);
        var sourceAcl = descriptor.DiscretionaryAcl
            ?? throw new IOException("The new AppContainer profile root has no DACL.");
        var canonicalAcl = new RawAcl(sourceAcl.Revision, sourceAcl.Count + 1);
        canonicalAcl.InsertAce(0, new CommonAce(
            (AceFlags)PackageAceFlags,
            AceQualifier.AccessAllowed,
            (int)FileSystemRights.FullControl,
            packageSid,
            isCallback: false,
            opaque: null));
        var targetIndex = 1;
        foreach (GenericAce ace in sourceAcl)
        {
            if (ace is KnownAce knownAce && knownAce.SecurityIdentifier.Equals(packageSid))
                continue;
            canonicalAcl.InsertAce(targetIndex++, ace);
        }

        descriptor.DiscretionaryAcl = canonicalAcl;
        var binary = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(binary, 0);
        security.SetSecurityDescriptorBinaryForm(binary, AccessControlSections.Access);
        root.SetAccessControl(security);
    }

    private static void ApplyLowMandatoryLabel(string rootPath)
    {
        var labelAcl = CreateLowMandatoryLabelAcl();
        var aclPointer = Marshal.AllocHGlobal(labelAcl.Length);
        try
        {
            Marshal.Copy(labelAcl, 0, aclPointer, labelAcl.Length);
            var error = SetNamedSecurityInfo(
                rootPath,
                SeFileObject,
                LabelSecurityInformation,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero,
                aclPointer);
            if (error != 0)
                throw new Win32Exception(checked((int)error));
        }
        finally
        {
            Marshal.FreeHGlobal(aclPointer);
        }
    }

    private static byte[] CreateLowMandatoryLabelAcl()
    {
        var lowSid = new SecurityIdentifier("S-1-16-4096");
        var aceSize = checked(8 + lowSid.BinaryLength);
        var aclSize = checked(8 + aceSize);
        var acl = new byte[aclSize];
        acl[0] = GenericAcl.AclRevision;
        BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(2), checked((ushort)aclSize));
        BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(4), 1);
        acl[8] = SystemMandatoryLabelAceType;
        acl[9] = (byte)((byte)AceFlags.ObjectInherit | (byte)AceFlags.ContainerInherit);
        BinaryPrimitives.WriteUInt16LittleEndian(acl.AsSpan(10), checked((ushort)aceSize));
        BinaryPrimitives.WriteUInt32LittleEndian(acl.AsSpan(12), MandatoryLabelNoWriteUp);
        lowSid.GetBinaryForm(acl, 16);
        return acl;
    }

    internal static bool HasCanonicalLowMandatoryLabel(string rootPath) =>
        ReadMandatoryLabelAcl(rootPath).AsSpan().SequenceEqual(CreateLowMandatoryLabelAcl());

    private static byte[] ReadMandatoryLabelAcl(string rootPath)
    {
        var error = GetNamedSecurityInfo(
            rootPath,
            SeFileObject,
            LabelSecurityInformation,
            out _,
            out _,
            out _,
            out var labelAclPointer,
            out var descriptorPointer);
        if (error != 0)
        {
            if (descriptorPointer != IntPtr.Zero)
                _ = LocalFree(descriptorPointer);
            throw new Win32Exception(checked((int)error));
        }
        if (descriptorPointer == IntPtr.Zero)
            throw new IOException("The AppContainer profile security descriptor is empty.");

        try
        {
            if (labelAclPointer == IntPtr.Zero)
            {
                if (!GetSecurityDescriptorSacl(
                        descriptorPointer,
                        out var labelPresent,
                        out labelAclPointer,
                        out _))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                if (!labelPresent || labelAclPointer == IntPtr.Zero)
                    return [];
            }
            var aclSize = unchecked((ushort)Marshal.ReadInt16(labelAclPointer, 2));
            var acl = new byte[aclSize];
            Marshal.Copy(labelAclPointer, acl, 0, acl.Length);
            return acl;
        }
        finally
        {
            _ = LocalFree(descriptorPointer);
        }
    }

    private static void VerifyProfilePostcondition(
        ProfileRootScope scope,
        SecurityIdentifier packageSid,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifyParentUnchanged(scope);
        if (!TryGetPhysicalRootAttributes(scope.RootPath, out var rootAttributes))
            throw new IOException("The AppContainer profile root was not recreated.");
        var forbiddenAttributes = FileAttributes.ReadOnly
            | FileAttributes.Hidden
            | FileAttributes.System
            | FileAttributes.ReparsePoint
            | FileAttributes.Temporary
            | FileAttributes.Offline;
        if ((rootAttributes & forbiddenAttributes) != 0)
            throw new IOException("The AppContainer profile root retained unsafe attributes.");

        var rootEntries = new DirectoryInfo(scope.RootPath).EnumerateFileSystemInfos().ToArray();
        if (rootEntries.Length != 1
            || !string.Equals(
                Path.GetFullPath(rootEntries[0].FullName),
                scope.TempPath,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("The AppContainer profile cleanup left unexpected root entries.");
        }

        rootEntries[0].Refresh();
        if ((rootEntries[0].Attributes & FileAttributes.ReparsePoint) != 0
            || rootEntries[0] is not DirectoryInfo tempDirectory)
        {
            throw new IOException("The AppContainer Temp path is not a physical directory.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (tempDirectory.EnumerateFileSystemInfos().Any())
            throw new IOException("The AppContainer Temp directory was not emptied.");
        VerifyCanonicalRootSecurity(scope, packageSid);
    }

    private static void VerifyParentUnchanged(ProfileRootScope scope)
    {
        EnsurePhysicalDirectory(scope.PackageParent, "package parent");
        if (scope.PackageParent.Attributes != scope.ParentAttributes)
            throw new IOException("The package parent attributes changed during profile cleanup.");
        var security = scope.PackageParent.GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        if (!security.GetSecurityDescriptorBinaryForm().AsSpan()
            .SequenceEqual(scope.ParentSecurityDescriptor))
        {
            throw new IOException("The package parent security descriptor changed during profile cleanup.");
        }
    }

    private static void VerifyCanonicalRootSecurity(
        ProfileRootScope scope,
        SecurityIdentifier packageSid)
    {
        var security = new DirectoryInfo(scope.RootPath).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        if (!security.AreAccessRulesCanonical)
            throw new IOException("The AppContainer profile DACL is not canonical.");
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner
            || !owner.Equals(scope.ParentOwner))
        {
            throw new IOException("The AppContainer profile owner differs from the package parent.");
        }

        var descriptor = new RawSecurityDescriptor(
            security.GetSecurityDescriptorBinaryForm(),
            0);
        var dacl = descriptor.DiscretionaryAcl
            ?? throw new IOException("The AppContainer profile root has no DACL.");
        var packageAces = dacl
            .OfType<CommonAce>()
            .Where(ace => ace.SecurityIdentifier.Equals(packageSid))
            .ToArray();
        if (packageAces.Length != 1
            || packageAces[0].AceQualifier != AceQualifier.AccessAllowed
            || packageAces[0].AccessMask != (int)FileSystemRights.FullControl
            || (byte)packageAces[0].AceFlags != PackageAceFlags)
        {
            throw new IOException("The AppContainer package SID access rule is not canonical.");
        }
        if (!HasCanonicalLowMandatoryLabel(scope.RootPath))
            throw new IOException("The AppContainer profile Low mandatory label is not canonical.");
    }

    private sealed record ProfileRootScope(
        string RootPath,
        string TempPath,
        DirectoryInfo PackageParent,
        byte[] ParentSecurityDescriptor,
        FileAttributes ParentAttributes,
        SecurityIdentifier ParentOwner);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "SetNamedSecurityInfoW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    private static extern uint SetNamedSecurityInfo(
        string objectName,
        int objectType,
        uint securityInformation,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport(
        "advapi32.dll",
        EntryPoint = "GetNamedSecurityInfoW",
        CharSet = CharSet.Unicode,
        ExactSpelling = true)]
    private static extern uint GetNamedSecurityInfo(
        string objectName,
        int objectType,
        uint securityInformation,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorSacl(
        IntPtr securityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool saclPresent,
        out IntPtr sacl,
        [MarshalAs(UnmanagedType.Bool)] out bool saclDefaulted);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}
