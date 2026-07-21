using EzyImageViewer.CodecProtocol;
using Xunit;
using Xunit.Abstractions;

namespace EzyImageViewer.Tests.Codec;

public sealed class CodecPerformanceGateTests(ITestOutputHelper output)
{
    private const string RunGateVariable = "EZYIMAGEVIEWER_RUN_CODEC_PERFORMANCE";
    private const int DecodeTargetDimension = 1024;
    private const long MaxPeakWorkingSetBytes = 512L * 1024 * 1024;
    private const long MaxPeakCommitBytes = 768L * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly DecodeShapeCase[] MeasuredPdfPages =
    [
        new(0, 1920, 1080, 1024, 576),
        new(4, 1600, 1200, 1024, 768),
        new(8, 1200, 1600, 768, 1024),
    ];

    [CodecPerformanceFact]
    [Trait("Category", "Performance")]
    public async Task SyntheticPdfAndPsd_MeetExactShapeTimeAndPeakMemoryContracts()
    {
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            AppContext.BaseDirectory,
            StringComparison.OrdinalIgnoreCase);

        var pdf = CodecSyntheticDocumentFactory.BuildPdf(
        [
            (1920, 1080),
            (1900, 1100),
            (1800, 1200),
            (1700, 1300),
            (1600, 1200),
            (1500, 1300),
            (1400, 1400),
            (1300, 1500),
            (1200, 1600),
        ]);
        _ = AssertSuccess(
            await DecodeAsync(pdf, CodecFormat.Pdf, pageIndex: 0),
            CodecFormat.Pdf,
            MeasuredPdfPages[0],
            pageCount: 9,
            expectedDiagnostic: "pdf-page");
        var pdfMeasurements = new List<Measurement>();
        for (var repetition = 0; repetition < 3; repetition++)
        {
            foreach (var page in MeasuredPdfPages)
            {
                var result = await DecodeAsync(pdf, CodecFormat.Pdf, page.PageIndex);
                _ = AssertSuccess(
                    result,
                    CodecFormat.Pdf,
                    page,
                    pageCount: 9,
                    expectedDiagnostic: "pdf-page");
                pdfMeasurements.Add(new(
                    page.PageIndex,
                    result.EndToEndElapsed.TotalMilliseconds,
                    result.Process.Elapsed.TotalMilliseconds,
                    result.Process.PeakWorkingSetBytes,
                    result.Process.PeakCommitBytes));
            }
        }

        foreach (var page in MeasuredPdfPages)
        {
            var measurements = pdfMeasurements
                .Where(item => item.PageIndex == page.PageIndex)
                .ToArray();
            output.WriteLine(
                "PDF synthetic page={0} native={1}x{2} output={3}x{4} " +
                "endToEndMedianMs={5:F1} processSamplesMs=[{6}] " +
                "peakWorkingSetMiB={7:F1} peakCommitMiB={8:F1}",
                page.PageIndex,
                page.NativeWidth,
                page.NativeHeight,
                page.OutputWidth,
                page.OutputHeight,
                Median(measurements.Select(item => item.EndToEndElapsedMilliseconds)),
                string.Join(
                    ",",
                    measurements.Select(item =>
                        item.ProcessElapsedMilliseconds.ToString("F1"))),
                BytesToMiB(measurements.Max(item => item.PeakWorkingSetBytes)),
                BytesToMiB(measurements.Max(item => item.PeakCommitBytes)));
        }

        var firstPageMedian = Median(
            pdfMeasurements
                .Where(item => item.PageIndex == 0)
                .Select(item => item.EndToEndElapsedMilliseconds));
        output.WriteLine(
            "PDF synthetic NFR-PERF-003 targetEndToEndMs=2000 measuredMedianMs={0:F1} meetsTarget={1}",
            firstPageMedian,
            firstPageMedian <= 2_000);
        Assert.True(
            firstPageMedian <= 2_000,
            $"Synthetic PDF first-page median {firstPageMedian:F1}ms exceeded the 2,000ms target.");

        var psd = CodecSyntheticDocumentFactory.BuildRgbPsd(2048, 1536);
        var psdPage = new DecodeShapeCase(0, 2048, 1536, 1024, 768);
        _ = AssertSuccess(
            await DecodeAsync(psd, CodecFormat.Psd, pageIndex: 0),
            CodecFormat.Psd,
            psdPage,
            pageCount: 1,
            expectedDiagnostic: "psd-composite");
        var psdMeasurements = new List<Measurement>();
        for (var repetition = 0; repetition < 3; repetition++)
        {
            var result = await DecodeAsync(psd, CodecFormat.Psd, pageIndex: 0);
            _ = AssertSuccess(
                result,
                CodecFormat.Psd,
                psdPage,
                pageCount: 1,
                expectedDiagnostic: "psd-composite");
            psdMeasurements.Add(new(
                0,
                result.EndToEndElapsed.TotalMilliseconds,
                result.Process.Elapsed.TotalMilliseconds,
                result.Process.PeakWorkingSetBytes,
                result.Process.PeakCommitBytes));
        }
        output.WriteLine(
            "PSD synthetic composite native=2048x1536 output=1024x768 " +
            "endToEndMedianMs={0:F1} processSamplesMs=[{1}] " +
            "peakWorkingSetMiB={2:F1} peakCommitMiB={3:F1}",
            Median(psdMeasurements.Select(item => item.EndToEndElapsedMilliseconds)),
            string.Join(
                ",",
                psdMeasurements.Select(item =>
                    item.ProcessElapsedMilliseconds.ToString("F1"))),
            BytesToMiB(psdMeasurements.Max(item => item.PeakWorkingSetBytes)),
            BytesToMiB(psdMeasurements.Max(item => item.PeakCommitBytes)));
        output.WriteLine(
            "Synthetic gate excludes physical-4K viewport and Photoshop-authored fidelity claims.");
    }

    private static Task<CodecHostGateResult> DecodeAsync(
        byte[] input,
        CodecFormat format,
        int pageIndex) => CodecHostGateTestClient.RunAsync(
        CodecHostTestClient.Request(
            CodecOperation.Decode,
            format,
            input.Length,
            pageIndex,
            targetWidth: DecodeTargetDimension,
            targetHeight: DecodeTargetDimension),
        input,
        RequestTimeout);

    private static CodecResponse AssertSuccess(
        CodecHostGateResult result,
        CodecFormat format,
        DecodeShapeCase page,
        int pageCount,
        string expectedDiagnostic)
    {
        Assert.True(
            result.Process.ExitCode == 0,
            $"exit={result.Process.ExitCode}, stderr={result.Process.StandardError}");
        Assert.Equal(string.Empty, result.Process.StandardError);
        Assert.True(result.EndToEndElapsed >= result.Process.Elapsed);
        var response = Assert.IsType<CodecResponse>(result.Response);
        Assert.Equal(CodecOperation.Decode, response.Operation);
        Assert.Equal(format, response.Format);
        Assert.Equal(CodecResultCode.Success, response.Result);
        Assert.Equal(expectedDiagnostic, response.Diagnostic);
        Assert.Equal(page.NativeWidth, response.NativeWidth);
        Assert.Equal(page.NativeHeight, response.NativeHeight);
        Assert.Equal(pageCount, response.PageCount);
        Assert.Equal(page.OutputWidth, response.Width);
        Assert.Equal(page.OutputHeight, response.Height);
        Assert.Equal(checked(response.Width * 4), response.Stride);
        Assert.Equal(checked((long)response.Stride * response.Height), result.Payload.LongLength);
        Assert.Equal(result.Payload.LongLength, response.PayloadLength);
        Assert.InRange(result.Process.PeakWorkingSetBytes, 1, MaxPeakWorkingSetBytes);
        Assert.InRange(result.Process.PeakCommitBytes, 1, MaxPeakCommitBytes);
        return response;
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

    private static double BytesToMiB(long value) => value / 1024d / 1024d;

    private sealed record Measurement(
        int PageIndex,
        double EndToEndElapsedMilliseconds,
        double ProcessElapsedMilliseconds,
        long PeakWorkingSetBytes,
        long PeakCommitBytes);

    private sealed record DecodeShapeCase(
        int PageIndex,
        int NativeWidth,
        int NativeHeight,
        int OutputWidth,
        int OutputHeight);

    public sealed class CodecPerformanceFactAttribute : FactAttribute
    {
        public CodecPerformanceFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(RunGateVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = $"Set {RunGateVariable}=1 to run the synthetic codec performance gate.";
            }
        }
    }
}
