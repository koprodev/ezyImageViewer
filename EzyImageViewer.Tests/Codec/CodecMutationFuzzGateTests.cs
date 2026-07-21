using System.Text;
using EzyImageViewer.CodecProtocol;
using Xunit;
using Xunit.Abstractions;

namespace EzyImageViewer.Tests.Codec;

public sealed class CodecMutationFuzzGateTests(ITestOutputHelper output)
{
    private const string RunGateVariable = "EZYIMAGEVIEWER_RUN_CODEC_MUTATION_FUZZ";
    private const int DecodeTargetDimension = 64;
    private const long MaxPeakProcessMemoryBytes = 1024L * 1024 * 1024;
    private static readonly TimeSpan CaseTimeout = TimeSpan.FromSeconds(5);
    private static readonly CodecResultCode[] AllowedResults =
    [
        CodecResultCode.Success,
        CodecResultCode.InvalidRequest,
        CodecResultCode.CorruptInput,
        CodecResultCode.PasswordRequired,
        CodecResultCode.ResourceLimitExceeded,
    ];

    [CodecMutationFuzzFact]
    [Trait("Category", "MutationFuzz")]
    public async Task DeterministicPdfAndPsdMutations_NeverCrashHangOrEscapeStructuredResponse()
    {
        Assert.Contains(
            $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}",
            AppContext.BaseDirectory,
            StringComparison.OrdinalIgnoreCase);

        var seeds = new (CodecFormat Format, byte[] Bytes)[]
        {
            (CodecFormat.Pdf, CodecSyntheticDocumentFactory.BuildPdf(3, width: 640, height: 480)),
            (CodecFormat.Psd, CodecSyntheticDocumentFactory.BuildRgbPsd(32, 24)),
        };

        foreach (var seed in seeds)
        {
            var resultCounts =
                new Dictionary<(CodecOperation Operation, CodecResultCode Result), int>();
            var mutations = CreateMutations(seed.Bytes);
            foreach (var mutation in mutations)
            {
                foreach (var operation in new[]
                         {
                             CodecOperation.Inspect,
                             CodecOperation.Decode,
                         })
                {
                    var request = CodecHostTestClient.Request(
                        operation,
                        seed.Format,
                        mutation.Bytes.Length,
                        pageIndex: operation == CodecOperation.Decode ? 0 : -1,
                        targetWidth: operation == CodecOperation.Decode
                            ? DecodeTargetDimension
                            : 0,
                        targetHeight: operation == CodecOperation.Decode
                            ? DecodeTargetDimension
                            : 0);
                    var result = await CodecHostGateTestClient.RunAsync(
                        request,
                        mutation.Bytes,
                        CaseTimeout);
                    var response = AssertStructuredResponse(
                        seed.Format,
                        mutation.Name,
                        operation,
                        result);
                    var key = (operation, response.Result);
                    resultCounts[key] = resultCounts.GetValueOrDefault(key) + 1;
                }
            }

            output.WriteLine(
                "{0} deterministic mutations={1}, boundedOperations={2}, results=[{3}]",
                seed.Format,
                mutations.Count,
                mutations.Count * 2,
                string.Join(
                    ", ",
                    resultCounts
                        .OrderBy(static pair => pair.Key.Operation)
                        .ThenBy(static pair => pair.Key.Result)
                        .Select(static pair =>
                            $"{pair.Key.Operation}/{pair.Key.Result}:{pair.Value}")));
        }
    }

    private static CodecResponse AssertStructuredResponse(
        CodecFormat format,
        string mutation,
        CodecOperation operation,
        CodecHostGateResult result)
    {
        Assert.True(
            result.Process.ExitCode == 0,
            $"format={format}, mutation={mutation}, operation={operation}, " +
            $"exit={result.Process.ExitCode}, stderr={result.Process.StandardError}");
        Assert.Equal(string.Empty, result.Process.StandardError);
        Assert.True(result.EndToEndElapsed > TimeSpan.Zero);
        Assert.InRange(
            result.Process.PeakWorkingSetBytes,
            1,
            MaxPeakProcessMemoryBytes);
        Assert.InRange(
            result.Process.PeakCommitBytes,
            1,
            MaxPeakProcessMemoryBytes);

        var response = Assert.IsType<CodecResponse>(result.Response);
        Assert.Contains(response.Result, AllowedResults);
        Assert.InRange(
            Encoding.UTF8.GetByteCount(response.Diagnostic ?? string.Empty),
            1,
            CodecHostGateTestClient.Limits.MaxDiagnosticBytes);
        Assert.Equal(response.PayloadLength, result.Payload.LongLength);

        if (response.Result != CodecResultCode.Success)
        {
            Assert.Empty(result.Payload);
            Assert.Equal(0, response.Width);
            Assert.Equal(0, response.Height);
            Assert.Equal(0, response.Stride);
            Assert.Equal(0, response.NativeWidth);
            Assert.Equal(0, response.NativeHeight);
            Assert.Equal(0, response.PageCount);
            return response;
        }

        Assert.InRange(
            response.NativeWidth,
            1,
            CodecHostGateTestClient.Limits.MaxDimension);
        Assert.InRange(
            response.NativeHeight,
            1,
            CodecHostGateTestClient.Limits.MaxDimension);
        Assert.InRange(
            response.PageCount,
            1,
            CodecHostGateTestClient.Limits.MaxPageCount);
        if (operation == CodecOperation.Inspect)
        {
            Assert.Equal(0, response.Width);
            Assert.Equal(0, response.Height);
            Assert.Equal(0, response.Stride);
            Assert.Empty(result.Payload);
            return response;
        }

        Assert.InRange(response.Width, 1, DecodeTargetDimension);
        Assert.InRange(response.Height, 1, DecodeTargetDimension);
        Assert.Equal(checked(response.Width * 4), response.Stride);
        Assert.Equal(
            checked((long)response.Stride * response.Height),
            result.Payload.LongLength);
        Assert.InRange(
            result.Payload.LongLength,
            4,
            checked((long)DecodeTargetDimension * DecodeTargetDimension * 4));
        return response;
    }

    private static IReadOnlyList<MutationCase> CreateMutations(byte[] seed)
    {
        var cases = new List<MutationCase>();
        var truncationLengths = new[]
        {
            1,
            4,
            8,
            25,
            seed.Length / 2,
            seed.Length - 1,
        };
        foreach (var length in truncationLengths.Distinct().Order())
        {
            if (length < 0 || length >= seed.Length)
                continue;
            cases.Add(new($"truncate-{length}", seed[..length]));
        }

        for (var index = 0; index < 16; index++)
        {
            var bytes = seed.ToArray();
            var position = (int)((long)index * (bytes.Length - 1) / 15);
            var mask = checked((byte)(1 << (index % 8)));
            bytes[position] ^= mask;
            cases.Add(new($"bitflip-{position}-{mask:X2}", bytes));
        }

        for (var index = 0; index < 6; index++)
        {
            var bytes = seed.ToArray();
            var position = (index * 83 + bytes.Length / 7) % bytes.Length;
            var length = Math.Min(1 << (index % 3), bytes.Length - position);
            bytes.AsSpan(position, length).Clear();
            cases.Add(new($"zero-{position}-{length}", bytes));
        }

        for (var index = 0; index < 2; index++)
        {
            var appended = new byte[index == 0 ? 1 : 16];
            for (var offset = 0; offset < appended.Length; offset++)
                appended[offset] = (byte)((0xA5 ^ (offset * 17 + index)) & 0xFF);
            cases.Add(new(
                $"append-{appended.Length}",
                [.. seed, .. appended]));
        }

        return cases;
    }

    private sealed record MutationCase(string Name, byte[] Bytes);

    public sealed class CodecMutationFuzzFactAttribute : FactAttribute
    {
        public CodecMutationFuzzFactAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(RunGateVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = $"Set {RunGateVariable}=1 to run the deterministic mutation-fuzz gate.";
            }
        }
    }
}
