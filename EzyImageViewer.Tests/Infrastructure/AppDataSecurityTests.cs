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

    [Fact]
    public void AtomicWriteInFlightInAnotherProcess_DoesNotFailTheProtectionPass()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(_directory, "concurrent");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "settings.json"), "{}");
        AppDataSecurity.EnsureProtected(new AppDataPaths(root));

        // AtomicFileWriter 작업 중 상태 재현.
        // 형제 임시 파일을 마지막 이름 변경까지 FileShare.None으로 잡아 두어 두 번째 인스턴스와 충돌.
        var temp = Path.Combine(root, $".settings.json.{Guid.NewGuid():N}.tmp");
        using (new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            AppDataSecurity.EnsureProtected(new AppDataPaths(root));
        }

        AssertProtectedFile(Path.Combine(root, "settings.json"));
    }

    [Fact]
    public void EntryRemovedDuringTheWalk_DoesNotFailTheProtectionPass()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(_directory, "vanishing");
        Directory.CreateDirectory(root);
        var doomed = Path.Combine(root, "retired.jsonl");
        File.WriteAllText(doomed, "{}");
        AppDataSecurity.EnsureProtected(new AppDataPaths(root));

        // 닫을 때 삭제되는 핸들로 순회가 놓는 순간 항목이 사라지게 함.
        // 로그 보존 정리가 순회 중 파일을 지우는 상황 대역.
        using (new FileStream(
            doomed, FileMode.Open, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose))
        {
        }

        Assert.False(File.Exists(doomed));
        AppDataSecurity.EnsureProtected(new AppDataPaths(root));
    }

    [Fact]
    public void ForeignLockedFile_StillFailsClosed()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(_directory, "foreign-lock");
        Directory.CreateDirectory(root);
        // 원자 쓰기 임시 파일만 건너뜀. 평범한 잠긴 파일은 닫힘 우선으로 실패해야 함.
        var locked = Path.Combine(root, "settings.json");
        using var handle = new FileStream(
            locked, FileMode.CreateNew, FileAccess.Write, FileShare.None);

        Assert.Throws<AppDataProtectionException>(() =>
            AppDataSecurity.EnsureProtected(new AppDataPaths(root)));
    }

    [Fact]
    public void ProtectedAtomicWrite_LeavesAnExplicitAclThatSurvivesTheVerifyPass()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var root = Path.Combine(_directory, "protected-write");
        Directory.CreateDirectory(root);
        AppDataSecurity.EnsureProtected(new AppDataPaths(root));

        var target = Path.Combine(root, "settings.json");
        AtomicFileWriter.Write(target, [1, 2, 3], AtomicFileProtection.CurrentUserAndSystem);

        // 명시적 ACL이 없으면 이름 바꾼 파일이 디렉터리 ACE를 물려받음.
        // 검증 단계는 이를 보호되지 않은 파일로 거부.
        AssertProtectedFile(target);
        AppDataSecurity.EnsureProtected(new AppDataPaths(root));
        AssertProtectedFile(target);
    }

    [Fact]
    public void InheritedAtomicWrite_LeavesTheDirectoryAclInPlaceForUserFacingOutput()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var folder = Directory.CreateDirectory(Path.Combine(_directory, "exports")).FullName;
        var target = Path.Combine(folder, "export.png");

        AtomicFileWriter.Write(target, [1, 2, 3]);

        Assert.False(new FileInfo(target).GetAccessControl(AccessControlSections.Access)
            .AreAccessRulesProtected);
    }

    [Theory]
    [InlineData(".settings.json.0123456789abcdef0123456789abcdef.tmp", true)]
    [InlineData(".a.0123456789ABCDEF0123456789ABCDEF.tmp", true)]
    [InlineData("settings.json.0123456789abcdef0123456789abcdef.tmp", false)]
    [InlineData(".settings.json.tmp", false)]
    [InlineData(".settings.json.short.tmp", false)]
    [InlineData(".settings.json.0123456789abcdef0123456789abcdef.txt", false)]
    [InlineData(".0123456789abcdef0123456789abcdef.tmp", false)]
    public void AtomicTempNames_AreRecognizedOnlyInTheWriterFormat(string fileName, bool expected)
        => Assert.Equal(expected, AtomicFileWriter.IsTempFileName(fileName));

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
