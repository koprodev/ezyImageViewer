using System.Text.Json;
using Xunit;

namespace EzyImageViewer.Tests.Codec;

public sealed class CodecCorpusManifestTests
{
    private const string Digest =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void TrackedEmptyManifest_IsStructurallyValidButCannotPassOptInCodecGate()
    {
        var manifest = CodecCorpusManifestSerializer.ReadTrackedManifest();

        CodecCorpusManifestValidator.ValidateStructure(manifest);
        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateCodecActivationCoverage(manifest));

        Assert.Contains("at least 30 normal samples", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QualificationPerformanceSchema_MatchesValidatorPolicyCeilings()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Formats",
            "corpus-manifest.schema.json");
        using var schema = JsonDocument.Parse(File.ReadAllText(path));
        var properties = schema.RootElement
            .GetProperty("$defs")
            .GetProperty("qualificationPerformanceBudget")
            .GetProperty("properties");

        Assert.Equal(
            CodecCorpusManifestValidator.MaxQualificationMedianDecodeElapsedMilliseconds,
            properties
                .GetProperty("maxMedianDecodeElapsedMilliseconds")
                .GetProperty("maximum")
                .GetInt32());
        Assert.Equal(
            CodecCorpusManifestValidator.MaxQualificationRepetitions,
            properties
                .GetProperty("repetitions")
                .GetProperty("maximum")
                .GetInt32());
        Assert.Equal(
            CodecCorpusManifestValidator.MaxQualificationPeakMemoryBytes,
            properties
                .GetProperty("maxPeakWorkingSetBytes")
                .GetProperty("maximum")
                .GetInt64());
        Assert.Equal(
            CodecCorpusManifestValidator.MaxQualificationPeakMemoryBytes,
            properties
                .GetProperty("maxPeakCommitBytes")
                .GetProperty("maximum")
                .GetInt64());
    }

    [Fact]
    public void Deserialize_RejectsUnmappedMember()
    {
        var json = """
        {
          "schemaVersion": 2,
          "minimumNormalPerExtension": 30,
          "formats": [],
          "unexpected": true
        }
        """;

        Assert.Throws<JsonException>(() => CodecCorpusManifestSerializer.Deserialize(json));
    }

    [Fact]
    public void Structure_RejectsCodecSupportTierThatDoesNotMatchFormat()
    {
        var json = """
        {
          "schemaVersion": 2,
          "minimumNormalPerExtension": 30,
          "formats": [
            {
              "extension": ".pdf",
              "format": "Pdf",
              "supportTier": "official",
              "samples": []
            }
          ]
        }
        """;
        var manifest = CodecCorpusManifestSerializer.Deserialize(json);

        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(manifest));
        Assert.Contains("supportTier", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("pdf")]
    [InlineData(".PDF")]
    [InlineData(".pdf-backup")]
    public void Structure_RejectsExtensionOutsideSchemaPattern(string extension)
    {
        var manifest = CodecCorpusManifestSerializer.Deserialize(
            EmptyGenericFormatJson(extension));

        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(manifest));

        Assert.Contains("must match", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Structure_RejectsCodecOnlyFieldsOnGenericSample()
    {
        var manifest = CodecCorpusManifestSerializer.Deserialize(
            GenericSampleJson(
                $"{(char)34}id{(char)34}: {(char)34}codec-only{(char)34},"));

        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(manifest));

        Assert.Contains("codec-only fields", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Structure_RejectsDuplicateSamplePathWithinFormat()
    {
        var manifest = SuccessfulManifest();
        var first = manifest.Formats.Single().Samples.Single();
        var duplicate = first with
        {
            Id = "pdf-duplicate-path",
            Sha256 = new string('1', 64),
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(
                ReplaceSingleFormatSamples(manifest, first, duplicate)));

        Assert.Contains("paths must be unique", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Structure_RejectsDuplicateSampleDigestWithinFormat()
    {
        var manifest = SuccessfulManifest();
        var first = manifest.Formats.Single().Samples.Single();
        var duplicate = first with
        {
            Id = "pdf-duplicate-digest",
            Path = "pdf/duplicate-digest.pdf",
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(
                ReplaceSingleFormatSamples(manifest, first, duplicate)));

        Assert.Contains("digests must be unique", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(@"C:pdf\sample.pdf")]
    [InlineData(@"\pdf\sample.pdf")]
    public void Structure_RejectsWindowsRootedOrDriveRelativeSamplePath(string path)
    {
        var manifest = SuccessfulManifest();
        var sample = manifest.Formats.Single().Samples.Single() with { Path = path };

        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(
                ReplaceSingleFormatSamples(manifest, sample)));

        Assert.Contains("corpus root", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Coverage_RequiresTwoDistinctNormalProducerNames()
    {
        var manifest = SuccessfulManifest();
        var template = manifest.Formats.Single().Samples.Single();
        var samples = Enumerable.Range(0, 34)
            .Select(index => template with
            {
                Id = $"pdf-producer-{index:D2}",
                Path = $"pdf/producer-{index:D2}.pdf",
                Sha256 = (index + 1).ToString("x64", System.Globalization.CultureInfo.InvariantCulture),
                Kind = index switch
                {
                    < 30 => CodecCorpusSampleKind.Normal,
                    30 => CodecCorpusSampleKind.Large,
                    31 => CodecCorpusSampleKind.Boundary,
                    32 => CodecCorpusSampleKind.Corrupt,
                    _ => CodecCorpusSampleKind.Security,
                },
            })
            .ToArray();

        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateCodecActivationCoverage(
                ReplaceSingleFormatSamples(manifest, samples)));

        Assert.Contains("two distinct producer names", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("codecUnavailable", "success")]
    [InlineData("success", "systemCodecUnavailable")]
    public void Deserialize_RejectsUnmappedExpectedResults(
        string inspectResult,
        string productOutcome)
    {
        var json = CodecSampleJson(
            inspectResult,
            productOutcome,
            includePassword: true);

        Assert.Throws<JsonException>(() => CodecCorpusManifestSerializer.Deserialize(json));
    }

    [Fact]
    public void PasswordRefusal_RequiresExplicitPasswordFieldRegardlessOfFilename()
    {
        var withoutPassword = CodecCorpusManifestSerializer.Deserialize(
            CodecSampleJson(
                "passwordRequired",
                "credentialsOrPermissionRequired",
                includePassword: false));
        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(withoutPassword));
        Assert.Contains("password", error.Message, StringComparison.OrdinalIgnoreCase);

        var withPassword = CodecCorpusManifestSerializer.Deserialize(
            CodecSampleJson(
                "passwordRequired",
                "credentialsOrPermissionRequired",
                includePassword: true));
        CodecCorpusManifestValidator.ValidateStructure(withPassword);
    }

    [Fact]
    public void ScenarioSemantics_RejectsSuccessfulScenarioWithRefusalOutcome()
    {
        var manifest = CodecCorpusManifestSerializer.Deserialize(
            CodecSampleJson("corruptInput", "corruptFile", includePassword: false));
        var sample = manifest.Formats.Single().Samples.Single() with
        {
            Scenarios = [CodecCorpusScenario.PdfTransparency],
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(
                ReplaceSingleFormatSamples(manifest, sample)));

        Assert.Contains("requires exact Host/product success", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("accessDenied", "credentialsOrPermissionRequired", "PdfEncrypted", "passwordRequired")]
    [InlineData("invalidRequest", "corruptFile", "PdfCorruptStructure", "corruptInput")]
    [InlineData("corruptInput", "corruptFile", "PdfCompressionBomb", "resourceLimitExceeded")]
    public void ScenarioSemantics_RejectsMismatchedRefusalOutcome(
        string inspectResult,
        string productOutcome,
        string scenarioName,
        string requiredHostResult)
    {
        var manifest = CodecCorpusManifestSerializer.Deserialize(
            CodecSampleJson(inspectResult, productOutcome, includePassword: false));
        var sample = manifest.Formats.Single().Samples.Single() with
        {
            Scenarios = [Enum.Parse<CodecCorpusScenario>(scenarioName)],
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(
                ReplaceSingleFormatSamples(manifest, sample)));

        Assert.Contains(requiredHostResult, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScenarioSemantics_RejectsSlowCancellationDeclaredAsProductSuccess()
    {
        var manifest = SuccessfulManifest();
        var sample = manifest.Formats.Single().Samples.Single() with
        {
            Scenarios = [CodecCorpusScenario.PdfSlowRenderCancellation],
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(
                ReplaceSingleFormatSamples(manifest, sample)));

        Assert.Contains("canceled product outcome", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScenarioSemantics_RejectsConflictingSuccessAndCancellationScenarios()
    {
        var manifest = CodecCorpusManifestSerializer.Deserialize(
            SuccessfulCodecSampleJson(
                productOutcome: "canceled",
                cancellationAfterMilliseconds: 250,
                includeGolden: true));
        var sample = manifest.Formats.Single().Samples.Single() with
        {
            Scenarios =
            [
                CodecCorpusScenario.PdfSinglePage,
                CodecCorpusScenario.PdfSlowRenderCancellation,
            ],
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(
                ReplaceSingleFormatSamples(manifest, sample)));

        Assert.Contains("requires exact Host/product success", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ScenarioSemantics_RejectsSinglePageScenarioWithMultiplePages()
    {
        var manifest = SuccessfulManifest();
        var sample = manifest.Formats.Single().Samples.Single();
        sample = sample with
        {
            Expected = sample.Expected! with { PageCount = 2 },
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(
                ReplaceSingleFormatSamples(manifest, sample)));

        Assert.Contains("exactly one page", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void GoldenComparison_RejectsRgbDeltaBeyondChannelTolerance()
    {
        using var expected = CreateSinglePixelFrame(blue: 0, alpha: 255);
        using var actual = CreateSinglePixelFrame(blue: 5, alpha: 255);
        var tolerance = new CodecCorpusPixelTolerance(
            MaxChannelDelta: 4,
            MaxAlphaDelta: 255,
            ChangedPixelDelta: 255,
            MaxChangedPixelRatio: 1,
            MaxMeanAbsoluteError: 255);

        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            CodecCorpusGoldenVerifier.AssertPixelsMatch(tolerance, expected, actual));
        Assert.Contains("max channel delta", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GoldenComparison_RejectsAlphaDeltaIndependentlyOfRgbTolerance()
    {
        using var expected = CreateSinglePixelFrame(blue: 0, alpha: 255);
        using var actual = CreateSinglePixelFrame(blue: 0, alpha: 254);
        var tolerance = new CodecCorpusPixelTolerance(
            MaxChannelDelta: 255,
            MaxAlphaDelta: 0,
            ChangedPixelDelta: 255,
            MaxChangedPixelRatio: 1,
            MaxMeanAbsoluteError: 255);

        var error = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() =>
            CodecCorpusGoldenVerifier.AssertPixelsMatch(tolerance, expected, actual));
        Assert.Contains("max alpha delta", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GoldenComparison_AllowsAlphaDeltaWithoutConsumingRgbTolerance()
    {
        using var expected = CreateSinglePixelFrame(blue: 0, alpha: 255);
        using var actual = CreateSinglePixelFrame(blue: 0, alpha: 254);
        var tolerance = new CodecCorpusPixelTolerance(
            MaxChannelDelta: 0,
            MaxAlphaDelta: 1,
            ChangedPixelDelta: 255,
            MaxChangedPixelRatio: 1,
            MaxMeanAbsoluteError: 255);

        CodecCorpusGoldenVerifier.AssertPixelsMatch(tolerance, expected, actual);
    }

    [Fact]
    public void SuccessfulHostBaseline_RequiresGoldenForSamePageAndTarget()
    {
        var withoutGolden = CodecCorpusManifestSerializer.Deserialize(
            SuccessfulCodecSampleJson(
                productOutcome: "success",
                cancellationAfterMilliseconds: null,
                includeGolden: false));
        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(withoutGolden));
        Assert.Contains("same page and target", error.Message, StringComparison.OrdinalIgnoreCase);

        var withGolden = CodecCorpusManifestSerializer.Deserialize(
            SuccessfulCodecSampleJson(
                productOutcome: "success",
                cancellationAfterMilliseconds: null,
                includeGolden: true));
        CodecCorpusManifestValidator.ValidateStructure(withGolden);
    }

    [Fact]
    public void CanceledProductOutcome_RequiresExplicitCancellationDelay()
    {
        var withoutDelay = CodecCorpusManifestSerializer.Deserialize(
            SuccessfulCodecSampleJson(
                productOutcome: "canceled",
                cancellationAfterMilliseconds: null,
                includeGolden: true));
        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(withoutDelay));
        Assert.Contains(
            "cancellationAfterMilliseconds",
            error.Message,
            StringComparison.Ordinal);

        var withDelay = CodecCorpusManifestSerializer.Deserialize(
            SuccessfulCodecSampleJson(
                productOutcome: "canceled",
                cancellationAfterMilliseconds: 250,
                includeGolden: true));
        CodecCorpusManifestValidator.ValidateStructure(withDelay);
    }

    [Fact]
    public void Golden_RequiresNativeDimensionsForItsPage()
    {
        var manifest = SuccessfulManifest();
        var sample = manifest.Formats.Single().Samples.Single();
        var golden = sample.Goldens!.Single() with { NativeWidth = null };

        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(
                ReplaceSingleFormatSamples(
                    manifest,
                    sample with { Goldens = [golden] })));

        Assert.Contains("golden.nativeWidth", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PageZeroGolden_RequiresInspectNativeDimensions()
    {
        var manifest = SuccessfulManifest();
        var sample = manifest.Formats.Single().Samples.Single();
        var golden = sample.Goldens!.Single() with { NativeWidth = 2 };

        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(
                ReplaceSingleFormatSamples(
                    manifest,
                    sample with { Goldens = [golden] })));

        Assert.Contains("match inspect expectations", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("maxChannelDelta")]
    [InlineData("maxAlphaDelta")]
    [InlineData("changedPixelDelta")]
    [InlineData("maxChangedPixelRatio")]
    [InlineData("maxMeanAbsoluteError")]
    public void Golden_RejectsToleranceAboveFidelityPolicy(string field)
    {
        var manifest = SuccessfulManifest();
        var sample = manifest.Formats.Single().Samples.Single();
        var golden = sample.Goldens!.Single();
        var tolerance = golden.Tolerance!;
        tolerance = field switch
        {
            "maxChannelDelta" => tolerance with
            {
                MaxChannelDelta = CodecCorpusManifestValidator.MaxGoldenChannelDelta + 1,
            },
            "maxAlphaDelta" => tolerance with
            {
                MaxAlphaDelta = CodecCorpusManifestValidator.MaxGoldenAlphaDelta + 1,
            },
            "changedPixelDelta" => tolerance with
            {
                ChangedPixelDelta = CodecCorpusManifestValidator.MaxGoldenChangedPixelDelta + 1,
            },
            "maxChangedPixelRatio" => tolerance with
            {
                MaxChangedPixelRatio =
                    CodecCorpusManifestValidator.MaxGoldenChangedPixelRatio + 0.001,
            },
            "maxMeanAbsoluteError" => tolerance with
            {
                MaxMeanAbsoluteError =
                    CodecCorpusManifestValidator.MaxGoldenMeanAbsoluteError + 0.001,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(
                ReplaceSingleFormatSamples(
                    manifest,
                    sample with { Goldens = [golden with { Tolerance = tolerance }] })));

        Assert.Contains(field, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Golden_AcceptsFidelityPolicyCeilings()
    {
        var manifest = SuccessfulManifest();
        var sample = manifest.Formats.Single().Samples.Single();
        var golden = sample.Goldens!.Single();
        var tolerance = new CodecCorpusPixelTolerance(
            CodecCorpusManifestValidator.MaxGoldenChannelDelta,
            CodecCorpusManifestValidator.MaxGoldenAlphaDelta,
            CodecCorpusManifestValidator.MaxGoldenChangedPixelDelta,
            CodecCorpusManifestValidator.MaxGoldenChangedPixelRatio,
            CodecCorpusManifestValidator.MaxGoldenMeanAbsoluteError);

        CodecCorpusManifestValidator.ValidateStructure(
            ReplaceSingleFormatSamples(
                manifest,
                sample with { Goldens = [golden with { Tolerance = tolerance }] }));
    }

    [Fact]
    public void TargetMaxDimension_RejectsValueAboveHostLimit()
    {
        var manifest = SuccessfulManifest();
        var sample = manifest.Formats.Single().Samples.Single();
        var expected = sample.Expected! with { DecodeTargetMaxDimension = 65_501 };

        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(
                ReplaceSingleFormatSamples(
                    manifest,
                    sample with { Expected = expected })));

        Assert.Contains("decodeTargetMaxDimension", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QualificationPerformanceBudget_AcceptsPolicyCeilingsForSuccessfulSample()
    {
        var manifest = SuccessfulManifest();
        var sample = manifest.Formats.Single().Samples.Single() with
        {
            Kind = CodecCorpusSampleKind.Large,
            QualificationPerformanceBudget = new(
                CodecCorpusManifestValidator.MaxQualificationRepetitions,
                CodecCorpusManifestValidator.MaxQualificationMedianDecodeElapsedMilliseconds,
                CodecCorpusManifestValidator.MaxQualificationPeakMemoryBytes,
                CodecCorpusManifestValidator.MaxQualificationPeakMemoryBytes),
        };

        CodecCorpusManifestValidator.ValidateStructure(
            ReplaceSingleFormatSamples(manifest, sample));
    }

    [Theory]
    [InlineData("repetitions", 2L)]
    [InlineData("repetitions", 11L)]
    [InlineData("maxMedianDecodeElapsedMilliseconds", 0L)]
    [InlineData("maxMedianDecodeElapsedMilliseconds", 120_001L)]
    [InlineData("maxPeakWorkingSetBytes", 0L)]
    [InlineData("maxPeakWorkingSetBytes", 1_073_741_825L)]
    [InlineData("maxPeakCommitBytes", 0L)]
    [InlineData("maxPeakCommitBytes", 1_073_741_825L)]
    public void QualificationPerformanceBudget_RejectsValuesOutsidePolicy(
        string field,
        long value)
    {
        var manifest = SuccessfulManifest();
        var sample = manifest.Formats.Single().Samples.Single();
        var budget = new CodecCorpusQualificationPerformanceBudget(
            Repetitions: 3,
            MaxMedianDecodeElapsedMilliseconds: 2_000,
            MaxPeakWorkingSetBytes: 512L * 1024 * 1024,
            MaxPeakCommitBytes: 768L * 1024 * 1024);
        budget = field switch
        {
            "repetitions" => budget with
            {
                Repetitions = checked((int)value),
            },
            "maxMedianDecodeElapsedMilliseconds" => budget with
            {
                MaxMedianDecodeElapsedMilliseconds = checked((int)value),
            },
            "maxPeakWorkingSetBytes" => budget with
            {
                MaxPeakWorkingSetBytes = value,
            },
            "maxPeakCommitBytes" => budget with
            {
                MaxPeakCommitBytes = value,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(
                ReplaceSingleFormatSamples(
                    manifest,
                    sample with { QualificationPerformanceBudget = budget })));

        Assert.Contains(field, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QualificationPerformanceBudget_RejectsNonSuccessSample()
    {
        var manifest = CodecCorpusManifestSerializer.Deserialize(
            SuccessfulCodecSampleJson(
                productOutcome: "canceled",
                cancellationAfterMilliseconds: 250,
                includeGolden: true));
        var sample = manifest.Formats.Single().Samples.Single() with
        {
            QualificationPerformanceBudget = new(
                Repetitions: 3,
                MaxMedianDecodeElapsedMilliseconds: 2_000,
                MaxPeakWorkingSetBytes: 512L * 1024 * 1024,
                MaxPeakCommitBytes: 768L * 1024 * 1024),
        };

        var error = Assert.Throws<InvalidDataException>(() =>
            CodecCorpusManifestValidator.ValidateStructure(
                ReplaceSingleFormatSamples(manifest, sample)));

        Assert.Contains(
            "requires exact Host/product success",
            error.Message,
            StringComparison.Ordinal);
    }

    private static EzyImageViewer.Core.Imaging.DecodedFrame CreateSinglePixelFrame(
        byte blue,
        byte alpha) => new(
        [blue, 0, 0, alpha],
        width: 1,
        height: 1,
        strideBytes: 4,
        hasAlpha: alpha != 255);

    private static CodecCorpusManifest SuccessfulManifest() =>
        CodecCorpusManifestSerializer.Deserialize(
            SuccessfulCodecSampleJson(
                productOutcome: "success",
                cancellationAfterMilliseconds: null,
                includeGolden: true));

    private static CodecCorpusManifest ReplaceSingleFormatSamples(
        CodecCorpusManifest manifest,
        params CodecCorpusSample[] samples)
    {
        var format = manifest.Formats.Single();
        return manifest with
        {
            Formats = [format with { Samples = samples }],
        };
    }

    private static string EmptyGenericFormatJson(string extension) => $$"""
    {
      "schemaVersion": 2,
      "minimumNormalPerExtension": 30,
      "formats": [
        {
          "extension": "{{extension}}",
          "format": "Png",
          "supportTier": "official",
          "samples": []
        }
      ]
    }
    """;

    private static string GenericSampleJson(string extraField) => $$"""
    {
      "schemaVersion": 2,
      "minimumNormalPerExtension": 30,
      "formats": [
        {
          "extension": ".png",
          "format": "Png",
          "supportTier": "official",
          "samples": [
            {
              {{extraField}}
              "path": "png/sample.png",
              "kind": "normal",
              "source": "test fixture",
              "license": "test-only",
              "sha256": "{{Digest}}"
            }
          ]
        }
      ]
    }
    """;

    private static string SuccessfulCodecSampleJson(
        string productOutcome,
        int? cancellationAfterMilliseconds,
        bool includeGolden)
    {
        var scenario = productOutcome == "canceled"
            ? "pdfSlowRenderCancellation"
            : "pdfSinglePage";
        var cancellation = cancellationAfterMilliseconds is { } delay
            ? $"{(char)34}cancellationAfterMilliseconds{(char)34}: {delay},"
            : string.Empty;
        var goldens = includeGolden
            ? $$"""
              [
                {
                  "pageIndex": 0,
                  "targetMaxDimension": 0,
                  "nativeWidth": 1,
                  "nativeHeight": 1,
                  "path": "goldens/pdf-baseline.png",
                  "sha256": "{{Digest}}",
                  "referenceRenderer": { "name": "reference", "version": "1.0" },
                  "colorSpace": "srgb",
                  "alphaMode": "premultipliedBgra8",
                  "tolerance": {
                    "maxChannelDelta": 4,
                    "maxAlphaDelta": 0,
                    "changedPixelDelta": 2,
                    "maxChangedPixelRatio": 0.01,
                    "maxMeanAbsoluteError": 1.0
                  }
                }
              ]
              """
            : "[]";
        return $$"""
        {
          "schemaVersion": 2,
          "minimumNormalPerExtension": 30,
          "formats": [
            {
              "extension": ".pdf",
              "format": "Pdf",
              "supportTier": "conditional",
              "samples": [
                {
                  "id": "pdf-success-baseline",
                  "path": "pdf/baseline.pdf",
                  "kind": "boundary",
                  "scenarios": ["{{scenario}}"],
                  "producer": { "name": "producer", "version": "1.0", "platform": "test" },
                  "source": "test fixture",
                  "license": "test-only",
                  "sha256": "{{Digest}}",
                  "expected": {
                    "inspectResult": "success",
                    "decodeResult": "success",
                    "productOutcome": "{{productOutcome}}",
                    "pageCount": 1,
                    "nativeWidth": 1,
                    "nativeHeight": 1,
                    "decodePageIndex": 0,
                    {{cancellation}}
                    "decodeTargetMaxDimension": 0
                  },
                  "goldens": {{goldens}}
                }
              ]
            }
          ]
        }
        """;
    }

    private static string CodecSampleJson(
        string inspectResult,
        string productOutcome,
        bool includePassword)
    {
        var password = includePassword ? "\"password\": \"test-only-password\"," : string.Empty;
        return $$"""
        {
          "schemaVersion": 2,
          "minimumNormalPerExtension": 30,
          "formats": [
            {
              "extension": ".pdf",
              "format": "Pdf",
              "supportTier": "conditional",
              "samples": [
                {
                  "id": "pdf-encrypted-explicit-password",
                  "path": "password/encrypted.pdf",
                  "kind": "security",
                  "scenarios": ["pdfEncrypted"],
                  "producer": {
                    "name": "test-producer",
                    "version": "1.0",
                    "platform": "test"
                  },
                  "source": "test fixture",
                  "license": "test-only",
                  "sha256": "{{Digest}}",
                  {{password}}
                  "expected": {
                    "inspectResult": "{{inspectResult}}",
                    "productOutcome": "{{productOutcome}}"
                  },
                  "goldens": []
                }
              ]
            }
          ]
        }
        """;
    }
}
