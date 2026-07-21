using System.Runtime.Versioning;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class AppDataSecurityTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ezy-appdata-security-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExistingBroadTree_IsMigratedAndNewChildrenInheritOnlyTrustedPrincipals()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(_directory, "data");
        var nested = Directory.CreateDirectory(Path.Combine(root, "recovery")).FullName;
        var existingFile = Path.Combine(nested, "checkpoint.recovery");
        File.WriteAllBytes(existingFile, [1, 2, 3]);
        ApplyBroadDirectoryAcl(root);
        ApplyBroadDirectoryAcl(nested);
        ApplyBroadFileAcl(existingFile);

        AppDataSecurity.EnsureProtected(new AppDataPaths(root));

        AssertProtectedDirectory(root);
        AssertProtectedDirectory(nested);
        AssertProtectedFile(existingFile);
        var futureDirectory = Directory.CreateDirectory(Path.Combine(root, "logs")).FullName;
        var futureFile = Path.Combine(futureDirectory, "current.jsonl");
        File.WriteAllText(futureFile, "{}");
        AssertOnlyTrustedEffectiveRules(new DirectoryInfo(futureDirectory).GetAccessControl());
        AssertOnlyTrustedEffectiveRules(new FileInfo(futureFile).GetAccessControl());
    }

    [Fact]
    public void MissingRoot_IsCreatedWithProtectedTrustedAcl()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(_directory, "new-data");

        AppDataSecurity.EnsureProtected(new AppDataPaths(root));

        AssertProtectedDirectory(root);
    }

    [Fact]
    public void RootThatCannotBeCreatedAsDirectory_FailsClosed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        Directory.CreateDirectory(_directory);
        var root = Path.Combine(_directory, "not-a-directory");
        File.WriteAllBytes(root, [1]);

        Assert.Throws<AppDataProtectionException>(() =>
            AppDataSecurity.EnsureProtected(new AppDataPaths(root)));
    }

    [Fact]
    public void HardLinkedFileInExistingTree_FailsClosedBeforeAclMigration()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(_directory, "app-data");
        Directory.CreateDirectory(root);
        var external = Path.Combine(_directory, "external.txt");
        var linked = Path.Combine(root, "linked.txt");
        File.WriteAllText(external, "external");
        if (!CreateHardLink(linked, external, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());

        Assert.Throws<AppDataProtectionException>(() =>
            AppDataSecurity.EnsureProtected(new AppDataPaths(root)));
        Assert.Equal("external", File.ReadAllText(external));
    }

    [Theory]
    [InlineData(unchecked((int)0x80070020), true)]
    [InlineData(unchecked((int)0x80070021), true)]
    [InlineData(unchecked((int)0x80070005), false)]
    public void TransientProtectionRetry_OnlyClassifiesSharingAndLockViolations(
        int hresult,
        bool expected)
    {
        var classifier = typeof(AppDataSecurity).GetMethod(
            "IsTransientSharingViolation",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Sharing-violation classifier is missing.");

        var actual = (bool)classifier.Invoke(
            null,
            [new IOException("Injected protection failure.", hresult)])!;

        Assert.Equal(expected, actual);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyBroadDirectoryAcl(string path)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var user = identity.User!;
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        const InheritanceFlags inheritance =
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            everyone,
            FileSystemRights.ReadAndExecute,
            inheritance,
            PropagationFlags.None,
            AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyBroadFileAcl(string path)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var user = identity.User!;
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(user);
        security.AddAccessRule(new FileSystemAccessRule(
            user, FileSystemRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            everyone, FileSystemRights.Read, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertProtectedDirectory(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        Assert.True(security.AreAccessRulesProtected);
        AssertOnlyTrustedEffectiveRules(security);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertProtectedFile(string path)
    {
        var security = new FileInfo(path).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner);
        Assert.True(security.AreAccessRulesProtected);
        AssertOnlyTrustedEffectiveRules(security);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertOnlyTrustedEffectiveRules(FileSystemSecurity security)
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        var user = identity.User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var allowed = new HashSet<SecurityIdentifier> { user, system };
        var rules = security.GetAccessRules(
            includeExplicit: true,
            includeInherited: true,
            typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();

        Assert.NotEmpty(rules);
        Assert.All(rules, rule =>
        {
            Assert.Contains((SecurityIdentifier)rule.IdentityReference, allowed);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        });
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);
}
