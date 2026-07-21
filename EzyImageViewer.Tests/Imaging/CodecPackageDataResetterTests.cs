using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using EzyImageViewer.Imaging.Codecs;
using EzyImageViewer.Imaging.Codecs.Isolation;
using Xunit;

namespace EzyImageViewer.Tests.Imaging;

public sealed class CodecPackageDataResetterTests
{
    private const string PackageFamilyName = "GRTech.ezyImageViewer.CodecHost_test";
    private const byte CanonicalPackageAceFlags = 0x23;

    [Fact]
    public async Task ClearAsync_FrameworkApplicationDataMissing_RecreatesCanonicalAcRoot()
    {
        using var fixture = new ProfileFixture();
        var parentSecurity = fixture.GetPackageParentSecurityDescriptor();
        var parentAttributes = new DirectoryInfo(fixture.PackageParentPath).Attributes;
        var outsideFile = Path.Combine(fixture.OutsidePath, "must-survive.txt");
        File.WriteAllText(outsideFile, "outside");
        var directoryLinked = TryCreateDirectoryLink(
            Path.Combine(fixture.Profile.LocalAppDataPath, "outside-link"),
            fixture.OutsidePath);
        var fileLinked = TryCreateFileLink(
            Path.Combine(fixture.Profile.LocalAppDataPath, "outside-file-link"),
            outsideFile);
        var adsPath = string.Concat(fixture.Profile.LocalAppDataPath, ":root-state");
        File.WriteAllText(adsPath, "persistent-state");
        MutateRootMetadata(fixture.Profile.LocalAppDataPath);
        var calls = 0;
        var resetter = fixture.CreateResetter((_, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new FileNotFoundException("ReadStateName");
        });

        await resetter.ClearAsync(
            PackageFamilyName,
            fixture.Profile,
            CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.ThrowsAny<IOException>(() => File.ReadAllText(adsPath));
        Assert.Equal(parentSecurity, fixture.GetPackageParentSecurityDescriptor());
        Assert.Equal(parentAttributes, new DirectoryInfo(fixture.PackageParentPath).Attributes);
        if (directoryLinked || fileLinked)
            Assert.True(File.Exists(outsideFile));
        AssertCanonicalProfile(fixture);
    }

    [Fact]
    public async Task ClearAsync_ApplicationDataSuccess_StillRecreatesCanonicalAcRoot()
    {
        using var fixture = new ProfileFixture();
        var resetter = CreateSuccessfulResetter(fixture);

        await resetter.ClearAsync(
            PackageFamilyName,
            fixture.Profile,
            CancellationToken.None);

        AssertCanonicalProfile(fixture);
    }

    [Fact]
    public async Task ClearAsync_MissingPackageParent_BootstrapsCanonicalProfile()
    {
        using var fixture = new ProfileFixture(createProfile: false);
        var resetter = CreateSuccessfulResetter(fixture);

        Assert.False(Directory.Exists(fixture.PackageParentPath));

        await resetter.ClearAsync(
            PackageFamilyName,
            fixture.Profile,
            CancellationToken.None);

        AssertCanonicalProfile(fixture);
    }

    [Fact]
    public async Task ClearAsync_MissingPackageParentOutsidePackages_DoesNotCreateIt()
    {
        using var fixture = new ProfileFixture(createProfile: false);
        var unsafeRoot = Directory.CreateDirectory(
            Path.Combine(fixture.DirectoryPath, "NotPackages"));
        var unsafeParentPath = Path.Combine(unsafeRoot.FullName, PackageFamilyName);
        var unsafeProfilePath = Path.Combine(unsafeParentPath, "AC");
        var unsafeProfile = fixture.Profile with
        {
            LocalAppDataPath = unsafeProfilePath,
            TempPath = Path.Combine(unsafeProfilePath, "Temp"),
        };
        var resetter = CreateSuccessfulResetter(fixture, unsafeProfile);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            resetter.ClearAsync(
                PackageFamilyName,
                unsafeProfile,
                CancellationToken.None));

        Assert.False(Directory.Exists(unsafeParentPath));
        Assert.False(Directory.Exists(fixture.PackageParentPath));
    }

    [Fact]
    public async Task ClearAsync_DeepTree_ScrubsIteratively()
    {
        using var fixture = new ProfileFixture();
        var deepestPath = fixture.Profile.LocalAppDataPath;
        const int depth = 256;
        for (var index = 0; index < depth; index++)
        {
            deepestPath = Path.Combine(deepestPath, "d");
            Directory.CreateDirectory(deepestPath);
        }

        File.WriteAllText(Path.Combine(deepestPath, "state.bin"), "state");
        var resetter = CreateSuccessfulResetter(fixture);

        await resetter.ClearAsync(
            PackageFamilyName,
            fixture.Profile,
            CancellationToken.None);

        AssertCanonicalProfile(fixture);
    }

    [SymbolicLinkFact]
    public async Task ClearAsync_NestedOutsideLink_ScrubsWithoutFollowingLink()
    {
        using var fixture = new ProfileFixture();
        var outsideFile = Path.Combine(fixture.OutsidePath, "must-survive-nested-reset.txt");
        File.WriteAllText(outsideFile, "outside");
        Assert.True(TryCreateDirectoryLink(
            Path.Combine(fixture.Profile.LocalAppDataPath, "outside-link"),
            fixture.OutsidePath));
        var resetter = CreateSuccessfulResetter(fixture);

        await resetter.ClearAsync(
            PackageFamilyName,
            fixture.Profile,
            CancellationToken.None);

        Assert.True(File.Exists(outsideFile));
        AssertCanonicalProfile(fixture);
    }

    [Fact]
    public async Task ClearAsync_ApplicationDataFailure_ScrubsProfileThenPropagates()
    {
        using var fixture = new ProfileFixture();
        var resetter = fixture.CreateResetter(
            static (_, _) => throw new InvalidOperationException("simulated clear failure"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resetter.ClearAsync(
                PackageFamilyName,
                fixture.Profile,
                CancellationToken.None));

        Assert.Equal("simulated clear failure", exception.Message);
        AssertCanonicalProfile(fixture);
    }

    [Fact]
    public async Task ClearAsync_Cancellation_ScrubsProfileBeforePropagatingCancellation()
    {
        using var fixture = new ProfileFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var resetter = fixture.CreateResetter(
            static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            resetter.ClearAsync(
                PackageFamilyName,
                fixture.Profile,
                cancellation.Token));

        AssertCanonicalProfile(fixture);
    }

    [Fact]
    public async Task ClearAsync_RejectsTempOutsideExactAcRootWithoutDeletingState()
    {
        using var fixture = new ProfileFixture();
        var unsafeProfile = fixture.Profile with
        {
            TempPath = fixture.OutsidePath,
        };
        var resetter = CreateSuccessfulResetter(fixture);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            resetter.ClearAsync(
                PackageFamilyName,
                unsafeProfile,
                CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(
            fixture.Profile.LocalAppDataPath,
            "nested",
            "state.bin")));
    }

    [SymbolicLinkFact]
    public async Task ClearAsync_RejectsReparsePointAcRootWithoutFollowingIt()
    {
        using var fixture = new ProfileFixture();
        Directory.Delete(fixture.Profile.LocalAppDataPath, recursive: true);
        var outsideFile = Path.Combine(fixture.OutsidePath, "must-survive.txt");
        File.WriteAllText(outsideFile, "outside");
        Assert.True(TryCreateDirectoryLink(
            fixture.Profile.LocalAppDataPath,
            fixture.OutsidePath));
        var resetter = CreateSuccessfulResetter(fixture);

        await Assert.ThrowsAsync<IOException>(() =>
            resetter.ClearAsync(
                PackageFamilyName,
                fixture.Profile,
                CancellationToken.None));

        Assert.True(File.Exists(outsideFile));
    }

    [SymbolicLinkFact]
    public async Task ClearAsync_RejectsReparsePointPackageParentWithoutFollowingIt()
    {
        using var fixture = new ProfileFixture();
        Directory.Delete(fixture.PackageParentPath, recursive: true);
        var outsideParent = Directory.CreateDirectory(Path.Combine(
            fixture.OutsidePath,
            "package-parent"));
        var outsideAc = Directory.CreateDirectory(Path.Combine(outsideParent.FullName, "AC"));
        var outsideFile = Path.Combine(outsideAc.FullName, "must-survive.txt");
        File.WriteAllText(outsideFile, "outside");
        Assert.True(TryCreateDirectoryLink(
            fixture.PackageParentPath,
            outsideParent.FullName));
        var resetter = CreateSuccessfulResetter(fixture);

        await Assert.ThrowsAsync<IOException>(() =>
            resetter.ClearAsync(
                PackageFamilyName,
                fixture.Profile,
                CancellationToken.None));

        Assert.True(File.Exists(outsideFile));
    }

    [Fact]
    public async Task ClearAsync_RejectsPackageFamilyNameThatDoesNotOwnAcRoot()
    {
        using var fixture = new ProfileFixture();
        var resetter = CreateSuccessfulResetter(fixture);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            resetter.ClearAsync(
                "GRTech.ezyImageViewer.WrongPackage_test",
                fixture.Profile,
                CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(
            fixture.Profile.LocalAppDataPath,
            "nested",
            "state.bin")));
    }

    [Fact]
    public async Task ClearAsync_RejectsPackageSidThatDoesNotMatchFamilyName()
    {
        using var fixture = new ProfileFixture();
        var wrongSidProfile = fixture.Profile with
        {
            Sid = new SecurityIdentifier(WellKnownSidType.WorldSid, domainSid: null),
        };
        var resetter = CreateSuccessfulResetter(fixture);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            resetter.ClearAsync(
                PackageFamilyName,
                wrongSidProfile,
                CancellationToken.None));

        Assert.True(File.Exists(Path.Combine(
            fixture.Profile.LocalAppDataPath,
            "nested",
            "state.bin")));
    }

    private static ApplicationDataCodecPackageDataResetter CreateSuccessfulResetter(
        ProfileFixture fixture,
        AppContainerProfileInfo? expectedProfile = null) => fixture.CreateResetter(
            static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            },
            expectedProfile);

    private static void MutateRootMetadata(string rootPath)
    {
        var root = new DirectoryInfo(rootPath);
        var security = root.GetAccessControl(AccessControlSections.Access);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
        security.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.WorldSid, domainSid: null),
            FileSystemRights.Write,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        root.SetAccessControl(security);
        File.SetAttributes(
            rootPath,
            root.Attributes | FileAttributes.Hidden | FileAttributes.ReadOnly);
    }

    private static void AssertCanonicalProfile(ProfileFixture fixture)
    {
        var root = new DirectoryInfo(fixture.Profile.LocalAppDataPath);
        root.Refresh();
        Assert.True(root.Exists);
        Assert.Equal(
            0,
            (int)(root.Attributes & (FileAttributes.ReadOnly
                                      | FileAttributes.Hidden
                                      | FileAttributes.System
                                      | FileAttributes.ReparsePoint
                                      | FileAttributes.Temporary
                                      | FileAttributes.Offline)));
        var rootEntry = Assert.Single(root.EnumerateFileSystemInfos());
        Assert.Equal(fixture.Profile.TempPath, rootEntry.FullName, ignoreCase: true);
        var temp = Assert.IsType<DirectoryInfo>(rootEntry);
        Assert.Equal(0, (int)(temp.Attributes & FileAttributes.ReparsePoint));
        Assert.Empty(temp.EnumerateFileSystemInfos());

        var security = root.GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        Assert.True(security.AreAccessRulesCanonical);
        var parentOwner = new DirectoryInfo(fixture.PackageParentPath)
            .GetAccessControl(AccessControlSections.Owner)
            .GetOwner(typeof(SecurityIdentifier));
        Assert.Equal(parentOwner, security.GetOwner(typeof(SecurityIdentifier)));
        var descriptor = new RawSecurityDescriptor(
            security.GetSecurityDescriptorBinaryForm(),
            0);
        var packageAces = Assert.IsType<RawAcl>(descriptor.DiscretionaryAcl)
            .OfType<CommonAce>()
            .Where(ace => ace.SecurityIdentifier.Equals(fixture.Profile.Sid))
            .ToArray();
        var packageAce = Assert.Single(packageAces);
        Assert.Equal(AceQualifier.AccessAllowed, packageAce.AceQualifier);
        Assert.Equal((int)FileSystemRights.FullControl, packageAce.AccessMask);
        Assert.Equal(CanonicalPackageAceFlags, (byte)packageAce.AceFlags);
        Assert.True(ApplicationDataCodecPackageDataResetter.HasCanonicalLowMandatoryLabel(
            fixture.Profile.LocalAppDataPath));
    }

    private static bool TryCreateDirectoryLink(string linkPath, string targetPath)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                   or IOException
                                   or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool TryCreateFileLink(string linkPath, string targetPath)
    {
        try
        {
            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException
                                   or IOException
                                   or PlatformNotSupportedException)
        {
            return false;
        }
    }

    public sealed class SymbolicLinkFactAttribute : FactAttribute
    {
        public SymbolicLinkFactAttribute()
        {
            if (!CanCreateDirectorySymbolicLink())
                Skip = "Directory symbolic links are unavailable; reparse defense was not exercised.";
        }

        private static bool CanCreateDirectorySymbolicLink()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"ezy-symbolic-link-probe-{Guid.NewGuid():N}");
            var target = Path.Combine(root, "target");
            var link = Path.Combine(root, "link");
            try
            {
                Directory.CreateDirectory(target);
                Directory.CreateSymbolicLink(link, target);
                return true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                                       or IOException
                                       or PlatformNotSupportedException)
            {
                return false;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(link))
                        Directory.Delete(link);
                    if (Directory.Exists(root))
                        Directory.Delete(root, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private sealed class ProfileFixture : IDisposable
    {
        public ProfileFixture(bool createProfile = true)
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                $"ezy-codec-profile-reset-{Guid.NewGuid():N}");
            PackagesRootPath = Path.Combine(DirectoryPath, "Packages");
            PackageParentPath = Path.Combine(PackagesRootPath, PackageFamilyName);
            var profilePath = Path.Combine(PackageParentPath, "AC");
            var tempPath = Path.Combine(profilePath, "Temp");
            OutsidePath = Path.Combine(DirectoryPath, "outside");
            Directory.CreateDirectory(PackagesRootPath);
            Directory.CreateDirectory(OutsidePath);
            if (createProfile)
            {
                Directory.CreateDirectory(tempPath);
                var nested = Directory.CreateDirectory(Path.Combine(profilePath, "nested"));
                File.WriteAllText(Path.Combine(nested.FullName, "state.bin"), "state");
                File.WriteAllText(Path.Combine(tempPath, "temp.bin"), "state");
            }
            Profile = new AppContainerProfileInfo(
                DerivePackageSid(PackageFamilyName),
                profilePath,
                tempPath);
        }

        public string DirectoryPath { get; }
        public string PackagesRootPath { get; }
        public string PackageParentPath { get; }
        public string OutsidePath { get; }
        public AppContainerProfileInfo Profile { get; }

        public byte[] GetPackageParentSecurityDescriptor() =>
            new DirectoryInfo(PackageParentPath)
                .GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
                .GetSecurityDescriptorBinaryForm();

        public ApplicationDataCodecPackageDataResetter CreateResetter(
            Func<string, CancellationToken, Task> clearApplicationDataAsync,
            AppContainerProfileInfo? expectedProfile = null) => new(
                clearApplicationDataAsync,
                packageFamilyName =>
                {
                    if (!string.Equals(
                            packageFamilyName,
                            PackageFamilyName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("Unexpected test package family name.");
                    }
                    return expectedProfile ?? Profile;
                });

        public void Dispose()
        {
            if (Directory.Exists(Profile.LocalAppDataPath))
            {
                var attributes = File.GetAttributes(Profile.LocalAppDataPath);
                if ((attributes & FileAttributes.ReparsePoint) == 0)
                    File.SetAttributes(Profile.LocalAppDataPath, FileAttributes.Normal);
            }
            Directory.Delete(DirectoryPath, recursive: true);
        }

        private static SecurityIdentifier DerivePackageSid(string packageFamilyName)
        {
            var result = IsolationNativeMethods.DeriveAppContainerSidFromAppContainerName(
                packageFamilyName,
                out var sidPointer);
            if (result < 0)
                Marshal.ThrowExceptionForHR(result);
            if (sidPointer == IntPtr.Zero)
                throw new InvalidOperationException("The test package SID is empty.");
            using var sidHandle = new IsolationNativeMethods.SafeSidHandle(sidPointer);
            return new SecurityIdentifier(sidPointer);
        }
    }
}
