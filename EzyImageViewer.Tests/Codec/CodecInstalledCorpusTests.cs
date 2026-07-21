using EzyImageViewer.CodecProtocol;
using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging;
using EzyImageViewer.Imaging.Codecs;
using EzyImageViewer.Imaging.Codecs.Isolation;
using Xunit;

namespace EzyImageViewer.Tests.Codec;

public sealed partial class CodecIsolationTests
{
    private const string InstalledCorpusGateVariable =
        "EZYIMAGEVIEWER_RUN_INSTALLED_CODEC_CORPUS";
    private const string CorpusRootVariable = "EZYIMAGEVIEWER_FORMAT_CORPUS";

    [InstalledCodecCorpusFact]
    [Trait("Category", "ExternalCorpus")]
    [Trait("Boundary", "InstalledProduct")]
    public async Task ConfiguredPdfPsdCorpus_InstalledProductMatchesExactOutcomesAndGoldens()
    {
        var manifest = CodecCorpusManifestSerializer.ReadTrackedManifest();
        CodecCorpusManifestValidator.ValidateCodecActivationCoverage(manifest);

        var rootValue = Environment.GetEnvironmentVariable(CorpusRootVariable);
        Assert.False(
            string.IsNullOrWhiteSpace(rootValue),
            $"{CorpusRootVariable} must be set when {InstalledCorpusGateVariable}=1.");
        var root = Path.GetFullPath(rootValue!);
        Assert.True(Directory.Exists(root), $"Corpus root does not exist: {root}");

        var package = FindCodecHostFrameworkPackage();
        Assert.True(package.IsFramework);
        var configuration = CreateInstalledConfiguration(package);
        var profile = AppContainerProfileAccess.GetExistingPackageProfileInfo(
            configuration.PackageFamilyName);
        var requestSentProbe = new RequestSentProbe();
        var launcher = new InstalledCodecAuditLauncher(
            profile,
            requestSent: requestSentProbe.Signal);
        var loader = CreateInstalledLoader(configuration, launcher);
        var resetter = new ApplicationDataCodecPackageDataResetter();

        try
        {
            foreach (var format in manifest.Formats.Where(format =>
                         format.Format is "Pdf" or "Psd"))
            {
                foreach (var sample in format.Samples)
                {
                    CodecCorpusFile.VerifyDigest(root, sample.Path, sample.Sha256);
                    SeedProfileData(profile, sample.Id!);
                    var requestsBefore = launcher.Requests.Count;
                    await VerifyInstalledProductSampleAsync(
                        root,
                        loader,
                        configuration.PackageVersion,
                        requestSentProbe,
                        format,
                        sample);
                    Assert.True(
                        launcher.Requests.Count > requestsBefore,
                        $"Installed product did not cross the isolated CodecHost boundary for '{sample.Id}'.");
                    AssertProfileDataEmpty(profile);
                }
            }

            Assert.All(launcher.PreLaunchDataWasEmpty, value => Assert.True(value));
            Assert.All(launcher.Requests, request =>
            {
                Assert.Equal(CodecInputTransport.InheritedReadHandle, request.InputTransport);
                Assert.NotEqual(0UL, request.InputHandle);
            });
        }
        finally
        {
            await resetter.ClearAsync(
                configuration.PackageFamilyName,
                profile,
                CancellationToken.None);
        }
        AssertProfileDataEmpty(profile);
    }

    private static async Task VerifyInstalledProductSampleAsync(
        string root,
        DocumentLoader loader,
        string packageVersion,
        RequestSentProbe requestSentProbe,
        CodecCorpusFormat format,
        CodecCorpusSample sample)
    {
        var path = CodecCorpusFile.Resolve(root, sample.Path);
        var expected = sample.Expected!;
        switch (expected.ProductOutcome!.Value)
        {
            case CodecCorpusProductOutcome.Success:
                using (var document = await loader.LoadFileAsync(path, CancellationToken.None))
                {
                    Assert.Equal(ToImageFormat(format), document.Format);
                    Assert.Equal(expected.PageCount, document.FrameCount);
                    Assert.Equal(
                        (expected.NativeWidth, expected.NativeHeight),
                        (document.NativeSize.Width, document.NativeSize.Height));
                    Assert.StartsWith(
                        "Isolated CodecHost",
                        document.Renderer.Name,
                        StringComparison.Ordinal);
                    Assert.Equal(packageVersion, document.Renderer.Version);
                    await VerifyProductGoldensAsync(root, document, format, sample.Goldens!);
                }
                break;

            case CodecCorpusProductOutcome.Canceled:
                using (var cancellation = new CancellationTokenSource())
                {
                    var requestSent = requestSentProbe.Arm();
                    var loadTask = loader.LoadFileAsync(path, cancellation.Token);
                    var observedRequest = requestSent.WaitAsync(TimeSpan.FromSeconds(10));
                    var first = await Task.WhenAny(loadTask, observedRequest);
                    if (first == loadTask)
                    {
                        using var unexpected = await loadTask;
                        Assert.Fail("The slow-render sample completed before the installed Host request could be canceled.");
                    }
                    await observedRequest;
                    cancellation.CancelAfter(TimeSpan.FromMilliseconds(
                        expected.CancellationAfterMilliseconds!.Value));
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                    {
                        using var unexpected = await loadTask;
                    });
                }
                break;

            case CodecCorpusProductOutcome.CorruptFile:
            case CodecCorpusProductOutcome.CredentialsOrPermissionRequired:
            case CodecCorpusProductOutcome.ResourceOrSecurityLimitExceeded:
                var rejected = await Assert.ThrowsAnyAsync<ImageRejectedException>(() =>
                    loader.LoadFileAsync(path, CancellationToken.None));
                Assert.Equal(ToFailureKind(expected.ProductOutcome.Value), rejected.Kind);
                break;

            default:
                throw new InvalidDataException(
                    $"Unmapped installed-product outcome '{expected.ProductOutcome}'.");
        }
    }

    private static async Task VerifyProductGoldensAsync(
        string root,
        ImageDocument document,
        CodecCorpusFormat format,
        IReadOnlyList<CodecCorpusGolden> goldens)
    {
        foreach (var golden in goldens)
        {
            if (format.Format == "Psd")
            {
                Assert.Equal(0, golden.PageIndex);
                Assert.Equal(0, golden.TargetMaxDimension);
            }
            else
            {
                var request = golden.TargetMaxDimension == 0
                    ? DecodeRequest.Default
                    : new DecodeRequest(
                        InputLimits.Default,
                        PreferredMaxDimension: golden.TargetMaxDimension);
                Assert.True(await document.LoadFrameAsync(
                    golden.PageIndex!.Value,
                    request,
                    forceRerender: true,
                    CancellationToken.None));
            }

            Assert.Equal(
                (golden.NativeWidth!.Value, golden.NativeHeight!.Value),
                (document.NativeSize.Width, document.NativeSize.Height));
            await CodecCorpusGoldenVerifier.AssertMatchesAsync(root, golden, document.Frame);
        }
    }

    private static ImageFormat ToImageFormat(CodecCorpusFormat format) => format.Format switch
    {
        "Pdf" => ImageFormat.Pdf,
        "Psd" => ImageFormat.Psd,
        _ => throw new InvalidDataException($"Unmapped product format '{format.Format}'."),
    };

    private static ImageLoadFailureKind ToFailureKind(CodecCorpusProductOutcome outcome) => outcome switch
    {
        CodecCorpusProductOutcome.CorruptFile => ImageLoadFailureKind.CorruptFile,
        CodecCorpusProductOutcome.CredentialsOrPermissionRequired =>
            ImageLoadFailureKind.CredentialsOrPermissionRequired,
        CodecCorpusProductOutcome.ResourceOrSecurityLimitExceeded =>
            ImageLoadFailureKind.ResourceOrSecurityLimitExceeded,
        _ => throw new InvalidDataException($"Unmapped product failure outcome '{outcome}'."),
    };

    private sealed class RequestSentProbe
    {
        private TaskCompletionSource<bool>? _next;

        public Task Arm()
        {
            var signal = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (Interlocked.CompareExchange(ref _next, signal, null) is not null)
                throw new InvalidOperationException("A request-sent observation is already pending.");
            return signal.Task;
        }

        public void Signal() =>
            Interlocked.Exchange(ref _next, null)?.TrySetResult(true);
    }

    public sealed class InstalledCodecCorpusFactAttribute : FactAttribute
    {
        public InstalledCodecCorpusFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(InstalledCorpusGateVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = $"Set {InstalledCorpusGateVariable}=1, install CodecHost, and configure the PDF/PSD corpus to run this gate.";
            }
        }
    }
}
