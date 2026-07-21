using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using EzyImageViewer.CodecProtocol;
using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging;
using EzyImageViewer.Imaging.Codecs;
using EzyImageViewer.Imaging.Codecs.Isolation;
using EzyImageViewer.Imaging.Skia;
using EzyImageViewer.Imaging.Svg;
using EzyImageViewer.Imaging.Wic;
using Windows.ApplicationModel;
using Windows.Management.Deployment;
using Xunit;

namespace EzyImageViewer.Tests.Codec;

public sealed partial class CodecIsolationTests
{
    [PackagedCodecSmokeFact]
    [Trait("Category", "PackagedCodecSmoke")]
    public async Task InstalledMainPackage_ExplicitCodecSmokeResolvesFrameworkDependency()
    {
        var mainPackage = FindInstalledMainPackage();
        var frameworkPackage = Assert.Single(mainPackage.Dependencies, package =>
            string.Equals(
                package.Id.Name,
                IsolatedCodecHostConfiguration.PackageName,
                StringComparison.Ordinal));
        Assert.True(frameworkPackage.IsFramework);
        var profile = AppContainerProfileAccess.GetExistingPackageProfileInfo(
            frameworkPackage.Id.FamilyName);
        var executable = Path.Combine(mainPackage.InstalledPath, "ezyImageViewer.exe");
        Assert.True(File.Exists(executable), $"Installed app executable was not found at '{executable}'.");
        var appUserModelId = $"{mainPackage.Id.FamilyName}!App";
        var frameworkVersion = FormatPackageVersion(frameworkPackage.Id.Version);
        var directory = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(),
            $"ezy-installed-main-codec-{Guid.NewGuid():N}"));

        try
        {
            var pdfPath = Path.Combine(directory.FullName, "three-pages.pdf");
            var psdPath = Path.Combine(directory.FullName, "composite.psd");
            await File.WriteAllBytesAsync(
                pdfPath,
                CodecSyntheticDocumentFactory.BuildPdf(pageCount: 3));
            await File.WriteAllBytesAsync(
                psdPath,
                CodecSyntheticDocumentFactory.BuildRgbPsd(width: 4, height: 3));

            await RunInstalledAppCodecSmokeAsync(
                appUserModelId,
                pdfPath,
                Path.Combine(directory.FullName, "pdf-result.json"),
                expectedFormat: "Pdf",
                expectedFrameCount: 3,
                expectedSequenceKind: "Pages",
                expectedWidth: null,
                expectedHeight: null,
                frameworkVersion,
                frameworkPackage.Id.FamilyName,
                profile);
            await RunInstalledAppCodecSmokeAsync(
                appUserModelId,
                psdPath,
                Path.Combine(directory.FullName, "psd-result.json"),
                expectedFormat: "Psd",
                expectedFrameCount: 1,
                expectedSequenceKind: "SingleFrame",
                expectedWidth: 4,
                expectedHeight: 3,
                frameworkVersion,
                frameworkPackage.Id.FamilyName,
                profile);
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

    [PackagedCodecSmokeFact]
    [Trait("Category", "PackagedCodecSmoke")]
    public async Task RecreatedProfile_ActualTokenAllowsOnlyTempAndDeniesExternalWriteAndLoopback()
    {
        var package = FindCodecHostFrameworkPackage();
        var profile = AppContainerProfileAccess.GetExistingPackageProfileInfo(
            package.Id.FamilyName);
        var resetter = new ApplicationDataCodecPackageDataResetter();
        await resetter.ClearAsync(
            package.Id.FamilyName,
            profile,
            CancellationToken.None);
        AssertProfileDataEmpty(profile);
        var markerPath = Path.Combine(
            profile.TempPath,
            $"appcontainer-write-{Guid.NewGuid():N}.marker");
        var outsidePath = Path.Combine(
            AppContext.BaseDirectory,
            $".installed-codec-boundary-write-{Guid.NewGuid():N}.tmp");
        var systemDirectory = Path.Combine(RequiredEnvironment("SystemRoot"), "System32");
        var policy = CreatePolicy() with
        {
            AppContainerName = package.Id.FamilyName,
            ProfileSource = AppContainerProfileSource.ExistingPackage,
            AppContainerDisplayName = "ezy Image Viewer CodecHost",
            AppContainerDescription = "Installed CodecHost framework package identity.",
        };
        var request = new IsolatedCodecProcessRequest(
            Path.Combine(systemDirectory, "cmd.exe"),
            systemDirectory,
            Arguments: ["/d", "/c", $"echo ezy-codec-profile-write>{markerPath}"],
            Environment: CreateMinimalEnvironment(package.InstalledPath, profile),
            StandardInput: ReadOnlyMemory<byte>.Empty);
        var launcher = new ClassicAppContainerProcessLauncher(TimeProvider.System);

        try
        {
            var result = await launcher.ExecuteAsync(
                request,
                policy,
                CancellationToken.None);

            Assert.Equal(0, result.ExitCode);
            Assert.True(result.StandardOutput.IsEmpty);
            Assert.True(result.StandardError.IsEmpty);
            Assert.Equal(
                "ezy-codec-profile-write",
                (await File.ReadAllTextAsync(markerPath)).Trim());

            var outsideResult = await launcher.ExecuteAsync(
                request with
                {
                    Arguments = ["/d", "/c", string.Concat("(echo codec-boundary)>", outsidePath)],
                },
                policy,
                CancellationToken.None);
            Assert.NotEqual(0, outsideResult.ExitCode);
            Assert.False(File.Exists(outsidePath));

            var curlPath = Path.Combine(systemDirectory, "curl.exe");
            Assert.True(File.Exists(curlPath), $"Windows curl was not found at '{curlPath}'.");
            using var listener = new TcpListener(IPAddress.Loopback, port: 0);
            listener.Start(backlog: 1);
            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            await ProveLoopbackListenerReachableAsync(listener, endpoint.Port);
            using var serverCancellation = new CancellationTokenSource();
            var reached = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var serverTask = ServeOneRequestAsync(listener, reached, serverCancellation.Token);
            try
            {
                var networkResult = await launcher.ExecuteAsync(
                    request with
                    {
                        ExecutablePath = curlPath,
                        Arguments =
                        [
                            "--silent",
                            "--show-error",
                            "--max-time",
                            "2",
                            $"http://127.0.0.1:{endpoint.Port}/",
                        ],
                    },
                    policy with { WallClockDeadline = TimeSpan.FromSeconds(4) },
                    CancellationToken.None);
                await Task.Delay(250);

                Assert.NotEqual(0, networkResult.ExitCode);
                Assert.Contains(
                    "curl:",
                    Encoding.UTF8.GetString(networkResult.StandardError.Content.Span),
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
        finally
        {
            if (File.Exists(outsidePath))
                File.Delete(outsidePath);
            await resetter.ClearAsync(
                package.Id.FamilyName,
                profile,
                CancellationToken.None);
        }

        AssertProfileDataEmpty(profile);
    }

    [PackagedCodecSmokeFact]
    [Trait("Category", "PackagedCodecSmoke")]
    public async Task ProductLoader_InstalledFrameworkRunsFileBackedDocumentsAndFailsClosed()
    {
        var package = FindCodecHostFrameworkPackage();
        Assert.True(package.IsFramework);
        var configuration = CreateInstalledConfiguration(package);
        var profile = AppContainerProfileAccess.GetExistingPackageProfileInfo(
            configuration.PackageFamilyName);
        var launcher = new InstalledCodecAuditLauncher(profile);
        var loader = CreateInstalledLoader(configuration, launcher);
        var directory = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(),
            $"ezy-installed-codec-{Guid.NewGuid():N}"));
        var pdfPath = Path.Combine(directory.FullName, "three-pages.pdf");
        var psdPath = Path.Combine(directory.FullName, "composite.psd");
        var malformedPath = Path.Combine(directory.FullName, "malformed.pdf");

        try
        {
            await File.WriteAllBytesAsync(
                pdfPath,
                CodecSyntheticDocumentFactory.BuildPdf(pageCount: 3));
            await File.WriteAllBytesAsync(
                psdPath,
                CodecSyntheticDocumentFactory.BuildRgbPsd(width: 4, height: 3));
            await File.WriteAllBytesAsync(malformedPath, "%PDF-1.7\nnot-a-document"u8.ToArray());

            await Assert.ThrowsAsync<UnsupportedFormatException>(() =>
                new DocumentLoader().LoadFileAsync(pdfPath, CancellationToken.None));
            Assert.Empty(launcher.Requests);

            SeedProfileData(profile, "pdf-initial");
            using (var pdf = await loader.LoadFileAsync(pdfPath, CancellationToken.None))
            {
                Assert.Equal(ImageFormat.Pdf, pdf.Format);
                Assert.Equal(DocumentSequenceKind.Pages, pdf.SequenceKind);
                Assert.Equal(3, pdf.FrameCount);
                Assert.Equal(0, pdf.CurrentFrameIndex);
                Assert.True(pdf.SupportsScaleDependentRendering);
                Assert.Equal(configuration.PackageVersion, pdf.Renderer.Version);
                Assert.Single(launcher.Requests);
                AssertProfileDataEmpty(profile);

                SeedProfileData(profile, "pdf-page");
                Assert.True(await pdf.LoadFrameAsync(
                    2,
                    new DecodeRequest(InputLimits.Default, PreferredMaxDimension: 240),
                    forceRerender: false,
                    CancellationToken.None));
                Assert.Equal(2, pdf.CurrentFrameIndex);
                Assert.Equal(2, launcher.Requests.Count);
                Assert.True(Math.Max(pdf.Frame.Width, pdf.Frame.Height) <= 240);
                var pageWidth = pdf.Frame.Width;
                var pageHeight = pdf.Frame.Height;
                AssertProfileDataEmpty(profile);

                SeedProfileData(profile, "pdf-rerender");
                Assert.True(await pdf.LoadFrameAsync(
                    pdf.CurrentFrameIndex,
                    new DecodeRequest(InputLimits.Default, PreferredMaxDimension: 120),
                    forceRerender: true,
                    CancellationToken.None));
                Assert.Equal(2, pdf.CurrentFrameIndex);
                Assert.Equal(3, launcher.Requests.Count);
                Assert.True(Math.Max(pdf.Frame.Width, pdf.Frame.Height) <= 120);
                Assert.True(pdf.Frame.Width < pageWidth || pdf.Frame.Height < pageHeight);
                AssertProfileDataEmpty(profile);
            }

            SeedProfileData(profile, "psd");
            using (var psd = await loader.LoadFileAsync(psdPath, CancellationToken.None))
            {
                Assert.Equal(ImageFormat.Psd, psd.Format);
                Assert.Equal(DocumentSequenceKind.SingleFrame, psd.SequenceKind);
                Assert.Equal((4, 3), (psd.Frame.Width, psd.Frame.Height));
                Assert.False(psd.SupportsScaleDependentRendering);
                Assert.Equal(new byte[] { 0, 0, 255, 255 }, psd.Frame.Pixels[..4].ToArray());
            }
            AssertProfileDataEmpty(profile);

            SeedProfileData(profile, "malformed");
            var malformed = await Assert.ThrowsAsync<CorruptImageException>(() =>
                loader.LoadFileAsync(malformedPath, CancellationToken.None));
            Assert.Equal(ImageLoadFailureKind.CorruptFile, malformed.Kind);
            AssertProfileDataEmpty(profile);

            var crashLauncher = new InstalledCodecAuditLauncher(
                profile,
                constrainProcessMemory: true);
            var crashClient = CreateInstalledClient(configuration, crashLauncher);
            SeedProfileData(profile, "host-termination");
            await using (var input = new FileStream(
                pdfPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var unavailable = await Assert.ThrowsAsync<CodecUnavailableException>(() =>
                    crashClient.DecodeAsync(
                        input,
                        CodecFormat.Pdf,
                        pageIndex: 0,
                        targetMaxDimension: 120,
                        CancellationToken.None));
                Assert.Equal(ImageLoadFailureKind.SystemCodecUnavailable, unavailable.Kind);
            }
            Assert.True(crashLauncher.ObservedHostFailure);
            Assert.Single(crashLauncher.Requests);
            AssertProfileDataEmpty(profile);

            SeedProfileData(profile, "recovery");
            using var recovered = await loader.LoadFileAsync(pdfPath, CancellationToken.None);
            Assert.Equal(ImageFormat.Pdf, recovered.Format);
            Assert.Equal(3, recovered.FrameCount);
            AssertProfileDataEmpty(profile);

            Assert.All(launcher.PreLaunchDataWasEmpty, value => Assert.True(value));
            Assert.All(crashLauncher.PreLaunchDataWasEmpty, value => Assert.True(value));
            Assert.All(launcher.Requests, request =>
            {
                Assert.Equal(CodecInputTransport.InheritedReadHandle, request.InputTransport);
                Assert.NotEqual(0UL, request.InputHandle);
            });
            Assert.Equal(
                [
                    (CodecFormat.Pdf, 0, 0),
                    (CodecFormat.Pdf, 2, 240),
                    (CodecFormat.Pdf, 2, 120),
                    (CodecFormat.Psd, 0, 0),
                    (CodecFormat.Pdf, 0, 0),
                    (CodecFormat.Pdf, 0, 0),
                ],
                launcher.Requests
                    .Select(static request =>
                        (request.Format, request.PageIndex, request.TargetWidth))
                    .ToArray());
        }
        finally
        {
            Directory.Delete(directory.FullName, recursive: true);
        }
    }

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

    private static IsolatedCodecHostConfiguration CreateInstalledConfiguration(Package package)
    {
        var version = package.Id.Version;
        return new IsolatedCodecHostConfiguration(
            package.Id.FamilyName,
            Path.Combine(
                package.InstalledPath,
                IsolatedCodecHostConfiguration.HostExecutableFileName),
            $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}");
    }

    private static Package FindInstalledMainPackage()
    {
        var package = new PackageManager()
            .FindPackagesForUserWithPackageTypes(string.Empty, PackageTypes.Main)
            .Where(candidate => string.Equals(
                candidate.Id.Name,
                "GRTech.ezyImageViewer",
                StringComparison.Ordinal))
            .OrderByDescending(static candidate => new Version(
                candidate.Id.Version.Major,
                candidate.Id.Version.Minor,
                candidate.Id.Version.Build,
                candidate.Id.Version.Revision))
            .FirstOrDefault();
        return package ?? throw new InvalidOperationException(
            "The installed ezy Image Viewer main package was not found.");
    }

    private static async Task RunInstalledAppCodecSmokeAsync(
        string appUserModelId,
        string sourcePath,
        string resultPath,
        string expectedFormat,
        int expectedFrameCount,
        string expectedSequenceKind,
        int? expectedWidth,
        int? expectedHeight,
        string expectedRendererVersion,
        string packageFamilyName,
        AppContainerProfileInfo profile)
    {
        Process? process = null;
        Exception? operationFailure = null;
        var cleanupFailures = new List<Exception>();
        try
        {
            SeedProfileData(profile, $"installed-main-{expectedFormat}");
            var arguments = string.Join(
                ' ',
                QuoteCommandLineArgument($"--smoke-open={sourcePath}"),
                QuoteCommandLineArgument($"--smoke-out={resultPath}"),
                "--smoke-codec");
            var processId = ActivatePackagedApplication(appUserModelId, arguments);
            process = Process.GetProcessById(checked((int)processId));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(deadline.Token);

            Assert.Equal(0, process.ExitCode);
            Assert.True(File.Exists(resultPath), $"Installed app smoke result was not written: {resultPath}");
            await using var resultStream = File.OpenRead(resultPath);
            using var result = await JsonDocument.ParseAsync(resultStream);
            var root = result.RootElement;
            Assert.Equal("Ready", root.GetProperty("state").GetString());
            Assert.Equal(expectedFormat, root.GetProperty("format").GetString());
            Assert.Equal(expectedFrameCount, root.GetProperty("frameCount").GetInt32());
            Assert.Equal(expectedSequenceKind, root.GetProperty("sequenceKind").GetString());
            Assert.True(root.GetProperty("packageIdentity").GetBoolean());
            Assert.True(root.GetProperty("isolatedCodecExercise").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("error").ValueKind);
            var renderer = root.GetProperty("renderer");
            Assert.StartsWith(
                "Isolated CodecHost",
                renderer.GetProperty("Name").GetString() ?? string.Empty,
                StringComparison.Ordinal);
            Assert.Equal(expectedRendererVersion, renderer.GetProperty("Version").GetString());
            if (expectedWidth is { } width)
                Assert.Equal(width, root.GetProperty("width").GetInt32());
            else
                Assert.True(root.GetProperty("width").GetInt32() > 0);
            if (expectedHeight is { } height)
                Assert.Equal(height, root.GetProperty("height").GetInt32());
            else
                Assert.True(root.GetProperty("height").GetInt32() > 0);
            AssertProfileDataEmpty(profile);
        }
        catch (Exception ex)
        {
            operationFailure = ex;
        }

        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            cleanupFailures.Add(ex);
        }
        finally
        {
            process?.Dispose();
        }

        try
        {
            await new ApplicationDataCodecPackageDataResetter().ClearAsync(
                packageFamilyName,
                profile,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            cleanupFailures.Add(ex);
        }

        if (operationFailure is not null)
        {
            if (cleanupFailures.Count > 0)
            {
                throw new AggregateException(
                    "The installed app codec smoke and its cleanup both failed.",
                    [operationFailure, .. cleanupFailures]);
            }
            ExceptionDispatchInfo.Capture(operationFailure).Throw();
        }
        if (cleanupFailures.Count == 1)
            ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        if (cleanupFailures.Count > 1)
            throw new AggregateException("Installed app codec smoke cleanup failed.", cleanupFailures);
    }

    private static uint ActivatePackagedApplication(
        string appUserModelId,
        string arguments)
    {
        IApplicationActivationManager? manager = null;
        try
        {
            manager = (IApplicationActivationManager)(object)new ApplicationActivationManager();
            var result = manager.ActivateApplication(
                appUserModelId,
                arguments,
                ActivateOptions.None,
                out var processId);
            Marshal.ThrowExceptionForHR(result);
            if (processId == 0)
                throw new InvalidOperationException("Package activation returned an empty process identifier.");
            return processId;
        }
        finally
        {
            if (manager is not null && Marshal.IsComObject(manager))
                _ = Marshal.FinalReleaseComObject(manager);
        }
    }

    private static string QuoteCommandLineArgument(string value)
    {
        if (value.Contains('"', StringComparison.Ordinal))
            throw new ArgumentException("Smoke arguments cannot contain quotation marks.", nameof(value));
        return string.Concat((char)34, value, (char)34);
    }

    private static string FormatPackageVersion(PackageVersion version) =>
        $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";

    [Flags]
    private enum ActivateOptions : uint
    {
        None = 0,
    }

    [ComImport]
    [Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        [PreserveSig]
        int ActivateApplication(
            [MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments,
            ActivateOptions options,
            out uint processId);
    }

    [ComImport]
    [Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private sealed class ApplicationActivationManager;

    private static DocumentLoader CreateInstalledLoader(
        IsolatedCodecHostConfiguration configuration,
        IIsolatedCodecProcessLauncher launcher) => new(
        limits: null,
        new WicImageDecoder(),
        new SkiaImageDecoder(),
        new SvgImageDecoder(),
        new WicCodecCatalog(),
        CreateInstalledClient(configuration, launcher));

    private static IsolatedDocumentCodecClient CreateInstalledClient(
        IsolatedCodecHostConfiguration configuration,
        IIsolatedCodecProcessLauncher launcher) => new(
        configuration,
        launcher,
        new ApplicationDataCodecPackageDataResetter(),
        new CodecProfilePathResolver());

    private static void SeedProfileData(
        AppContainerProfileInfo profile,
        string marker)
    {
        var stateDirectory = Directory.CreateDirectory(Path.Combine(
            profile.LocalAppDataPath,
            "sentinel-state"));
        File.WriteAllText(
            Path.Combine(stateDirectory.FullName, $"{marker}-{Guid.NewGuid():N}.marker"),
            "codec-profile-reset-gate");
        Directory.CreateDirectory(profile.TempPath);
        File.WriteAllText(
            Path.Combine(profile.TempPath, $"{marker}-{Guid.NewGuid():N}.tmp"),
            "codec-profile-reset-gate");
        Assert.False(IsProfileDataEmpty(profile));
    }

    private static void AssertProfileDataEmpty(AppContainerProfileInfo profile) =>
        Assert.True(
            IsProfileDataEmpty(profile),
            "CodecHost AppContainer profile contained items after its one-shot request.");

    private static bool IsProfileDataEmpty(AppContainerProfileInfo profile)
    {
        if (!Directory.Exists(profile.LocalAppDataPath)
            || !Directory.Exists(profile.TempPath)
            || Directory.EnumerateFileSystemEntries(profile.TempPath).Any())
        {
            return false;
        }

        var entries = Directory.EnumerateFileSystemEntries(profile.LocalAppDataPath).ToArray();
        return entries.Length == 1
            && string.Equals(entries[0], profile.TempPath, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class InstalledCodecAuditLauncher(
        AppContainerProfileInfo profile,
        bool constrainProcessMemory = false,
        Action? requestSent = null) : IIsolatedCodecProcessLauncher
    {
        private readonly ClassicAppContainerProcessLauncher _inner =
            new(TimeProvider.System, requestSent);

        public ConcurrentQueue<CodecRequest> Requests { get; } = new();
        public ConcurrentQueue<bool> PreLaunchDataWasEmpty { get; } = new();
        public bool ObservedHostFailure { get; private set; }

        public async Task<IsolatedCodecProcessResult> ExecuteAsync(
            IsolatedCodecProcessRequest request,
            IsolatedCodecProcessPolicy policy,
            CancellationToken cancellationToken)
        {
            PreLaunchDataWasEmpty.Enqueue(
                IsProfileDataEmpty(profile));
            var forwarded = WrapInheritedControlMessage(request);
            var effectivePolicy = constrainProcessMemory
                ? policy with
                {
                    WallClockDeadline = TimeSpan.FromSeconds(10),
                    ProcessMemoryLimitBytes = 1L * 1024 * 1024,
                }
                : policy;

            try
            {
                var result = await _inner.ExecuteAsync(
                    forwarded,
                    effectivePolicy,
                    cancellationToken);
                ObservedHostFailure |= result.ExitCode != 0;
                return result;
            }
            catch
            {
                ObservedHostFailure = true;
                throw;
            }
        }

        private IsolatedCodecProcessRequest WrapInheritedControlMessage(
            IsolatedCodecProcessRequest request)
        {
            var source = request.InheritedSource
                ?? throw new InvalidOperationException(
                    "The packaged product smoke gate requires file-backed inherited input.");
            return request with
            {
                InheritedSource = source with
                {
                    CreateStandardInputAsync = async (handle, cancellationToken) =>
                    {
                        var control = await source.CreateStandardInputAsync(
                            handle,
                            cancellationToken);
                        await using var wire = new MemoryStream(control.ToArray(), writable: false);
                        var parsed = await CodecWireProtocol.ReadRequestAsync(
                            wire,
                            ProtocolLimits,
                            cancellationToken);
                        if (wire.Position != wire.Length)
                            throw new InvalidDataException(
                                "Inherited codec control message contains trailing bytes.");
                        Requests.Enqueue(parsed);
                        return control;
                    },
                },
            };
        }
    }
}
