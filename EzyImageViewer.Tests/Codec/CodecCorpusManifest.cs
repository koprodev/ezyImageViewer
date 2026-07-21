using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EzyImageViewer.CodecProtocol;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Imaging.Skia;
using Xunit;

namespace EzyImageViewer.Tests.Codec;

internal enum CodecCorpusSampleKind
{
    Normal,
    Large,
    Boundary,
    Corrupt,
    Security,
}

internal enum CodecCorpusScenario
{
    PdfSinglePage,
    PdfMultiPage,
    PdfEncrypted,
    PdfTransparency,
    PdfFontsEmbedded,
    PdfFontsMissing,
    PdfCorruptStructure,
    PdfCompressionBomb,
    PdfSlowRenderCancellation,
    PsdSingleLayer,
    PsdMultiLayer,
    PsdCompatibilityOn,
    PsdCompatibilityOff,
    PsdCmyk,
    PsdLab,
    PsdSpotColor,
    PsdSmartObject,
    PsdAbnormalLength,
    PsdCompressionBomb,
    Icc,
    Alpha,
}

internal enum CodecCorpusHostResult
{
    Success,
    InvalidRequest,
    CorruptInput,
    PasswordRequired,
    ResourceLimitExceeded,
    AccessDenied,
}

internal enum CodecCorpusProductOutcome
{
    Success,
    Canceled,
    CorruptFile,
    CredentialsOrPermissionRequired,
    ResourceOrSecurityLimitExceeded,
}

internal enum CodecCorpusColorSpace
{
    Srgb,
}

internal enum CodecCorpusAlphaMode
{
    PremultipliedBgra8,
}

internal sealed record CodecCorpusManifest(
    [property: JsonPropertyName("$schema")] string? Schema,
    int SchemaVersion,
    int MinimumNormalPerExtension,
    IReadOnlyList<CodecCorpusFormat> Formats);

internal sealed record CodecCorpusFormat(
    string Extension,
    string Format,
    string SupportTier,
    IReadOnlyList<CodecCorpusSample> Samples);

internal sealed record CodecCorpusSample(
    string Path,
    CodecCorpusSampleKind Kind,
    string Source,
    string License,
    string Sha256,
    string? GoldenPath,
    string? GoldenSha256,
    string? Id,
    IReadOnlyList<CodecCorpusScenario>? Scenarios,
    CodecCorpusProducer? Producer,
    CodecCorpusExpected? Expected,
    IReadOnlyList<CodecCorpusGolden>? Goldens,
    string? Password,
    CodecCorpusQualificationPerformanceBudget? QualificationPerformanceBudget);

internal sealed record CodecCorpusProducer(
    string Name,
    string Version,
    string Platform);

internal sealed record CodecCorpusExpected(
    CodecCorpusHostResult? InspectResult,
    CodecCorpusHostResult? DecodeResult,
    CodecCorpusProductOutcome? ProductOutcome,
    int? PageCount,
    int? NativeWidth,
    int? NativeHeight,
    int? DecodePageIndex,
    int? DecodeTargetMaxDimension,
    int? CancellationAfterMilliseconds);

internal sealed record CodecCorpusGolden(
    int? PageIndex,
    int? TargetMaxDimension,
    int? NativeWidth,
    int? NativeHeight,
    string Path,
    string Sha256,
    CodecCorpusReferenceRenderer? ReferenceRenderer,
    CodecCorpusColorSpace? ColorSpace,
    CodecCorpusAlphaMode? AlphaMode,
    CodecCorpusPixelTolerance? Tolerance);

internal sealed record CodecCorpusReferenceRenderer(
    string Name,
    string Version);

internal sealed record CodecCorpusPixelTolerance(
    int? MaxChannelDelta,
    int? MaxAlphaDelta,
    int? ChangedPixelDelta,
    double? MaxChangedPixelRatio,
    double? MaxMeanAbsoluteError);

internal sealed record CodecCorpusQualificationPerformanceBudget(
    int Repetitions,
    int MaxMedianDecodeElapsedMilliseconds,
    long MaxPeakWorkingSetBytes,
    long MaxPeakCommitBytes);

internal static class CodecCorpusManifestSerializer
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static CodecCorpusManifest ReadTrackedManifest()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Formats",
            "corpus-manifest.json");
        using var stream = File.OpenRead(path);
        return Deserialize(stream);
    }

    public static CodecCorpusManifest Deserialize(string json) =>
        JsonSerializer.Deserialize<CodecCorpusManifest>(json, Options)
        ?? throw new InvalidDataException("The format corpus manifest is empty.");

    private static CodecCorpusManifest Deserialize(Stream stream) =>
        JsonSerializer.Deserialize<CodecCorpusManifest>(stream, Options)
        ?? throw new InvalidDataException("The format corpus manifest is empty.");

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        };
        options.Converters.Add(new JsonStringEnumConverter(
            JsonNamingPolicy.CamelCase,
            allowIntegerValues: false));
        return options;
    }
}

internal static class CodecCorpusManifestValidator
{
    private const int SchemaVersion = 2;
    internal const int MaxGoldenChannelDelta = 64;
    internal const int MaxGoldenAlphaDelta = 64;
    internal const int MaxGoldenChangedPixelDelta = 16;
    internal const double MaxGoldenChangedPixelRatio = 0.10;
    internal const double MaxGoldenMeanAbsoluteError = 4.0;
    internal const int MaxQualificationRepetitions = 10;
    internal const int MaxQualificationMedianDecodeElapsedMilliseconds = 120_000;
    internal const long MaxQualificationPeakMemoryBytes = 1024L * 1024 * 1024;
    private static readonly Regex Sha256Pattern = new(
        "^[A-Fa-f0-9]{64}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ExtensionPattern = new(
        "^\\.[a-z0-9]+$",
        RegexOptions.CultureInvariant);
    private static readonly HashSet<string> KnownFormats = new(
        ["Jpeg", "Png", "Bmp", "Gif", "Tiff", "Ico", "WebP", "Avif", "Heif", "Pdf", "Svg", "Psd"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> KnownSupportTiers = new(
        ["official", "limited", "conditional"],
        StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, CodecCorpusScenario[]> RequiredScenarios =
        new Dictionary<string, CodecCorpusScenario[]>(StringComparer.Ordinal)
        {
            ["Pdf"] =
            [
                CodecCorpusScenario.PdfSinglePage,
                CodecCorpusScenario.PdfMultiPage,
                CodecCorpusScenario.PdfEncrypted,
                CodecCorpusScenario.PdfTransparency,
                CodecCorpusScenario.PdfFontsEmbedded,
                CodecCorpusScenario.PdfFontsMissing,
                CodecCorpusScenario.PdfCorruptStructure,
                CodecCorpusScenario.PdfCompressionBomb,
                CodecCorpusScenario.PdfSlowRenderCancellation,
                CodecCorpusScenario.Icc,
                CodecCorpusScenario.Alpha,
            ],
            ["Psd"] =
            [
                CodecCorpusScenario.PsdSingleLayer,
                CodecCorpusScenario.PsdMultiLayer,
                CodecCorpusScenario.PsdCompatibilityOn,
                CodecCorpusScenario.PsdCompatibilityOff,
                CodecCorpusScenario.PsdCmyk,
                CodecCorpusScenario.PsdLab,
                CodecCorpusScenario.PsdSpotColor,
                CodecCorpusScenario.PsdSmartObject,
                CodecCorpusScenario.PsdAbnormalLength,
                CodecCorpusScenario.PsdCompressionBomb,
                CodecCorpusScenario.Icc,
                CodecCorpusScenario.Alpha,
            ],
        };

    public static void ValidateStructure(CodecCorpusManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion != SchemaVersion)
        {
            throw new InvalidDataException(
                $"Corpus schemaVersion must be {SchemaVersion}; actual={manifest.SchemaVersion}.");
        }
        if (manifest.MinimumNormalPerExtension < 30)
            throw new InvalidDataException("minimumNormalPerExtension must be at least 30.");
        if (manifest.Formats is null)
            throw new InvalidDataException("formats is required.");

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sampleIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var format in manifest.Formats)
        {
            if (format is null)
                throw new InvalidDataException("formats cannot contain null entries.");
            if (string.IsNullOrWhiteSpace(format.Extension)
                || !ExtensionPattern.IsMatch(format.Extension))
            {
                throw new InvalidDataException(
                    "Every extension must match '^\\.[a-z0-9]+$'.");
            }
            if (!extensions.Add(format.Extension))
                throw new InvalidDataException($"Duplicate extension '{format.Extension}'.");
            if (!KnownFormats.Contains(format.Format))
                throw new InvalidDataException($"Unknown format '{format.Format}'.");
            if ((format.Format == "Pdf"
                    && (format.Extension != ".pdf" || format.SupportTier != "conditional"))
                || (format.Format == "Psd"
                    && (format.Extension != ".psd" || format.SupportTier != "limited")))
            {
                throw new InvalidDataException(
                    $"Codec extension/supportTier does not match format '{format.Format}'.");
            }
            if (!KnownSupportTiers.Contains(format.SupportTier))
                throw new InvalidDataException($"Unknown supportTier '{format.SupportTier}'.");
            if (format.Samples is null)
                throw new InvalidDataException($"samples is required for '{format.Extension}'.");

            var samplePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sampleDigests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sample in format.Samples)
            {
                ValidateBaseSample(format, sample);
                var normalizedPath = NormalizeRelativePathForComparison(sample.Path);
                if (!samplePaths.Add(normalizedPath))
                {
                    throw new InvalidDataException(
                        $"Sample paths must be unique within '{format.Extension}': '{sample.Path}'.");
                }
                if (!sampleDigests.Add(sample.Sha256))
                {
                    throw new InvalidDataException(
                        $"Sample SHA-256 digests must be unique within '{format.Extension}': '{sample.Sha256}'.");
                }
                if (format.Format is "Pdf" or "Psd")
                    ValidateCodecSample(format, sample, sampleIds);
                else
                    ValidateGenericSample(format, sample);
            }
        }

        ValidateScenarioSemantics(manifest);
    }

    public static void ValidateCodecActivationCoverage(CodecCorpusManifest manifest)
    {
        ValidateStructure(manifest);
        foreach (var formatName in new[] { "Pdf", "Psd" })
        {
            var format = manifest.Formats.SingleOrDefault(candidate =>
                string.Equals(candidate.Format, formatName, StringComparison.Ordinal));
            if (format is null)
                throw new InvalidDataException($"The codec corpus must contain exactly one {formatName} format entry.");
            if (manifest.Formats.Count(candidate =>
                    string.Equals(candidate.Format, formatName, StringComparison.Ordinal)) != 1)
            {
                throw new InvalidDataException($"The codec corpus must contain exactly one {formatName} format entry.");
            }

            RequireKindCount(
                format,
                CodecCorpusSampleKind.Normal,
                manifest.MinimumNormalPerExtension);
            RequireKindCount(format, CodecCorpusSampleKind.Large, minimum: 1);
            RequireKindCount(format, CodecCorpusSampleKind.Boundary, minimum: 1);
            RequireKindCount(format, CodecCorpusSampleKind.Corrupt, minimum: 1);
            RequireKindCount(format, CodecCorpusSampleKind.Security, minimum: 1);
            var normalProducerCount = format.Samples
                .Where(sample => sample.Kind == CodecCorpusSampleKind.Normal)
                .Select(sample => sample.Producer!.Name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            if (normalProducerCount < 2)
            {
                throw new InvalidDataException(
                    $"{format.Extension} normal samples require at least two distinct producer names.");
            }

            var scenarios = format.Samples
                .SelectMany(sample => sample.Scenarios ?? [])
                .ToHashSet();
            foreach (var required in RequiredScenarios[formatName])
            {
                if (!scenarios.Contains(required))
                {
                    throw new InvalidDataException(
                        $"{format.Extension} is missing required scenario '{ToContractName(required)}'.");
                }
            }

            foreach (var sample in format.Samples.Where(sample =>
                         sample.Kind == CodecCorpusSampleKind.Normal))
            {
                RequireSuccessfulProductSample(sample, "normal");
            }
            if (!format.Samples.Any(sample =>
                    sample.Kind == CodecCorpusSampleKind.Large
                    && IsSuccessfulProductSample(sample)
                    && sample.QualificationPerformanceBudget is not null))
            {
                throw new InvalidDataException(
                    $"{format.Extension} requires at least one successful large sample with goldens and a qualificationPerformanceBudget.");
            }
            foreach (var sample in format.Samples.Where(sample =>
                         sample.Kind is CodecCorpusSampleKind.Corrupt or CodecCorpusSampleKind.Security))
            {
                var expected = sample.Expected!;
                if (expected.ProductOutcome == CodecCorpusProductOutcome.Success
                    || (expected.InspectResult == CodecCorpusHostResult.Success
                        && expected.DecodeResult == CodecCorpusHostResult.Success))
                {
                    throw new InvalidDataException(
                        $"Refusal sample '{sample.Id}' must declare an exact non-success Host and product outcome.");
                }
            }
        }
    }

    public static CodecFormat ToProtocolFormat(CodecCorpusFormat format) => format.Format switch
    {
        "Pdf" => CodecFormat.Pdf,
        "Psd" => CodecFormat.Psd,
        _ => throw new InvalidDataException($"'{format.Format}' is not an isolated codec format."),
    };

    public static CodecResultCode ToProtocolResult(CodecCorpusHostResult result) => result switch
    {
        CodecCorpusHostResult.Success => CodecResultCode.Success,
        CodecCorpusHostResult.InvalidRequest => CodecResultCode.InvalidRequest,
        CodecCorpusHostResult.CorruptInput => CodecResultCode.CorruptInput,
        CodecCorpusHostResult.PasswordRequired => CodecResultCode.PasswordRequired,
        CodecCorpusHostResult.ResourceLimitExceeded => CodecResultCode.ResourceLimitExceeded,
        CodecCorpusHostResult.AccessDenied => CodecResultCode.AccessDenied,
        _ => throw new InvalidDataException($"Unmapped corpus Host result '{result}'."),
    };

    private static void ValidateBaseSample(
        CodecCorpusFormat format,
        CodecCorpusSample sample)
    {
        if (sample is null)
            throw new InvalidDataException($"{format.Extension} samples cannot contain null entries.");
        ValidateRelativePath(sample.Path, "sample path");
        RequireText(sample.Source, $"source for '{sample.Path}'");
        RequireText(sample.License, $"license for '{sample.Path}'");
        ValidateSha256(sample.Sha256, $"sha256 for '{sample.Path}'");
        if ((sample.GoldenPath is null) != (sample.GoldenSha256 is null))
        {
            throw new InvalidDataException(
                $"Legacy goldenPath and goldenSha256 must both be present or absent for '{sample.Path}'.");
        }
        if (sample.GoldenPath is not null)
        {
            ValidateRelativePath(sample.GoldenPath, "legacy golden path");
            ValidateSha256(sample.GoldenSha256!, $"legacy goldenSha256 for '{sample.Path}'");
        }
    }

    private static void ValidateGenericSample(
        CodecCorpusFormat format,
        CodecCorpusSample sample)
    {
        if (sample.Id is not null
            || sample.Scenarios is not null
            || sample.Producer is not null
            || sample.Expected is not null
            || sample.Goldens is not null
            || sample.Password is not null
            || sample.QualificationPerformanceBudget is not null)
        {
            throw new InvalidDataException(
                $"Generic sample '{sample.Path}' for '{format.Extension}' cannot declare codec-only fields.");
        }
    }

    private static void ValidateCodecSample(
        CodecCorpusFormat format,
        CodecCorpusSample sample,
        HashSet<string> sampleIds)
    {
        RequireText(sample.Id, $"id for '{sample.Path}'");
        if (!sampleIds.Add(sample.Id!))
            throw new InvalidDataException($"Duplicate codec sample id '{sample.Id}'.");
        if (sample.Scenarios is null || sample.Scenarios.Count == 0)
            throw new InvalidDataException($"scenarios is required for codec sample '{sample.Id}'.");
        if (sample.Scenarios.Distinct().Count() != sample.Scenarios.Count)
            throw new InvalidDataException($"scenarios must be unique for codec sample '{sample.Id}'.");
        var allowedPrefix = format.Format == "Pdf" ? "Pdf" : "Psd";
        foreach (var scenario in sample.Scenarios)
        {
            var name = scenario.ToString();
            if (!name.StartsWith(allowedPrefix, StringComparison.Ordinal)
                && scenario is not CodecCorpusScenario.Icc and not CodecCorpusScenario.Alpha)
            {
                throw new InvalidDataException(
                    $"Scenario '{ToContractName(scenario)}' does not belong to {format.Format} sample '{sample.Id}'.");
            }
        }

        var producer = sample.Producer
            ?? throw new InvalidDataException($"producer is required for codec sample '{sample.Id}'.");
        RequireText(producer.Name, $"producer.name for '{sample.Id}'");
        RequireText(producer.Version, $"producer.version for '{sample.Id}'");
        RequireText(producer.Platform, $"producer.platform for '{sample.Id}'");

        var expected = sample.Expected
            ?? throw new InvalidDataException($"expected is required for codec sample '{sample.Id}'.");
        if (expected.InspectResult is null)
            throw new InvalidDataException($"expected.inspectResult is required for '{sample.Id}'.");
        if (expected.ProductOutcome is null)
            throw new InvalidDataException($"expected.productOutcome is required for '{sample.Id}'.");

        if (expected.InspectResult == CodecCorpusHostResult.Success)
        {
            RequirePositive(expected.PageCount, "pageCount", sample.Id!);
            RequirePositive(expected.NativeWidth, "nativeWidth", sample.Id!);
            RequirePositive(expected.NativeHeight, "nativeHeight", sample.Id!);
            if (expected.DecodeResult is null)
                throw new InvalidDataException($"expected.decodeResult is required for '{sample.Id}'.");
            if (expected.DecodePageIndex is null || expected.DecodePageIndex < 0
                || expected.DecodePageIndex >= expected.PageCount)
            {
                throw new InvalidDataException($"expected.decodePageIndex is invalid for '{sample.Id}'.");
            }
            if (expected.DecodeTargetMaxDimension is null
                || expected.DecodeTargetMaxDimension < 0
                || expected.DecodeTargetMaxDimension > CodecHostGateTestClient.Limits.MaxDimension)
            {
                throw new InvalidDataException(
                    $"expected.decodeTargetMaxDimension is invalid for '{sample.Id}'.");
            }
        }
        else
        {
            if (expected.DecodeResult is not null
                || expected.PageCount is not null
                || expected.NativeWidth is not null
                || expected.NativeHeight is not null
                || expected.DecodePageIndex is not null
                || expected.DecodeTargetMaxDimension is not null)
            {
                throw new InvalidDataException(
                    $"Inspect-refused sample '{sample.Id}' cannot declare decode or dimension expectations.");
            }
        }

        var requiresPassword = expected.InspectResult == CodecCorpusHostResult.PasswordRequired
            || expected.DecodeResult == CodecCorpusHostResult.PasswordRequired;
        if (requiresPassword)
            RequireText(sample.Password, $"password for '{sample.Id}'");
        else if (sample.Password is not null)
            throw new InvalidDataException($"password is only valid for password-refused sample '{sample.Id}'.");

        ValidateProductOutcome(sample);
        if (sample.GoldenPath is not null || sample.GoldenSha256 is not null)
        {
            throw new InvalidDataException(
                $"Codec sample '{sample.Id}' must use goldens instead of legacy goldenPath fields.");
        }
        if (sample.Goldens is null)
            throw new InvalidDataException($"goldens is required for codec sample '{sample.Id}'.");
        var goldenKeys = new HashSet<(int? PageIndex, int? TargetMaxDimension)>();
        foreach (var golden in sample.Goldens)
        {
            ValidateGolden(format, sample, golden);
            if (!goldenKeys.Add((golden.PageIndex, golden.TargetMaxDimension)))
            {
                throw new InvalidDataException(
                    $"Duplicate golden page/target pair for codec sample '{sample.Id}'.");
            }
        }
        if (expected.DecodeResult == CodecCorpusHostResult.Success
            && !sample.Goldens.Any(golden =>
                golden.PageIndex == expected.DecodePageIndex
                && golden.TargetMaxDimension == expected.DecodeTargetMaxDimension))
        {
            throw new InvalidDataException(
                $"Successful Host baseline for '{sample.Id}' requires a golden with the same page and target.");
        }
        ValidateQualificationPerformanceBudget(sample);
    }

    private static void ValidateQualificationPerformanceBudget(CodecCorpusSample sample)
    {
        var budget = sample.QualificationPerformanceBudget;
        if (budget is null)
            return;
        if (!IsSuccessfulProductSample(sample))
        {
            throw new InvalidDataException(
                $"qualificationPerformanceBudget requires exact Host/product success and a golden for '{sample.Id}'.");
        }
        if (budget.Repetitions is < 3 or > MaxQualificationRepetitions)
        {
            throw new InvalidDataException(
                $"qualificationPerformanceBudget.repetitions is outside [3, {MaxQualificationRepetitions}] for '{sample.Id}'.");
        }
        if (budget.MaxMedianDecodeElapsedMilliseconds is < 1
            or > MaxQualificationMedianDecodeElapsedMilliseconds)
        {
            throw new InvalidDataException(
                $"qualificationPerformanceBudget.maxMedianDecodeElapsedMilliseconds is outside [1, {MaxQualificationMedianDecodeElapsedMilliseconds}] for '{sample.Id}'.");
        }
        if (budget.MaxPeakWorkingSetBytes is < 1
            or > MaxQualificationPeakMemoryBytes)
        {
            throw new InvalidDataException(
                $"qualificationPerformanceBudget.maxPeakWorkingSetBytes is outside [1, {MaxQualificationPeakMemoryBytes}] for '{sample.Id}'.");
        }
        if (budget.MaxPeakCommitBytes is < 1
            or > MaxQualificationPeakMemoryBytes)
        {
            throw new InvalidDataException(
                $"qualificationPerformanceBudget.maxPeakCommitBytes is outside [1, {MaxQualificationPeakMemoryBytes}] for '{sample.Id}'.");
        }
    }

    private static void ValidateProductOutcome(CodecCorpusSample sample)
    {
        var expected = sample.Expected!;
        var hostResults = new[] { expected.InspectResult, expected.DecodeResult }
            .Where(result => result is not null)
            .Select(result => result!.Value)
            .ToHashSet();
        var valid = expected.ProductOutcome switch
        {
            CodecCorpusProductOutcome.Success =>
                expected.InspectResult == CodecCorpusHostResult.Success
                && expected.DecodeResult == CodecCorpusHostResult.Success,
            CodecCorpusProductOutcome.Canceled =>
                expected.InspectResult == CodecCorpusHostResult.Success
                && expected.DecodeResult == CodecCorpusHostResult.Success
                && sample.Scenarios!.Contains(CodecCorpusScenario.PdfSlowRenderCancellation),
            CodecCorpusProductOutcome.CorruptFile =>
                hostResults.Contains(CodecCorpusHostResult.CorruptInput)
                || hostResults.Contains(CodecCorpusHostResult.InvalidRequest),
            CodecCorpusProductOutcome.CredentialsOrPermissionRequired =>
                hostResults.Contains(CodecCorpusHostResult.PasswordRequired)
                || hostResults.Contains(CodecCorpusHostResult.AccessDenied),
            CodecCorpusProductOutcome.ResourceOrSecurityLimitExceeded =>
                hostResults.Contains(CodecCorpusHostResult.ResourceLimitExceeded),
            _ => false,
        };
        if (!valid)
        {
            throw new InvalidDataException(
                $"Product outcome '{expected.ProductOutcome}' is not mapped by Host expectations for '{sample.Id}'.");
        }
        if (expected.ProductOutcome == CodecCorpusProductOutcome.Canceled)
        {
            RequireRange(
                expected.CancellationAfterMilliseconds,
                1,
                5_000,
                "cancellationAfterMilliseconds",
                sample.Id!);
        }
        else if (expected.CancellationAfterMilliseconds is not null)
        {
            throw new InvalidDataException(
                $"cancellationAfterMilliseconds is only valid for canceled product outcome '{sample.Id}'.");
        }
    }

    private static void ValidateGolden(
        CodecCorpusFormat format,
        CodecCorpusSample sample,
        CodecCorpusGolden golden)
    {
        if (golden is null)
            throw new InvalidDataException($"goldens cannot contain null for '{sample.Id}'.");
        if (golden.PageIndex is null || golden.PageIndex < 0
            || golden.PageIndex >= sample.Expected!.PageCount)
        {
            throw new InvalidDataException($"golden.pageIndex is invalid for '{sample.Id}'.");
        }
        if (golden.TargetMaxDimension is null
            || golden.TargetMaxDimension < 0
            || golden.TargetMaxDimension > CodecHostGateTestClient.Limits.MaxDimension)
        {
            throw new InvalidDataException($"golden.targetMaxDimension is invalid for '{sample.Id}'.");
        }
        RequireRange(
            golden.NativeWidth,
            1,
            CodecHostGateTestClient.Limits.MaxDimension,
            "golden.nativeWidth",
            sample.Id!);
        RequireRange(
            golden.NativeHeight,
            1,
            CodecHostGateTestClient.Limits.MaxDimension,
            "golden.nativeHeight",
            sample.Id!);
        if (golden.PageIndex == 0
            && (golden.NativeWidth != sample.Expected!.NativeWidth
                || golden.NativeHeight != sample.Expected.NativeHeight))
        {
            throw new InvalidDataException(
                $"Page-zero golden native dimensions must match inspect expectations for '{sample.Id}'.");
        }
        if (format.Format == "Psd" && golden.TargetMaxDimension != 0)
        {
            throw new InvalidDataException(
                $"PSD product goldens must use the full-size target 0 for '{sample.Id}'.");
        }
        ValidateRelativePath(golden.Path, "golden path");
        ValidateSha256(golden.Sha256, $"golden sha256 for '{sample.Id}'");
        var renderer = golden.ReferenceRenderer
            ?? throw new InvalidDataException($"golden.referenceRenderer is required for '{sample.Id}'.");
        RequireText(renderer.Name, $"referenceRenderer.name for '{sample.Id}'");
        RequireText(renderer.Version, $"referenceRenderer.version for '{sample.Id}'");
        if (golden.ColorSpace != CodecCorpusColorSpace.Srgb)
            throw new InvalidDataException($"golden.colorSpace must be srgb for '{sample.Id}'.");
        if (golden.AlphaMode != CodecCorpusAlphaMode.PremultipliedBgra8)
        {
            throw new InvalidDataException(
                $"golden.alphaMode must be premultipliedBgra8 for '{sample.Id}'.");
        }

        var tolerance = golden.Tolerance
            ?? throw new InvalidDataException($"golden.tolerance is required for '{sample.Id}'.");
        RequireRange(
            tolerance.MaxChannelDelta,
            0,
            MaxGoldenChannelDelta,
            "maxChannelDelta",
            sample.Id!);
        RequireRange(
            tolerance.MaxAlphaDelta,
            0,
            MaxGoldenAlphaDelta,
            "maxAlphaDelta",
            sample.Id!);
        RequireRange(
            tolerance.ChangedPixelDelta,
            0,
            MaxGoldenChangedPixelDelta,
            "changedPixelDelta",
            sample.Id!);
        RequireFiniteRange(
            tolerance.MaxChangedPixelRatio,
            0,
            MaxGoldenChangedPixelRatio,
            "maxChangedPixelRatio",
            sample.Id!);
        RequireFiniteRange(
            tolerance.MaxMeanAbsoluteError,
            0,
            MaxGoldenMeanAbsoluteError,
            "maxMeanAbsoluteError",
            sample.Id!);
    }

    private static void ValidateScenarioSemantics(CodecCorpusManifest manifest)
    {
        var samples = manifest.Formats
            .Where(format => format.Format is "Pdf" or "Psd")
            .SelectMany(format => format.Samples)
            .ToArray();
        foreach (var sample in samples)
        {
            foreach (var scenario in sample.Scenarios!)
            {
                switch (scenario)
                {
                    case CodecCorpusScenario.PdfSinglePage:
                    case CodecCorpusScenario.PdfMultiPage:
                    case CodecCorpusScenario.PdfTransparency:
                    case CodecCorpusScenario.PdfFontsEmbedded:
                    case CodecCorpusScenario.PdfFontsMissing:
                    case CodecCorpusScenario.PsdSingleLayer:
                    case CodecCorpusScenario.PsdMultiLayer:
                    case CodecCorpusScenario.PsdCompatibilityOn:
                    case CodecCorpusScenario.PsdCompatibilityOff:
                    case CodecCorpusScenario.PsdCmyk:
                    case CodecCorpusScenario.PsdLab:
                    case CodecCorpusScenario.PsdSpotColor:
                    case CodecCorpusScenario.PsdSmartObject:
                    case CodecCorpusScenario.Icc:
                    case CodecCorpusScenario.Alpha:
                        RequireScenarioSuccess(sample, scenario);
                        break;
                    case CodecCorpusScenario.PdfEncrypted:
                        RequireScenarioHostOutcome(
                            sample,
                            scenario,
                            CodecCorpusHostResult.PasswordRequired,
                            CodecCorpusProductOutcome.CredentialsOrPermissionRequired);
                        break;
                    case CodecCorpusScenario.PdfCorruptStructure:
                    case CodecCorpusScenario.PsdAbnormalLength:
                        RequireScenarioHostOutcome(
                            sample,
                            scenario,
                            CodecCorpusHostResult.CorruptInput,
                            CodecCorpusProductOutcome.CorruptFile);
                        break;
                    case CodecCorpusScenario.PdfCompressionBomb:
                    case CodecCorpusScenario.PsdCompressionBomb:
                        RequireScenarioHostOutcome(
                            sample,
                            scenario,
                            CodecCorpusHostResult.ResourceLimitExceeded,
                            CodecCorpusProductOutcome.ResourceOrSecurityLimitExceeded);
                        break;
                    case CodecCorpusScenario.PdfSlowRenderCancellation:
                        if (sample.Expected!.InspectResult != CodecCorpusHostResult.Success
                            || sample.Expected.DecodeResult != CodecCorpusHostResult.Success
                            || sample.Expected.ProductOutcome != CodecCorpusProductOutcome.Canceled
                            || sample.Expected.CancellationAfterMilliseconds is null)
                        {
                            throw new InvalidDataException(
                                $"Scenario '{ToContractName(scenario)}' requires exact Host success, canceled product outcome, and an explicit delay for '{sample.Id}'.");
                        }
                        break;
                    default:
                        throw new InvalidDataException(
                            $"Scenario '{ToContractName(scenario)}' has no outcome contract for '{sample.Id}'.");
                }
            }
        }

        foreach (var sample in samples.Where(sample =>
                     sample.Scenarios!.Contains(CodecCorpusScenario.PdfMultiPage)))
        {
            var pageCount = sample.Expected!.PageCount!.Value;
            if (pageCount < 3)
                throw new InvalidDataException($"Multi-page sample '{sample.Id}' must have at least three pages.");
            var goldenPages = sample.Goldens!.Select(golden => golden.PageIndex!.Value).ToHashSet();
            foreach (var page in new[] { 0, pageCount / 2, pageCount - 1 })
            {
                if (!goldenPages.Contains(page))
                {
                    throw new InvalidDataException(
                        $"Multi-page sample '{sample.Id}' needs first, middle, and last page goldens.");
                }
            }
        }

        foreach (var sample in samples.Where(sample =>
                     sample.Scenarios!.Contains(CodecCorpusScenario.PdfSinglePage)))
        {
            if (sample.Expected!.PageCount != 1)
            {
                throw new InvalidDataException(
                    $"Single-page sample '{sample.Id}' must declare exactly one page.");
            }
        }
    }

    private static void RequireScenarioSuccess(
        CodecCorpusSample sample,
        CodecCorpusScenario scenario)
    {
        if (!IsSuccessfulProductSample(sample))
        {
            throw new InvalidDataException(
                $"Scenario '{ToContractName(scenario)}' requires exact Host/product success and a golden for '{sample.Id}'.");
        }
    }

    private static void RequireScenarioHostOutcome(
        CodecCorpusSample sample,
        CodecCorpusScenario scenario,
        CodecCorpusHostResult hostResult,
        CodecCorpusProductOutcome productOutcome)
    {
        var expected = sample.Expected!;
        if (expected.ProductOutcome != productOutcome
            || (expected.InspectResult != hostResult && expected.DecodeResult != hostResult))
        {
            throw new InvalidDataException(
                $"Scenario '{ToContractName(scenario)}' requires Host '{ToContractName(hostResult)}' and product '{ToContractName(productOutcome)}' for '{sample.Id}'.");
        }
    }

    private static bool IsSuccessfulProductSample(CodecCorpusSample sample) =>
        sample.Expected!.InspectResult == CodecCorpusHostResult.Success
        && sample.Expected.DecodeResult == CodecCorpusHostResult.Success
        && sample.Expected.ProductOutcome == CodecCorpusProductOutcome.Success
        && sample.Goldens!.Count > 0;

    private static void RequireSuccessfulProductSample(CodecCorpusSample sample, string kind)
    {
        if (!IsSuccessfulProductSample(sample))
        {
            throw new InvalidDataException(
                $"Every {kind} sample must declare exact Host/product success and at least one golden; id='{sample.Id}'.");
        }
    }

    private static void RequireKindCount(
        CodecCorpusFormat format,
        CodecCorpusSampleKind kind,
        int minimum)
    {
        var count = format.Samples.Count(sample => sample.Kind == kind);
        if (count < minimum)
        {
            throw new InvalidDataException(
                $"{format.Extension} requires at least {minimum} {ToContractName(kind)} samples; actual={count}.");
        }
    }

    private static void RequirePositive(int? value, string field, string id)
    {
        if (value is null || value <= 0)
            throw new InvalidDataException($"expected.{field} must be positive for '{id}'.");
    }

    private static void RequireRange(int? value, int minimum, int maximum, string field, string id)
    {
        if (value is null || value < minimum || value > maximum)
            throw new InvalidDataException($"{field} is outside [{minimum}, {maximum}] for '{id}'.");
    }

    private static void RequireFiniteRange(
        double? value,
        double minimum,
        double maximum,
        string field,
        string id)
    {
        if (value is null || !double.IsFinite(value.Value)
            || value < minimum || value > maximum)
        {
            throw new InvalidDataException(
                $"{field} is outside [{minimum}, {maximum}] for '{id}'.");
        }
    }

    private static void ValidateRelativePath(string? path, string field)
    {
        RequireText(path, field);
        if (Path.IsPathRooted(path!)
            || path!.Replace('\\', '/').Split('/').Contains("..", StringComparer.Ordinal))
        {
            throw new InvalidDataException($"{field} must stay below the configured corpus root.");
        }
    }

    private static string NormalizeRelativePathForComparison(string path) =>
        string.Join(
            '/',
            path.Replace('\\', '/')
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(segment => segment != "."));

    private static void ValidateSha256(string? value, string field)
    {
        if (value is null || !Sha256Pattern.IsMatch(value))
            throw new InvalidDataException($"{field} must be a 64-character hexadecimal digest.");
    }

    private static void RequireText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"{field} is required.");
    }

    private static string ToContractName<T>(T value) where T : struct, Enum =>
        JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
}

internal static class CodecCorpusFile
{
    public static string Resolve(string root, string relativePath)
    {
        var resolvedRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(Path.Combine(resolvedRoot, relativePath));
        var rootPrefix = Path.TrimEndingDirectorySeparator(resolvedRoot)
            + Path.DirectorySeparatorChar;
        Assert.StartsWith(rootPrefix, path, StringComparison.OrdinalIgnoreCase);
        return path;
    }

    public static void VerifyDigest(string root, string relativePath, string expectedSha256)
    {
        var path = Resolve(root, relativePath);
        Assert.True(File.Exists(path), $"Corpus file does not exist: {relativePath}");
        using var stream = File.OpenRead(path);
        Assert.Equal(
            expectedSha256.ToUpperInvariant(),
            Convert.ToHexString(SHA256.HashData(stream)));
    }
}

internal static class CodecCorpusGoldenVerifier
{
    public static async Task AssertMatchesAsync(
        string root,
        CodecCorpusGolden golden,
        DecodedFrame actual)
    {
        CodecCorpusFile.VerifyDigest(root, golden.Path, golden.Sha256);
        await using var stream = File.OpenRead(CodecCorpusFile.Resolve(root, golden.Path));
        var decoded = await new SkiaImageDecoder().DecodeAsync(
            stream,
            DecodeRequest.Default,
            CancellationToken.None);
        using var expected = decoded.Frame;

        AssertPixelsMatch(golden.Tolerance!, expected, actual);
    }

    public static void AssertPixelsMatch(
        CodecCorpusPixelTolerance tolerance,
        DecodedFrame expected,
        DecodedFrame actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        var visibleRowBytes = checked(actual.Width * 4);
        long absoluteError = 0;
        long changedPixels = 0;
        var maxChannelDelta = 0;
        var maxAlphaDelta = 0;
        for (var y = 0; y < actual.Height; y++)
        {
            var actualRow = actual.Pixels.Slice(y * actual.StrideBytes, visibleRowBytes);
            var expectedRow = expected.Pixels.Slice(y * expected.StrideBytes, visibleRowBytes);
            for (var x = 0; x < visibleRowBytes; x += 4)
            {
                var pixelChanged = false;
                for (var channel = 0; channel < 4; channel++)
                {
                    var delta = Math.Abs(actualRow[x + channel] - expectedRow[x + channel]);
                    absoluteError += delta;
                    if (channel == 3)
                        maxAlphaDelta = Math.Max(maxAlphaDelta, delta);
                    else
                        maxChannelDelta = Math.Max(maxChannelDelta, delta);
                    if (delta > tolerance.ChangedPixelDelta!.Value)
                        pixelChanged = true;
                }
                if (pixelChanged)
                    changedPixels++;
            }
        }

        var pixelCount = checked((long)actual.Width * actual.Height);
        var changedPixelRatio = changedPixels / (double)pixelCount;
        var meanAbsoluteError = absoluteError / (double)checked(pixelCount * 4);
        Assert.True(
            maxChannelDelta <= tolerance.MaxChannelDelta,
            $"Golden max channel delta exceeded: actual={maxChannelDelta}, allowed={tolerance.MaxChannelDelta}.");
        Assert.True(
            maxAlphaDelta <= tolerance.MaxAlphaDelta,
            $"Golden max alpha delta exceeded: actual={maxAlphaDelta}, allowed={tolerance.MaxAlphaDelta}.");
        Assert.True(
            changedPixelRatio <= tolerance.MaxChangedPixelRatio,
            $"Golden changed-pixel ratio exceeded: actual={changedPixelRatio:F8}, allowed={tolerance.MaxChangedPixelRatio}.");
        Assert.True(
            meanAbsoluteError <= tolerance.MaxMeanAbsoluteError,
            $"Golden mean absolute error exceeded: actual={meanAbsoluteError:F8}, allowed={tolerance.MaxMeanAbsoluteError}.");
    }
}
