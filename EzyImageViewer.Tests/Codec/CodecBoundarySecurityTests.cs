using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using EzyImageViewer.CodecProtocol;
using EzyImageViewer.Imaging.Codecs.Isolation;
using Xunit;

namespace EzyImageViewer.Tests.Codec;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CodecBoundarySecurityCollection
{
    public const string Name = "Codec boundary security";
}

[Collection(CodecBoundarySecurityCollection.Name)]
public sealed class CodecBoundarySecurityTests
{
    private const int HandleGrowthTolerance = 12;
    private const int RepeatedRunCount = 20;

    [Fact]
    public async Task WallClockDeadline_TerminatesRunningProcessAndDrainsPipes()
    {
        var policy = CreatePolicy() with
        {
            WallClockDeadline = TimeSpan.FromMilliseconds(1_500),
            PerProcessUserTimeLimit = TimeSpan.FromSeconds(5),
        };
        var profile = AppContainerProfileAccess.GetProfileInfo(policy);
        var launcher = new ClassicAppContainerProcessLauncher(TimeProvider.System);
        var execution = launcher.ExecuteAsync(
            CreateCommandRequest(profile, CreateBusyLoop()),
            policy,
            CancellationToken.None);
        var elapsed = Stopwatch.StartNew();

        await Task.Delay(150);
        Assert.False(execution.IsCompleted, "The deadline target was not still running.");
        await Assert.ThrowsAsync<TimeoutException>(() => execution);
        elapsed.Stop();

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(4),
            $"Deadline termination took {elapsed.Elapsed}.");
    }

    [Fact]
    public async Task Cancellation_TerminatesRunningProcessAndDrainsPipes()
    {
        var policy = CreatePolicy() with
        {
            WallClockDeadline = TimeSpan.FromSeconds(5),
            PerProcessUserTimeLimit = TimeSpan.FromSeconds(5),
        };
        var profile = AppContainerProfileAccess.GetProfileInfo(policy);
        using var cancellation = new CancellationTokenSource();
        var launcher = new ClassicAppContainerProcessLauncher(TimeProvider.System);
        var execution = launcher.ExecuteAsync(
            CreateCommandRequest(profile, CreateBusyLoop()),
            policy,
            cancellation.Token);

        try
        {
            await Task.Delay(150);
            Assert.False(execution.IsCompleted, "The cancellation target was not still running.");
            var elapsed = Stopwatch.StartNew();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
            elapsed.Stop();

            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(3),
                $"Cancellation termination took {elapsed.Elapsed}.");
        }
        finally
        {
            cancellation.Cancel();
            try
            {
                await execution;
            }
            catch
            {
            }
        }
    }

    [Fact]
    public async Task PerProcessUserTimeLimit_TerminatesCpuBoundProcessBeforeWallDeadline()
    {
        var policy = CreatePolicy() with
        {
            WallClockDeadline = TimeSpan.FromSeconds(12),
            PerProcessUserTimeLimit = TimeSpan.FromMilliseconds(100),
        };
        var profile = AppContainerProfileAccess.GetProfileInfo(policy);
        var launcher = new ClassicAppContainerProcessLauncher(TimeProvider.System);
        var elapsed = Stopwatch.StartNew();

        var result = await launcher.ExecuteAsync(
            CreateCommandRequest(profile, CreateBusyLoop()),
            policy,
            CancellationToken.None);
        elapsed.Stop();

        Assert.NotEqual(0, result.ExitCode);
        Assert.True(
            elapsed.Elapsed < policy.WallClockDeadline - TimeSpan.FromSeconds(1),
            $"CPU-limit termination took {elapsed.Elapsed} and may have reached the wall deadline.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandardPipeLimit_ExcessOutputFailsClosed(bool standardError)
    {
        var policy = CreatePolicy() with
        {
            MaxStandardOutputBytes = standardError ? 64 * 1024 : 1_024,
            MaxStandardErrorBytes = standardError ? 1_024 : 64 * 1024,
        };
        var profile = AppContainerProfileAccess.GetProfileInfo(policy);
        var redirection = standardError ? " 1>&2" : string.Empty;
        var command =
            $"for /l %i in (1,1,2048) do @echo 0123456789abcdef0123456789abcdef{redirection}";
        var launcher = new ClassicAppContainerProcessLauncher(TimeProvider.System);

        var failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            launcher.ExecuteAsync(
                CreateCommandRequest(profile, command),
                policy,
                CancellationToken.None));

        Assert.Contains("pipe limit", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ActiveProcessLimit_DeniesNestedChildProcess()
    {
        var policy = CreatePolicy();
        var profile = AppContainerProfileAccess.GetProfileInfo(policy);
        var systemDirectory = GetSystemDirectory();
        var childMarkerName = $"codec-child-{Guid.NewGuid():N}.tmp";
        var childMarkerPath = Path.Combine(profile.TempPath, childMarkerName);
        var nestedCommand =
            $"{Path.Combine(systemDirectory, "cmd.exe")} /d /c " +
            $"echo child^>%TEMP%\\{childMarkerName} & " +
            $"if exist %TEMP%\\{childMarkerName} (exit /b 0) else (exit /b 37)";
        var request = CreateCommandRequest(
            profile,
            nestedCommand);
        var launcher = new ClassicAppContainerProcessLauncher(TimeProvider.System);

        try
        {
            var result = await launcher.ExecuteAsync(request, policy, CancellationToken.None);

            Assert.Equal(37, result.ExitCode);
            Assert.False(File.Exists(childMarkerPath));
        }
        finally
        {
            DeleteFileIfPresent(childMarkerPath);
        }
    }

    [Fact]
    public async Task AppContainer_DeniesWriteOutsideItsPrivateDataDirectory()
    {
        var outsidePath = Path.Combine(
            AppContext.BaseDirectory,
            $".codec-boundary-write-{Guid.NewGuid():N}.tmp");
        var policy = CreatePolicy();
        var profile = AppContainerProfileAccess.GetProfileInfo(policy);
        var request = CreateCommandRequest(
            profile,
            $"(echo codec-boundary)>\"{outsidePath}\"");
        var launcher = new ClassicAppContainerProcessLauncher(TimeProvider.System);

        try
        {
            var result = await launcher.ExecuteAsync(
                request,
                policy,
                CancellationToken.None);

            Assert.NotEqual(0, result.ExitCode);
            Assert.False(File.Exists(outsidePath));
        }
        finally
        {
            DeleteFileIfPresent(outsidePath);
        }
    }

    [Fact]
    public async Task ZeroCapabilityAppContainer_CannotReachListeningLoopbackEndpoint()
    {
        var policy = CreatePolicy() with { WallClockDeadline = TimeSpan.FromSeconds(4) };
        var profile = AppContainerProfileAccess.GetProfileInfo(policy);
        var curlPath = Path.Combine(GetSystemDirectory(), "curl.exe");
        Assert.True(File.Exists(curlPath), $"Windows curl was not found at '{curlPath}'.");

        using var listener = new TcpListener(IPAddress.Loopback, port: 0);
        listener.Start(backlog: 1);
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        await ProveLoopbackListenerReachableAsync(listener, endpoint.Port);
        using var serverCancellation = new CancellationTokenSource();
        var reached = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var serverTask = ServeOneRequestAsync(listener, reached, serverCancellation.Token);
        var request = new IsolatedCodecProcessRequest(
            curlPath,
            GetSystemDirectory(),
            Arguments:
            [
                "--silent",
                "--show-error",
                "--max-time",
                "2",
                $"http://127.0.0.1:{endpoint.Port}/",
            ],
            Environment: CreateMinimalEnvironment(GetSystemDirectory(), profile),
            StandardInput: ReadOnlyMemory<byte>.Empty);
        var launcher = new ClassicAppContainerProcessLauncher(TimeProvider.System);

        try
        {
            var result = await launcher.ExecuteAsync(request, policy, CancellationToken.None);
            await Task.Delay(250);

            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains(
                "curl:",
                Encoding.UTF8.GetString(result.StandardError.Content.Span),
                StringComparison.OrdinalIgnoreCase);
            Assert.False(reached.Task.IsCompletedSuccessfully);
        }
        finally
        {
            serverCancellation.Cancel();
            listener.Stop();
            try
            {
                await serverTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (SocketException) when (serverCancellation.IsCancellationRequested)
            {
            }
        }
    }

    [Fact]
    public async Task RepeatedRuns_DoNotAccumulateProcessOrPipeHandles()
    {
        var policy = CreatePolicy();
        var profile = AppContainerProfileAccess.GetProfileInfo(policy);
        var request = CreateCommandRequest(profile, "exit 0");
        var launcher = new ClassicAppContainerProcessLauncher(TimeProvider.System);

        for (var index = 0; index < 5; index++)
            Assert.Equal(0, (await launcher.ExecuteAsync(request, policy, CancellationToken.None)).ExitCode);

        ForceFinalizers();
        var handlesBefore = GetCurrentProcessHandleCount();

        for (var index = 0; index < RepeatedRunCount; index++)
            Assert.Equal(0, (await launcher.ExecuteAsync(request, policy, CancellationToken.None)).ExitCode);

        ForceFinalizers();
        var handlesAfter = GetCurrentProcessHandleCount();

        Assert.True(
            handlesAfter <= handlesBefore + HandleGrowthTolerance,
            $"Handle count grew from {handlesBefore} to {handlesAfter} across " +
            $"{RepeatedRunCount} isolated launches.");
    }

    [ReleaseCodecHostFact]
    [Trait("Category", "ReleaseCodecHost")]
    public async Task ReleaseHost_ExcludesDiagnosticProcessorAndRejectsDiagnosticOperation()
    {
        var assemblyPath = Path.Combine(
            FindRepositoryRoot(),
            "EzyImageViewer.CodecHost",
            "bin",
            "Release",
            "net10.0",
            "win-x64",
            "EzyImageViewer.CodecHost.dll");
        Assert.True(File.Exists(assemblyPath), $"Release CodecHost was not found at '{assemblyPath}'.");

        var typeNames = ReadTypeNames(assemblyPath);
        Assert.DoesNotContain(
            "EzyImageViewer.CodecHost.DiagnosticOperationProcessor",
            typeNames);

        var request = CodecHostTestClient.Request(
            CodecOperation.DiagnosticEcho,
            CodecFormat.None,
            inputLength: 0);
        var result = await CodecHostTestClient.RunAsync(request, []);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.Response);
        Assert.Equal(CodecResultCode.UnsupportedOperation, result.Response.Result);
        Assert.Equal("diagnostics-disabled", result.Response.Diagnostic);
        Assert.Empty(result.Payload);
        Assert.Empty(result.StandardError);
    }

    private static IsolatedCodecProcessPolicy CreatePolicy() => new(
        AppContainerName: "koprodev.ezyimageviewer.codec.boundary.tests.v1",
        ProfileSource: AppContainerProfileSource.Classic,
        AppContainerDisplayName: "ezyImageViewer Codec Boundary Tests",
        AppContainerDescription: "Zero-capability profile for isolated codec boundary tests.",
        Capabilities: AppContainerCapabilities.None,
        WallClockDeadline: TimeSpan.FromSeconds(5),
        PerProcessUserTimeLimit: TimeSpan.FromSeconds(4),
        ProcessMemoryLimitBytes: 256L * 1024 * 1024,
        MaxStandardInputBytes: 64 * 1024,
        MaxStandardOutputBytes: 64 * 1024,
        MaxStandardErrorBytes: 64 * 1024,
        ForcedTerminationExitCode: 0xE000_0021);

    private static IsolatedCodecProcessRequest CreateCommandRequest(
        AppContainerProfileInfo profile,
        string command)
    {
        var systemDirectory = GetSystemDirectory();
        return new IsolatedCodecProcessRequest(
            Path.Combine(systemDirectory, "cmd.exe"),
            systemDirectory,
            Arguments: ["/d", "/c", command],
            CreateMinimalEnvironment(systemDirectory, profile),
            ReadOnlyMemory<byte>.Empty);
    }

    private static IReadOnlyDictionary<string, string> CreateMinimalEnvironment(
        string executableDirectory,
        AppContainerProfileInfo profile)
    {
        var systemRoot = RequiredEnvironment("SystemRoot");
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = systemRoot,
            ["WINDIR"] = systemRoot,
            ["PATH"] = string.Join(
                Path.PathSeparator,
                executableDirectory,
                Path.Combine(systemRoot, "System32")),
            ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
            ["ComSpec"] = Path.Combine(systemRoot, "System32", "cmd.exe"),
            ["LOCALAPPDATA"] = profile.LocalAppDataPath,
            ["TEMP"] = profile.TempPath,
            ["TMP"] = profile.TempPath,
        };
    }

    private static string CreateBusyLoop() =>
        "for /l %i in (1,1,2147483647) do @rem";

    private static async Task ProveLoopbackListenerReachableAsync(
        TcpListener listener,
        int port)
    {
        var acceptTask = listener.AcceptTcpClientAsync();
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, port);
        using var accepted = await acceptTask;
        Assert.True(client.Connected);
        Assert.True(accepted.Connected);
    }

    private static async Task ServeOneRequestAsync(
        TcpListener listener,
        TaskCompletionSource reached,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        reached.TrySetResult();
        await using var stream = client.GetStream();
        await stream.WriteAsync(
            "HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n"u8.ToArray(),
            cancellationToken);
    }

    private static IReadOnlyList<string> ReadTypeNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        var names = new List<string>();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var definition = metadata.GetTypeDefinition(handle);
            var name = metadata.GetString(definition.Name);
            var typeNamespace = metadata.GetString(definition.Namespace);
            names.Add(string.IsNullOrEmpty(typeNamespace) ? name : $"{typeNamespace}.{name}");
        }
        return names;
    }

    private static int GetCurrentProcessHandleCount()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.HandleCount;
    }

    private static void ForceFinalizers()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "EzyImageViewer.slnx")))
                return directory.FullName;
        }
        throw new DirectoryNotFoundException(
            "Repository root was not found from the test output directory.");
    }

    private static string GetSystemDirectory() =>
        Path.Combine(RequiredEnvironment("SystemRoot"), "System32");

    private static string RequiredEnvironment(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is unavailable.");

    private static void DeleteFileIfPresent(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }

    public sealed class ReleaseCodecHostFactAttribute : FactAttribute
    {
        public ReleaseCodecHostFactAttribute()
        {
            if (!AppContext.BaseDirectory.Contains(
                    $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            {
                Skip = "Run this gate with dotnet test -c Release.";
            }
        }
    }
}
