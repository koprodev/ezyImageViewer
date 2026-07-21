using System.Security.Principal;
using System.Text;
using System.Collections.Concurrent;
using EzyImageViewer.CodecProtocol;
using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging;
using EzyImageViewer.Imaging.Codecs;
using EzyImageViewer.Imaging.Codecs.Isolation;
using EzyImageViewer.Imaging.Skia;
using EzyImageViewer.Imaging.Svg;
using EzyImageViewer.Imaging.Wic;
using Xunit;

namespace EzyImageViewer.Tests.Imaging;

public sealed class IsolatedCodecProductIntegrationTests
{
    private static readonly CodecProtocolLimits ProtocolLimits = new(
        InputLimits.Default.MaxFileBytes,
        CodecBoundaryLimits.MaxPayloadBytes,
        CodecBoundaryLimits.MaxDiagnosticBytes,
        InputLimits.Default.MaxDimension,
        InputLimits.Default.MaxFrameCount,
        InputLimits.Default.HardMaxPixels);

    [Fact]
    public async Task ConfiguredPdf_IsLazyPagedAndScaleDependentWithoutCatalogActivation()
    {
        var codec = new FakeDocumentCodecClient(pageCount: 3);
        var loader = CreateLoader(codec);
        using var document = await loader.LoadMemoryAsync(
            Encoding.ASCII.GetBytes("%PDF-1.7\nproduct-gate"),
            DocumentSource.FromClipboard(),
            CancellationToken.None);

        Assert.Equal(ImageFormat.Pdf, document.Format);
        Assert.Equal(DocumentSequenceKind.Pages, document.SequenceKind);
        Assert.Equal(3, document.FrameCount);
        Assert.True(document.SupportsScaleDependentRendering);
        Assert.Equal("9.8.7.6", document.Renderer.Version);
        Assert.Single(codec.Calls);
        Assert.Equal((CodecFormat.Pdf, 0, 0), codec.Calls[0]);

        await document.LoadFrameAsync(
            2,
            new DecodeRequest(InputLimits.Default, PreferredMaxDimension: 240),
            forceRerender: false,
            CancellationToken.None);
        await document.LoadFrameAsync(
            document.CurrentFrameIndex,
            new DecodeRequest(InputLimits.Default, PreferredMaxDimension: 120),
            forceRerender: true,
            CancellationToken.None);

        Assert.Equal(2, document.CurrentFrameIndex);
        Assert.Equal(
            [
                (CodecFormat.Pdf, 0, 0),
                (CodecFormat.Pdf, 2, 240),
                (CodecFormat.Pdf, 2, 120),
            ],
            codec.Calls);
        Assert.DoesNotContain(".pdf", ImageFormatCatalog.ViewableExtensions);
    }

    [Fact]
    public async Task ConfiguredPsd_DecodesCompositeAsSingleNonScalableFrame()
    {
        var codec = new FakeDocumentCodecClient(pageCount: 1);
        var loader = CreateLoader(codec);
        using var document = await loader.LoadMemoryAsync(
            Encoding.ASCII.GetBytes("8BPS-product-gate"),
            DocumentSource.FromClipboard(),
            CancellationToken.None);

        Assert.Equal(ImageFormat.Psd, document.Format);
        Assert.Equal(DocumentSequenceKind.SingleFrame, document.SequenceKind);
        Assert.Equal(1, document.FrameCount);
        Assert.False(document.SupportsScaleDependentRendering);
        Assert.Equal((CodecFormat.Psd, 0, 0), Assert.Single(codec.Calls));
        Assert.DoesNotContain(".psd", ImageFormatCatalog.ViewableExtensions);
    }

    [Fact]
    public async Task ProductClient_UsesInlineTransportMinimalEnvironmentAndTwoStateResets()
    {
        using var fixture = new ClientFixture();
        var expected = "%PDF-1.7 inline"u8.ToArray();
        using var input = new MemoryStream(expected, writable: false);

        var decoded = await fixture.Client.DecodeAsync(
            input,
            CodecFormat.Pdf,
            pageIndex: 0,
            targetMaxDimension: 0,
            CancellationToken.None);

        Assert.Equal(expected, fixture.Launcher.LastInlineInput);
        Assert.Equal(CodecInputTransport.Inline, fixture.Launcher.LastRequest!.InputTransport);
        Assert.Null(fixture.Launcher.LastProcessRequest!.InheritedSource);
        Assert.Equal(AppContainerProfileSource.ExistingPackage, fixture.Launcher.LastPolicy!.ProfileSource);
        Assert.Equal(AppContainerCapabilities.CodeGeneration, fixture.Launcher.LastPolicy.Capabilities);
        Assert.Equal(fixture.Profile.LocalAppDataPath,
            fixture.Launcher.LastProcessRequest.Environment["USERPROFILE"]);
        Assert.Equal("0", fixture.Launcher.LastProcessRequest.Environment["DOTNET_EnableDiagnostics"]);
        Assert.Equal(2, fixture.Resetter.Calls);
        Assert.Equal(1, fixture.ProfileResolver.Calls);
        Assert.All(fixture.Resetter.Profiles, profile => Assert.Same(fixture.Profile, profile));
        Assert.Equal(new PixelSize(2, 1), decoded.NativeSize);
        Assert.Equal(8, decoded.PixelLength);
        Assert.True(decoded.Pixels.Length >= decoded.PixelLength);
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 0, 255, 255 }, decoded.Pixels[..8]);
        Assert.Equal(
            CodecBoundaryLimits.MaxStandardOutputBytes,
            fixture.Launcher.LastPolicy.MaxStandardOutputBytes);
        Assert.Equal(
            (long)fixture.Launcher.LastPolicy.MaxStandardOutputBytes,
            CodecWireProtocol.ResponseHeaderSize
                + CodecBoundaryLimits.MaxDiagnosticBytes
                + ProtocolLimits.MaxPayloadBytes);
        Assert.Equal(
            320L * 1024 * 1024
                + CodecWireProtocol.ResponseHeaderSize
                + CodecBoundaryLimits.MaxDiagnosticBytes,
            BoundedPipeReader.CalculateMaximumAllocationDuringGrowth(
                fixture.Launcher.LastPolicy.MaxStandardOutputBytes));
        Assert.True(
            BoundedPipeReader.CalculateMaximumAllocationDuringGrowth(
                fixture.Launcher.LastPolicy.MaxStandardOutputBytes)
                <= InputLimits.Default.DisplayByteBudget);
        Assert.Equal(
            (long)CodecWireProtocol.ResponseHeaderSize
                + CodecBoundaryLimits.MaxDiagnosticBytes,
            fixture.Launcher.LastPolicy.MaxStandardOutputBytes
                + ProtocolLimits.MaxPayloadBytes
                - InputLimits.Default.DisplayByteBudget);
    }

    [Fact]
    public async Task ProductClient_FileInputSerializesOnlyInheritedReadHandleControlMessage()
    {
        using var fixture = new ClientFixture();
        var path = Path.Combine(fixture.DirectoryPath, "source.pdf");
        await File.WriteAllBytesAsync(path, "%PDF-1.7 inherited"u8.ToArray());
        using var input = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        _ = await fixture.Client.DecodeAsync(
            input,
            CodecFormat.Pdf,
            pageIndex: 0,
            targetMaxDimension: 0,
            CancellationToken.None);

        Assert.NotNull(fixture.Launcher.LastProcessRequest!.InheritedSource);
        Assert.NotSame(input.SafeFileHandle,
            fixture.Launcher.LastProcessRequest.InheritedSource!.Handle);
        Assert.True(fixture.Launcher.LastProcessRequest.InheritedSource.Handle.IsClosed);
        Assert.Empty(fixture.Launcher.LastProcessRequest.StandardInput.ToArray());
        Assert.Equal(CodecInputTransport.InheritedReadHandle, fixture.Launcher.LastRequest!.InputTransport);
        Assert.Equal(0x1234UL, fixture.Launcher.LastRequest.InputHandle);
        Assert.Empty(fixture.Launcher.LastInlineInput);
        Assert.True(input.CanRead);
        Assert.Equal(2, fixture.Resetter.Calls);
    }

    [Fact]
    public async Task ProductClient_SerializesConcurrentOneShotRequests()
    {
        using var fixture = new ClientFixture();
        fixture.Launcher.Delay = TimeSpan.FromMilliseconds(30);
        var secondClient = fixture.CreateAdditionalClient();
        using var first = new MemoryStream("%PDF-a"u8.ToArray(), writable: false);
        using var second = new MemoryStream("%PDF-b"u8.ToArray(), writable: false);

        await Task.WhenAll(
            fixture.Client.DecodeAsync(first, CodecFormat.Pdf, 0, 0, CancellationToken.None),
            secondClient.DecodeAsync(second, CodecFormat.Pdf, 0, 0, CancellationToken.None));

        Assert.Equal(1, fixture.Launcher.MaximumConcurrentExecutions);
        Assert.Equal(4, fixture.Resetter.Calls);
        Assert.Equal(
            ["clear", "execute", "clear", "clear", "execute", "clear"],
            fixture.Events.ToArray());
    }

    [Fact]
    public async Task ProductClient_RejectsMismatchedCorrelationAndStillClearsState()
    {
        using var fixture = new ClientFixture();
        fixture.Launcher.MutateResponse = response => response with
        {
            Nonce = response.Nonce + 1,
        };
        using var input = new MemoryStream("%PDF-correlation"u8.ToArray(), writable: false);

        var exception = await Assert.ThrowsAsync<CodecUnavailableException>(() =>
            fixture.Client.DecodeAsync(input, CodecFormat.Pdf, 0, 0, CancellationToken.None));

        Assert.Equal(ImageLoadFailureKind.SystemCodecUnavailable, exception.Kind);
        Assert.Equal(2, fixture.Resetter.Calls);
    }

    [Fact]
    public async Task ProductClient_MapsHostRejectionAndRejectsTrailingStdout()
    {
        using (var rejected = new ClientFixture())
        {
            rejected.Launcher.ResponseResult = CodecResultCode.CorruptInput;
            rejected.Launcher.ResponseDiagnostic = "spoof\r\n[trusted] success\u202E";
            using var input = new MemoryStream("8BPS-rejected"u8.ToArray(), writable: false);
            var exception = await Assert.ThrowsAsync<CorruptImageException>(() =>
                rejected.Client.DecodeAsync(input, CodecFormat.Psd, 0, 0, CancellationToken.None));
            Assert.Equal(ImageLoadFailureKind.CorruptFile, exception.Kind);
            Assert.DoesNotContain("spoof", exception.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("trusted", exception.Message, StringComparison.Ordinal);
            Assert.Equal(2, rejected.Resetter.Calls);
        }

        using (var trailing = new ClientFixture())
        {
            trailing.Launcher.AppendTrailingOutput = true;
            using var input = new MemoryStream("%PDF-trailing"u8.ToArray(), writable: false);
            await Assert.ThrowsAsync<CodecUnavailableException>(() =>
                trailing.Client.DecodeAsync(input, CodecFormat.Pdf, 0, 0, CancellationToken.None));
            Assert.Equal(2, trailing.Resetter.Calls);
        }
    }

    [Fact]
    public async Task ProductClient_DiscardsSuccessfulPayloadWhenPostClearTimesOut()
    {
        using var fixture = new ClientFixture();
        var resetter = new FailingPostClearResetter();
        var client = fixture.CreateClient(resetter);
        using var input = new MemoryStream("%PDF-post-clear"u8.ToArray(), writable: false);

        var exception = await Assert.ThrowsAsync<SecurityLimitExceededException>(() =>
            client.DecodeAsync(input, CodecFormat.Pdf, 0, 0, CancellationToken.None));

        Assert.Equal(ImageLoadFailureKind.ResourceOrSecurityLimitExceeded, exception.Kind);
        Assert.Equal(2, resetter.Calls);
    }

    [Fact]
    public async Task ProductClient_DoesNotLaunchWhenPreClearFails()
    {
        using var fixture = new ClientFixture();
        var resetter = new FailingPreClearResetter();
        var client = fixture.CreateClient(resetter);
        using var input = new MemoryStream("%PDF-pre-clear"u8.ToArray(), writable: false);

        var exception = await Assert.ThrowsAsync<CodecUnavailableException>(() =>
            client.DecodeAsync(input, CodecFormat.Pdf, 0, 0, CancellationToken.None));

        Assert.Equal(ImageLoadFailureKind.SystemCodecUnavailable, exception.Kind);
        Assert.Equal(1, resetter.Calls);
        Assert.Null(fixture.Launcher.LastProcessRequest);
    }

    [Fact]
    public async Task ProductClient_RejectsNonzeroExitOrStderrAndStillClearsState()
    {
        using (var exited = new ClientFixture())
        {
            exited.Launcher.ExitCode = unchecked((int)0xE000_0002);
            using var input = new MemoryStream("%PDF-exit"u8.ToArray(), writable: false);
            await Assert.ThrowsAsync<CodecUnavailableException>(() =>
                exited.Client.DecodeAsync(input, CodecFormat.Pdf, 0, 0, CancellationToken.None));
            Assert.Equal(2, exited.Resetter.Calls);
        }

        using (var stderr = new ClientFixture())
        {
            stderr.Launcher.StandardError = "unexpected"u8.ToArray();
            using var input = new MemoryStream("%PDF-stderr"u8.ToArray(), writable: false);
            await Assert.ThrowsAsync<CodecUnavailableException>(() =>
                stderr.Client.DecodeAsync(input, CodecFormat.Pdf, 0, 0, CancellationToken.None));
            Assert.Equal(2, stderr.Resetter.Calls);
        }
    }

    private static DocumentLoader CreateLoader(IIsolatedDocumentCodecClient codec) => new(
        limits: null,
        new WicImageDecoder(),
        new SkiaImageDecoder(),
        new SvgImageDecoder(),
        new WicCodecCatalog(),
        codec);

    private sealed class FakeDocumentCodecClient(int pageCount) : IIsolatedDocumentCodecClient
    {
        public string RendererVersion => "9.8.7.6";
        public List<(CodecFormat Format, int PageIndex, int Target)> Calls { get; } = [];

        public Task<IsolatedCodecDecodedImage> DecodeAsync(
            Stream input,
            CodecFormat format,
            int pageIndex,
            int targetMaxDimension,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((format, pageIndex, targetMaxDimension));
            var native = new PixelSize(120, 60);
            var width = targetMaxDimension == 0 ? native.Width : targetMaxDimension;
            var height = width / 2;
            var stride = checked(width * 4);
            var pixels = new byte[checked(stride * height)];
            for (var offset = 0; offset < pixels.Length; offset += 4)
            {
                pixels[offset] = (byte)(pageIndex * 40);
                pixels[offset + 2] = 255;
                pixels[offset + 3] = 255;
            }
            return Task.FromResult(new IsolatedCodecDecodedImage(
                pixels,
                pixels.Length,
                width,
                height,
                stride,
                native,
                pageCount));
        }
    }

    private sealed class ClientFixture : IDisposable
    {
        public ClientFixture()
        {
            DirectoryPath = Path.Combine(Path.GetTempPath(), $"ezy-product-codec-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
            var executable = Path.Combine(
                DirectoryPath,
                IsolatedCodecHostConfiguration.HostExecutableFileName);
            File.WriteAllBytes(executable, [0]);
            Configuration = new IsolatedCodecHostConfiguration(
                "GRTech.ezyImageViewer.CodecHost_test",
                executable,
                "1.2.3.4");
            Profile = new AppContainerProfileInfo(
                new SecurityIdentifier("S-1-15-2-1"),
                Path.Combine(DirectoryPath, "profile"),
                Path.Combine(DirectoryPath, "profile", "Temp"));
            Launcher = new RecordingLauncher(Events.Enqueue);
            Resetter = new RecordingResetter(Events.Enqueue);
            ProfileResolver = new RecordingProfileResolver(Profile);
            Client = new IsolatedDocumentCodecClient(
                Configuration,
                Launcher,
                Resetter,
                ProfileResolver);
        }

        public string DirectoryPath { get; }
        public IsolatedCodecHostConfiguration Configuration { get; }
        public AppContainerProfileInfo Profile { get; }
        public ConcurrentQueue<string> Events { get; } = new();
        public RecordingLauncher Launcher { get; }
        public RecordingResetter Resetter { get; }
        public RecordingProfileResolver ProfileResolver { get; }
        public IsolatedDocumentCodecClient Client { get; }

        public IsolatedDocumentCodecClient CreateAdditionalClient() => new(
            Configuration,
            Launcher,
            Resetter,
            ProfileResolver);

        public IsolatedDocumentCodecClient CreateClient(ICodecPackageDataResetter resetter) => new(
            Configuration,
            Launcher,
            resetter,
            ProfileResolver);

        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
    }

    private sealed class RecordingProfileResolver(AppContainerProfileInfo profile)
        : ICodecProfilePathResolver
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public AppContainerProfileInfo GetProfileInfo(IsolatedCodecProcessPolicy policy)
        {
            Interlocked.Increment(ref _calls);
            return profile;
        }
    }

    private sealed class RecordingResetter(Action<string> recordEvent) : ICodecPackageDataResetter
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ConcurrentQueue<AppContainerProfileInfo> Profiles { get; } = new();

        public Task ClearAsync(
            string packageFamilyName,
            AppContainerProfileInfo profile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            recordEvent("clear");
            Profiles.Enqueue(profile);
            Interlocked.Increment(ref _calls);
            return Task.CompletedTask;
        }
    }

    private sealed class FailingPostClearResetter : ICodecPackageDataResetter
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task ClearAsync(
            string packageFamilyName,
            AppContainerProfileInfo profile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _calls) == 2)
                throw new TimeoutException("simulated post-clear timeout");
            return Task.CompletedTask;
        }
    }

    private sealed class FailingPreClearResetter : ICodecPackageDataResetter
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public Task ClearAsync(
            string packageFamilyName,
            AppContainerProfileInfo profile,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            throw new IOException("simulated pre-clear failure");
        }
    }

    private sealed class RecordingLauncher(Action<string> recordEvent) : IIsolatedCodecProcessLauncher
    {
        private int _active;
        private int _maximumConcurrent;

        public TimeSpan Delay { get; set; }
        public Func<CodecResponse, CodecResponse>? MutateResponse { get; set; }
        public CodecResultCode ResponseResult { get; set; } = CodecResultCode.Success;
        public string ResponseDiagnostic { get; set; } = "test-rejection";
        public bool AppendTrailingOutput { get; set; }
        public int ExitCode { get; set; }
        public byte[] StandardError { get; set; } = [];
        public int MaximumConcurrentExecutions => Volatile.Read(ref _maximumConcurrent);
        public IsolatedCodecProcessRequest? LastProcessRequest { get; private set; }
        public IsolatedCodecProcessPolicy? LastPolicy { get; private set; }
        public CodecRequest? LastRequest { get; private set; }
        public byte[] LastInlineInput { get; private set; } = [];

        public async Task<IsolatedCodecProcessResult> ExecuteAsync(
            IsolatedCodecProcessRequest processRequest,
            IsolatedCodecProcessPolicy policy,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            InterlockedExtensions.Max(ref _maximumConcurrent, active);
            try
            {
                recordEvent("execute");
                LastProcessRequest = processRequest;
                LastPolicy = policy;
                ReadOnlyMemory<byte> standardInput = processRequest.StandardInput;
                if (processRequest.InheritedSource is { } inherited)
                {
                    standardInput = await inherited.CreateStandardInputAsync(
                        (nint)0x1234,
                        cancellationToken);
                }

                await using var requestWire = new MemoryStream(standardInput.ToArray(), writable: false);
                var request = await CodecWireProtocol.ReadRequestAsync(
                    requestWire,
                    ProtocolLimits,
                    cancellationToken);
                LastRequest = request;
                if (request.InputTransport == CodecInputTransport.Inline)
                {
                    await using var inline = new MemoryStream();
                    await CodecWireProtocol.CopyInlineInputAsync(
                        requestWire,
                        inline,
                        request,
                        ProtocolLimits,
                        cancellationToken);
                    LastInlineInput = inline.ToArray();
                }
                else
                {
                    LastInlineInput = [];
                }
                Assert.Equal(requestWire.Length, requestWire.Position);

                if (Delay > TimeSpan.Zero)
                    await Task.Delay(Delay, cancellationToken);
                var payload = ResponseResult == CodecResultCode.Success
                    ? new byte[] { 0, 0, 255, 255, 0, 0, 255, 255 }
                    : [];
                var response = new CodecResponse(
                    request.RequestId,
                    request.Nonce,
                    request.Operation,
                    request.Format,
                    ResponseResult,
                    Width: ResponseResult == CodecResultCode.Success ? 2 : 0,
                    Height: ResponseResult == CodecResultCode.Success ? 1 : 0,
                    Stride: ResponseResult == CodecResultCode.Success ? 8 : 0,
                    NativeWidth: ResponseResult == CodecResultCode.Success ? 2 : 0,
                    NativeHeight: ResponseResult == CodecResultCode.Success ? 1 : 0,
                    PageCount: ResponseResult == CodecResultCode.Success ? 1 : 0,
                    PayloadLength: payload.Length,
                    Diagnostic: ResponseResult == CodecResultCode.Success
                        ? null
                        : ResponseDiagnostic);
                if (MutateResponse is not null)
                    response = MutateResponse(response);

                await using var responseWire = new MemoryStream();
                using var payloadStream = payload.Length == 0
                    ? null
                    : new MemoryStream(payload, writable: false);
                await CodecWireProtocol.WriteResponseAsync(
                    responseWire,
                    response,
                    payloadStream,
                    ProtocolLimits,
                    cancellationToken);
                if (AppendTrailingOutput)
                    responseWire.WriteByte(0xA5);
                return new IsolatedCodecProcessResult(
                    ExitCode,
                    responseWire.ToArray(),
                    StandardError);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private static class InterlockedExtensions
    {
        public static void Max(ref int location, int value)
        {
            var current = Volatile.Read(ref location);
            while (current < value)
            {
                var observed = Interlocked.CompareExchange(ref location, value, current);
                if (observed == current)
                    return;
                current = observed;
            }
        }
    }
}
