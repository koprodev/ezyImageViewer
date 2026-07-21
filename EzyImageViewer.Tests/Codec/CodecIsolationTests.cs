using EzyImageViewer.CodecProtocol;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging.Codecs;
using EzyImageViewer.Imaging.Codecs.Isolation;
using Microsoft.Win32.SafeHandles;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Xunit;

namespace EzyImageViewer.Tests.Codec;

public sealed partial class CodecIsolationTests
{
    private const string PackagedSmokeEnvironmentVariable = "EIV_RUN_PACKAGED_CODEC_SMOKE";
    private static readonly CodecProtocolLimits ProtocolLimits = new(
        InputLimits.Default.MaxFileBytes,
        InputLimits.Default.DisplayByteBudget / 2,
        maxDiagnosticBytes: 1024,
        InputLimits.Default.MaxDimension,
        InputLimits.Default.MaxFrameCount,
        InputLimits.Default.HardMaxPixels);

    [Fact]
    public void Policy_RejectsMissingResourceBudgets()
    {
        var valid = CreatePolicy();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (valid with { WallClockDeadline = TimeSpan.Zero }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (valid with { PerProcessUserTimeLimit = TimeSpan.Zero }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (valid with { ProcessMemoryLimitBytes = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (valid with { MaxStandardInputBytes = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (valid with { MaxStandardOutputBytes = 0 }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            (valid with { ProfileSource = (AppContainerProfileSource)int.MaxValue }).Validate());
    }

    [Fact]
    public async Task BoundedPipeReader_RetainsGrowableBackingWithoutFinalPayloadCopy()
    {
        const int maximumBytes = 100_000;
        const int envelopeLength = 89;
        var expected = Enumerable.Range(0, 70_001)
            .Select(static value => (byte)(value % 251))
            .ToArray();
        await using var source = new MemoryStream(expected, writable: false);

        var captured = await BoundedPipeReader.ReadAsync(source, maximumBytes);

        Assert.Equal(expected.Length, captured.Length);
        Assert.Equal(maximumBytes, captured.Buffer.Length);
        Assert.Equal(
            64L * 1024 + maximumBytes,
            BoundedPipeReader.CalculateMaximumAllocationDuringGrowth(maximumBytes));
        Assert.True(expected.AsSpan().SequenceEqual(captured.Content.Span));
        await using (var readable = captured.OpenReadStream())
            Assert.Same(captured.Buffer, readable.GetBuffer());

        var originalBacking = captured.Buffer;
        var retained = captured.RetainSliceInPlace(
            envelopeLength,
            expected.Length - envelopeLength);

        Assert.Same(originalBacking, retained);
        Assert.Equal(expected.Length - envelopeLength, captured.Length);
        Assert.True(expected.AsSpan(envelopeLength).SequenceEqual(captured.Content.Span));
    }

    [Fact]
    public void ExistingPackageIdentity_RejectsClassicProfileAclMutation()
    {
        var policy = CreatePolicy() with
        {
            ProfileSource = AppContainerProfileSource.ExistingPackage,
        };

        Assert.Throws<InvalidOperationException>(() =>
            AppContainerProfileAccess.EnsureClassicProfileReadAndExecute(
                policy,
                AppContext.BaseDirectory));
    }

    [Fact]
    public async Task NativeSystemProcess_RunsInsideZeroCapabilityBoundary()
    {
        var systemRoot = RequiredEnvironment("SystemRoot");
        var systemDirectory = Path.Combine(systemRoot, "System32");
        var policy = CreatePolicy() with { Capabilities = AppContainerCapabilities.None };
        var request = new IsolatedCodecProcessRequest(
            Path.Combine(systemDirectory, "cmd.exe"),
            systemDirectory,
            Arguments: ["/d", "/c", "exit", "0"],
            Environment: CreateMinimalEnvironment(
                systemDirectory,
                AppContainerProfileAccess.GetProfileInfo(policy)),
            StandardInput: ReadOnlyMemory<byte>.Empty);
        var launcher = new ClassicAppContainerProcessLauncher(TimeProvider.System);

        var result = await launcher.ExecuteAsync(
            request,
            policy,
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.StandardOutput.IsEmpty);
        Assert.True(result.StandardError.IsEmpty);
    }

    [Fact]
    public async Task StandardInput_RejectsConfiguredLimitBeforeLaunch()
    {
        var systemDirectory = Path.Combine(RequiredEnvironment("SystemRoot"), "System32");
        var request = new IsolatedCodecProcessRequest(
            Path.Combine(systemDirectory, "cmd.exe"),
            systemDirectory,
            Arguments: ["/d", "/c", "exit", "0"],
            Environment: new Dictionary<string, string>(),
            StandardInput: new byte[2]);
        var launcher = new ClassicAppContainerProcessLauncher(TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentException>(() => launcher.ExecuteAsync(
            request,
            CreatePolicy() with { MaxStandardInputBytes = 1 },
            CancellationToken.None));
    }

    [Fact]
    public async Task Environment_RejectsCaseInsensitiveDuplicateNames()
    {
        var systemDirectory = Path.Combine(RequiredEnvironment("SystemRoot"), "System32");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["PATH"] = systemDirectory,
            ["Path"] = systemDirectory,
        };
        var request = new IsolatedCodecProcessRequest(
            Path.Combine(systemDirectory, "cmd.exe"),
            systemDirectory,
            Arguments: ["/d", "/c", "exit", "0"],
            Environment: environment,
            StandardInput: ReadOnlyMemory<byte>.Empty);
        var launcher = new ClassicAppContainerProcessLauncher(TimeProvider.System);

        var failure = await Assert.ThrowsAsync<ArgumentException>(() => launcher.ExecuteAsync(
            request,
            CreatePolicy(),
            CancellationToken.None));
        Assert.Equal("environment", failure.ParamName);
    }

    [Fact]
    public async Task InheritedSource_UsesInheritableReadOnlyDuplicate()
    {
        var sourcePath = Path.Combine(
            Path.GetTempPath(),
            $"ezy-codec-handle-{Guid.NewGuid():N}.bin");
        var expected = "inherited-source"u8.ToArray();
        await File.WriteAllBytesAsync(sourcePath, expected);
        try
        {
            using var sourceHandle = File.OpenHandle(
                sourcePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Delete,
                FileOptions.RandomAccess);
            nint inheritedHandleValue = 0;
            var systemDirectory = Path.Combine(RequiredEnvironment("SystemRoot"), "System32");
            var policy = CreatePolicy() with { Capabilities = AppContainerCapabilities.None };
            var request = new IsolatedCodecProcessRequest(
                Path.Combine(systemDirectory, "cmd.exe"),
                systemDirectory,
                Arguments: ["/d", "/c", "exit", "0"],
                Environment: CreateMinimalEnvironment(
                    systemDirectory,
                    AppContainerProfileAccess.GetProfileInfo(policy)),
                StandardInput: ReadOnlyMemory<byte>.Empty,
                InheritedSource: new InheritedReadOnlySource(
                    sourceHandle,
                    (childHandle, _) =>
                    {
                        inheritedHandleValue = childHandle;
                        Assert.NotEqual(nint.Zero, childHandle);
                        Assert.NotEqual(sourceHandle.DangerousGetHandle(), childHandle);
                        Assert.True(IsolationNativeMethods.GetHandleInformation(
                            childHandle,
                            out var flags));
                        Assert.NotEqual(0u, flags & IsolationNativeMethods.HandleFlagInherit);

                        using var borrowedHandle = new SafeFileHandle(
                            childHandle,
                            ownsHandle: false);
                        var actual = new byte[expected.Length];
                        Assert.Equal(
                            expected.Length,
                            RandomAccess.Read(borrowedHandle, actual, fileOffset: 0));
                        Assert.Equal(expected, actual);
                        Assert.Throws<UnauthorizedAccessException>(() =>
                            RandomAccess.Write(borrowedHandle, [0xFF], fileOffset: 0));
                        return ValueTask.FromResult<ReadOnlyMemory<byte>>(
                            ReadOnlyMemory<byte>.Empty);
                    }));
            var launcher = new ClassicAppContainerProcessLauncher(TimeProvider.System);

            var result = await launcher.ExecuteAsync(
                request,
                policy,
                CancellationToken.None);

            Assert.NotEqual(nint.Zero, inheritedHandleValue);
            Assert.Equal(0, result.ExitCode);
            Assert.True(result.StandardOutput.IsEmpty);
            Assert.True(result.StandardError.IsEmpty);
            var unchanged = new byte[expected.Length];
            Assert.Equal(
                expected.Length,
                RandomAccess.Read(sourceHandle, unchanged, fileOffset: 0));
            Assert.Equal(expected, unchanged);
        }
        finally
        {
            File.Delete(sourcePath);
        }
    }

    [PackagedCodecSmokeFact]
    [Trait("Category", "PackagedCodecSmoke")]
    public async Task Probe_RunsWithInstalledCodecHostFrameworkIdentity()
    {
        var package = FindCodecHostFrameworkPackage();
        var hostDirectory = package.InstalledPath;
        var hostPath = Path.Combine(hostDirectory, "EzyImageViewer.CodecHost.exe");
        Assert.True(File.Exists(hostPath), $"CodecHost executable was not found at '{hostPath}'.");
        var policy = CreatePolicy() with
        {
            AppContainerName = package.Id.FamilyName,
            ProfileSource = AppContainerProfileSource.ExistingPackage,
            AppContainerDisplayName = "ezy Image Viewer CodecHost",
            AppContainerDescription = "Installed CodecHost framework package identity.",
        };
        var profile = AppContainerProfileAccess.GetProfileInfo(policy);
        var request = new IsolatedCodecProcessRequest(
            hostPath,
            hostDirectory,
            Arguments: [],
            Environment: CreateMinimalEnvironment(hostDirectory, profile),
            StandardInput: await CreateProbeRequestAsync());
        var launcher = new ClassicAppContainerProcessLauncher(TimeProvider.System);

        var result = await launcher.ExecuteAsync(request, policy, CancellationToken.None);

        Assert.True(
            result.ExitCode == 0,
            $"Exit 0x{result.ExitCode:X8}: " +
            System.Text.Encoding.UTF8.GetString(result.StandardError.Content.Span));
        Assert.True(result.StandardError.IsEmpty);
        await using var wire = result.StandardOutput.OpenReadStream();
        var response = await CodecWireProtocol.ReadResponseAsync(
            wire, ProtocolLimits, CancellationToken.None);
        Assert.Equal(CodecResultCode.Success, response.Result);
        Assert.Equal(CodecOperation.Probe, response.Operation);
        await using var payload = new MemoryStream();
        await CodecWireProtocol.CopyResponsePayloadAsync(
            wire, payload, response, ProtocolLimits, CancellationToken.None);
        Assert.Equal("ezy-codec-host-b1"u8.ToArray(), payload.ToArray());
    }

    [PackagedCodecSmokeFact]
    [Trait("Category", "PackagedCodecSmoke")]
    public async Task ProcessMemoryLimit_FailsClosedAfterInstalledHostPositiveControl()
    {
        var package = FindCodecHostFrameworkPackage();
        var hostDirectory = package.InstalledPath;
        var hostPath = Path.Combine(hostDirectory, "EzyImageViewer.CodecHost.exe");
        Assert.True(File.Exists(hostPath), $"CodecHost executable was not found at '{hostPath}'.");
        var positivePolicy = CreatePolicy() with
        {
            AppContainerName = package.Id.FamilyName,
            ProfileSource = AppContainerProfileSource.ExistingPackage,
            AppContainerDisplayName = "ezy Image Viewer CodecHost",
            AppContainerDescription = "Installed CodecHost framework package identity.",
        };
        var profile = AppContainerProfileAccess.GetProfileInfo(positivePolicy);
        var request = new IsolatedCodecProcessRequest(
            hostPath,
            hostDirectory,
            Arguments: [],
            Environment: CreateMinimalEnvironment(hostDirectory, profile),
            StandardInput: await CreateProbeRequestAsync());
        var launcher = new ClassicAppContainerProcessLauncher(TimeProvider.System);

        var positive = await launcher.ExecuteAsync(
            request,
            positivePolicy,
            CancellationToken.None);
        Assert.Equal(0, positive.ExitCode);
        Assert.False(positive.StandardOutput.IsEmpty);
        Assert.True(positive.StandardError.IsEmpty);

        var constrainedPolicy = positivePolicy with
        {
            WallClockDeadline = TimeSpan.FromSeconds(10),
            ProcessMemoryLimitBytes = 1L * 1024 * 1024,
        };
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        IsolatedCodecProcessResult? constrained = null;
        IOException? pipeFailure = null;
        try
        {
            constrained = await launcher.ExecuteAsync(
                request,
                constrainedPolicy,
                CancellationToken.None);
        }
        catch (IOException ex)
        {
            pipeFailure = ex;
        }
        elapsed.Stop();

        Assert.True(
            pipeFailure is not null || constrained is { ExitCode: not 0 },
            "The installed CodecHost did not fail closed under the 1 MiB process-memory limit.");
        Assert.True(
            elapsed.Elapsed < constrainedPolicy.WallClockDeadline - TimeSpan.FromSeconds(1),
            $"Memory-limit failure took {elapsed.Elapsed} and may have reached the wall deadline.");
    }

    private static IsolatedCodecProcessPolicy CreatePolicy() => new(
        AppContainerName: "koprodev.ezyimageviewer.codechost.codegen.tests.v1",
        ProfileSource: AppContainerProfileSource.Classic,
        AppContainerDisplayName: "ezyImageViewer CodecHost Tests",
        AppContainerDescription: "Code-generation-only test profile for the isolated codec boundary.",
        Capabilities: AppContainerCapabilities.CodeGeneration,
        WallClockDeadline: TimeSpan.FromSeconds(15),
        PerProcessUserTimeLimit: TimeSpan.FromSeconds(10),
        ProcessMemoryLimitBytes: 1024L * 1024 * 1024,
        MaxStandardInputBytes: 64 * 1024,
        MaxStandardOutputBytes: 64 * 1024,
        MaxStandardErrorBytes: 4 * 1024,
        ForcedTerminationExitCode: 0xE000_0001);

    private static async Task<byte[]> CreateProbeRequestAsync()
    {
        var request = new CodecRequest(
            Guid.NewGuid(),
            Nonce: 1,
            CodecOperation.Probe,
            CodecFormat.None,
            CodecInputTransport.Inline,
            InputLength: 0,
            InputHandle: 0,
            PageIndex: -1,
            TargetWidth: 0,
            TargetHeight: 0);
        await using var wire = new MemoryStream();
        await CodecWireProtocol.WriteRequestAsync(
            wire, request, inlineInput: null, ProtocolLimits, CancellationToken.None);
        return wire.ToArray();
    }

    private static Package FindCodecHostFrameworkPackage()
    {
        var package = new PackageManager()
            .FindPackagesForUserWithPackageTypes(
                string.Empty,
                PackageTypes.Framework)
            .Where(package => string.Equals(
                package.Id.Name,
                IsolatedCodecHostConfiguration.PackageName,
                StringComparison.Ordinal))
            .OrderByDescending(static candidate => new Version(
                candidate.Id.Version.Major,
                candidate.Id.Version.Minor,
                candidate.Id.Version.Build,
                candidate.Id.Version.Revision))
            .FirstOrDefault();
        return package ?? throw new InvalidOperationException(
            $"Framework package '{IsolatedCodecHostConfiguration.PackageName}' " +
            "is not installed for the current user.");
    }

    private static IReadOnlyDictionary<string, string> CreateMinimalEnvironment(
        string hostDirectory,
        AppContainerProfileInfo profile)
    {
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot")
            ?? throw new InvalidOperationException("SystemRoot is unavailable.");
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = systemRoot,
            ["WINDIR"] = systemRoot,
            ["PATH"] = string.Join(Path.PathSeparator, hostDirectory, Path.Combine(systemRoot, "System32")),
            ["LOCALAPPDATA"] = profile.LocalAppDataPath,
            ["USERPROFILE"] = RequiredEnvironment("USERPROFILE"),
            ["TEMP"] = profile.TempPath,
            ["TMP"] = profile.TempPath,
            ["APPDATA"] = profile.LocalAppDataPath,
            ["ProgramData"] = RequiredEnvironment("ProgramData"),
            ["ProgramFiles"] = RequiredEnvironment("ProgramFiles"),
            ["CommonProgramFiles"] = RequiredEnvironment("CommonProgramFiles"),
            ["ComSpec"] = Path.Combine(systemRoot, "System32", "cmd.exe"),
            ["HOMEDRIVE"] = RequiredEnvironment("HOMEDRIVE"),
            ["HOMEPATH"] = RequiredEnvironment("HOMEPATH"),
            ["OS"] = "Windows_NT",
            ["PROCESSOR_ARCHITECTURE"] = RequiredEnvironment("PROCESSOR_ARCHITECTURE"),
            ["NUMBER_OF_PROCESSORS"] = RequiredEnvironment("NUMBER_OF_PROCESSORS"),
            ["DOTNET_EnableDiagnostics"] = "0",
            ["COMPlus_EnableDiagnostics"] = "0",
            ["CORECLR_ENABLE_PROFILING"] = "0",
            ["COR_ENABLE_PROFILING"] = "0",
            ["DOTNET_SYSTEM_GLOBALIZATION_INVARIANT"] = "1",
        };
        if (Environment.GetEnvironmentVariable("EIV_COREHOST_TRACE") == "1")
        {
            environment["COREHOST_TRACE"] = "1";
            environment["COREHOST_TRACEFILE"] = Path.Combine(
                profile.TempPath,
                "ezy-codec-corehost-trace.log");
        }
        return environment;
    }

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is unavailable.");

    public sealed class PackagedCodecSmokeFactAttribute : FactAttribute
    {
        public PackagedCodecSmokeFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(PackagedSmokeEnvironmentVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = $"Set {PackagedSmokeEnvironmentVariable}=1 to run the installed-package gate.";
            }
        }
    }
}
