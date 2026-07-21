using EzyImageViewer.CodecProtocol;
using EzyImageViewer.Core.Imaging;
using Xunit;
using Xunit.Abstractions;

namespace EzyImageViewer.Tests.Codec;

public sealed class CodecExternalCorpusTests(ITestOutputHelper output)
{
    private const string RunGateVariable = "EZYIMAGEVIEWER_RUN_CODEC_CORPUS";
    private const string CorpusRootVariable = "EZYIMAGEVIEWER_FORMAT_CORPUS";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    [ExternalCodecCorpusFact]
    [Trait("Category", "ExternalCorpus")]
    [Trait("Boundary", "DirectHost")]
    public async Task ConfiguredPdfPsdCorpus_DirectHostMatchesExactOutcomesAndGoldens()
    {
        var manifest = CodecCorpusManifestSerializer.ReadTrackedManifest();
        CodecCorpusManifestValidator.ValidateCodecActivationCoverage(manifest);

        var rootValue = Environment.GetEnvironmentVariable(CorpusRootVariable);
        Assert.False(
            string.IsNullOrWhiteSpace(rootValue),
            $"{CorpusRootVariable} must be set when {RunGateVariable}=1.");
        var root = Path.GetFullPath(rootValue!);
        Assert.True(Directory.Exists(root), $"Corpus root does not exist: {root}");

        foreach (var format in manifest.Formats.Where(format =>
                     format.Format is "Pdf" or "Psd"))
        {
            var protocolFormat = CodecCorpusManifestValidator.ToProtocolFormat(format);
            foreach (var sample in format.Samples)
            {
                CodecCorpusFile.VerifyDigest(root, sample.Path, sample.Sha256);
                var inputPath = CodecCorpusFile.Resolve(root, sample.Path);
                var input = await ReadBoundedAsync(inputPath);
                await VerifySampleAsync(root, protocolFormat, sample, input);
            }
        }
    }

    private async Task VerifySampleAsync(
        string root,
        CodecFormat format,
        CodecCorpusSample sample,
        byte[] input)
    {
        var expected = sample.Expected!;
        var inspect = await CodecHostGateTestClient.RunAsync(
            CodecHostTestClient.Request(CodecOperation.Inspect, format, input.Length),
            input,
            RequestTimeout);
        var inspectResponse = AssertExactResult(
            sample.Id!,
            expected.InspectResult!.Value,
            inspect);
        if (expected.InspectResult != CodecCorpusHostResult.Success)
        {
            Assert.Empty(inspect.Payload);
            WriteOutcome(sample, "inspect", inspectResponse, inspect);
            return;
        }

        AssertExpectedInspectMetadata(expected, inspectResponse);
        Assert.Empty(inspect.Payload);

        var baselineGolden = sample.Goldens!.SingleOrDefault(golden =>
            golden.PageIndex == expected.DecodePageIndex
            && golden.TargetMaxDimension == expected.DecodeTargetMaxDimension);
        var baseline = await DecodeAsync(
            format,
            input,
            expected.DecodePageIndex!.Value,
            expected.DecodeTargetMaxDimension!.Value);
        var baselineResponse = AssertExactResult(
            $"{sample.Id} baseline decode",
            expected.DecodeResult!.Value,
            baseline);
        if (baselineResponse.Result == CodecResultCode.Success)
        {
            Assert.NotNull(baselineGolden);
            AssertExpectedDecodeMetadata(expected, baselineGolden!, baselineResponse);
            AssertSuccessPayload(baselineResponse, baseline.Payload);
            await AssertQualificationPerformanceBudgetAsync(
                format,
                input,
                sample,
                expected,
                baselineGolden!,
                baseline);
            using var frame = CreateFrame(baselineResponse, baseline.Payload);
            await CodecCorpusGoldenVerifier.AssertMatchesAsync(root, baselineGolden!, frame);
        }
        else
        {
            Assert.Empty(baseline.Payload);
        }
        WriteOutcome(sample, "decode", baselineResponse, baseline);

        foreach (var golden in sample.Goldens!.Where(candidate => candidate != baselineGolden))
        {
            var result = await DecodeAsync(
                format,
                input,
                golden.PageIndex!.Value,
                golden.TargetMaxDimension!.Value);
            var response = AssertExactResult(
                $"{sample.Id} golden page {golden.PageIndex}",
                CodecCorpusHostResult.Success,
                result);
            AssertExpectedDecodeMetadata(expected, golden, response);
            AssertSuccessPayload(response, result.Payload);
            using var frame = CreateFrame(response, result.Payload);
            await CodecCorpusGoldenVerifier.AssertMatchesAsync(root, golden, frame);
        }
    }

    private static Task<CodecHostGateResult> DecodeAsync(
        CodecFormat format,
        byte[] input,
        int pageIndex,
        int targetMaxDimension) =>
        CodecHostGateTestClient.RunAsync(
            CodecHostTestClient.Request(
                CodecOperation.Decode,
                format,
                input.Length,
                pageIndex,
                targetWidth: targetMaxDimension,
                targetHeight: targetMaxDimension),
            input,
            RequestTimeout);

    private static CodecResponse AssertExactResult(
        string name,
        CodecCorpusHostResult expected,
        CodecHostGateResult result)
    {
        Assert.True(
            result.Process.ExitCode == 0,
            $"sample={name}, exit={result.Process.ExitCode}, stderr={result.Process.StandardError}");
        Assert.Equal(string.Empty, result.Process.StandardError);
        var response = Assert.IsType<CodecResponse>(result.Response);
        Assert.True(
            response.Result == CodecCorpusManifestValidator.ToProtocolResult(expected),
            $"sample={name}, expected={expected}, actual={response.Result}, diagnostic={response.Diagnostic}");
        Assert.Equal(response.PayloadLength, result.Payload.LongLength);
        return response;
    }

    private async Task AssertQualificationPerformanceBudgetAsync(
        CodecFormat format,
        byte[] input,
        CodecCorpusSample sample,
        CodecCorpusExpected expected,
        CodecCorpusGolden baselineGolden,
        CodecHostGateResult baseline)
    {
        var budget = sample.QualificationPerformanceBudget;
        if (budget is null)
            return;

        var results = new List<CodecHostGateResult> { baseline };
        for (var repetition = 1; repetition < budget.Repetitions; repetition++)
        {
            var result = await DecodeAsync(
                format,
                input,
                expected.DecodePageIndex!.Value,
                expected.DecodeTargetMaxDimension!.Value);
            var response = AssertExactResult(
                $"{sample.Id} qualification decode {repetition + 1}",
                CodecCorpusHostResult.Success,
                result);
            AssertExpectedDecodeMetadata(expected, baselineGolden, response);
            AssertSuccessPayload(response, result.Payload);
            WriteOutcome(
                sample,
                $"qualification-{repetition + 1}",
                response,
                result);
            results.Add(result);
        }

        var medianElapsedMilliseconds = Median(
            results.Select(result => result.EndToEndElapsed.TotalMilliseconds));
        Assert.True(
            medianElapsedMilliseconds <= budget.MaxMedianDecodeElapsedMilliseconds,
            $"sample={sample.Id}, medianEndToEndMs={medianElapsedMilliseconds:F1}, " +
            $"budgetMs={budget.MaxMedianDecodeElapsedMilliseconds}, " +
            $"repetitions={budget.Repetitions}");
        Assert.True(
            results.Max(result => result.Process.PeakWorkingSetBytes)
                <= budget.MaxPeakWorkingSetBytes,
            $"sample={sample.Id}, peakWorkingSetBytes=" +
            $"{results.Max(result => result.Process.PeakWorkingSetBytes)}, " +
            $"budgetBytes={budget.MaxPeakWorkingSetBytes}");
        Assert.True(
            results.Max(result => result.Process.PeakCommitBytes)
                <= budget.MaxPeakCommitBytes,
            $"sample={sample.Id}, peakCommitBytes=" +
            $"{results.Max(result => result.Process.PeakCommitBytes)}, " +
            $"budgetBytes={budget.MaxPeakCommitBytes}");
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        Assert.NotEmpty(ordered);
        var middle = ordered.Length / 2;
        return (ordered.Length & 1) == 1
            ? ordered[middle]
            : (ordered[middle - 1] + ordered[middle]) / 2;
    }

    private static void AssertSuccessPayload(CodecResponse response, byte[] payload)
    {
        Assert.True(response.Width > 0);
        Assert.True(response.Height > 0);
        Assert.True(response.Stride >= checked(response.Width * 4));
        Assert.Equal(checked((long)response.Stride * response.Height), payload.LongLength);
    }

    private static void AssertExpectedInspectMetadata(
        CodecCorpusExpected expected,
        CodecResponse response)
    {
        Assert.Equal(expected.PageCount!.Value, response.PageCount);
        Assert.Equal(expected.NativeWidth!.Value, response.NativeWidth);
        Assert.Equal(expected.NativeHeight!.Value, response.NativeHeight);
    }

    private static void AssertExpectedDecodeMetadata(
        CodecCorpusExpected expected,
        CodecCorpusGolden golden,
        CodecResponse response)
    {
        Assert.Equal(expected.PageCount!.Value, response.PageCount);
        Assert.Equal(golden.NativeWidth!.Value, response.NativeWidth);
        Assert.Equal(golden.NativeHeight!.Value, response.NativeHeight);
    }

    private static DecodedFrame CreateFrame(CodecResponse response, byte[] payload) => new(
        payload,
        response.Width,
        response.Height,
        response.Stride,
        hasAlpha: true);

    private void WriteOutcome(
        CodecCorpusSample sample,
        string operation,
        CodecResponse response,
        CodecHostGateResult result) =>
        output.WriteLine(
            "{0} operation={1} result={2} endToEndMs={3:F1} processMs={4:F1} " +
            "peakWorkingSetMiB={5:F1} peakCommitMiB={6:F1}",
            sample.Id,
            operation,
            response.Result,
            result.EndToEndElapsed.TotalMilliseconds,
            result.Process.Elapsed.TotalMilliseconds,
            result.Process.PeakWorkingSetBytes / 1024d / 1024d,
            result.Process.PeakCommitBytes / 1024d / 1024d);

    private static async Task<byte[]> ReadBoundedAsync(string path)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        Assert.InRange(stream.Length, 1, CodecHostGateTestClient.Limits.MaxInputBytes);
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes);
        Assert.Equal(-1, stream.ReadByte());
        return bytes;
    }

    public sealed class ExternalCodecCorpusFactAttribute : FactAttribute
    {
        public ExternalCodecCorpusFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(RunGateVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = $"Set {RunGateVariable}=1 and configure the PDF/PSD corpus to run this gate.";
            }
        }
    }
}
