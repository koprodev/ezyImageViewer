using EzyImageViewer.Core.Imaging;
using Xunit;

namespace EzyImageViewer.Tests.Imaging;

public class FormatCorpusManifestTests
{
    private const string CorpusRootVariable = "EZYIMAGEVIEWER_FORMAT_CORPUS";
    private const string CompleteGateVariable = "EZYIMAGEVIEWER_REQUIRE_COMPLETE_FORMAT_CORPUS";

    [Fact]
    public void Manifest_CoversEveryViewableExtension_AndUsesThirtySampleGate()
    {
        var manifest = ReadManifest();

        FormatCorpusManifestValidator.ValidateStructure(manifest);
        Assert.Equal(2, manifest.SchemaVersion);
        Assert.True(manifest.MinimumNormalPerExtension >= 30);
        Assert.Equal(
            ImageFormatCatalog.KnownExtensions.Order(StringComparer.Ordinal),
            manifest.Formats.Select(format => format.Extension).Order(StringComparer.Ordinal));
        Assert.All(manifest.Formats, format => Assert.StartsWith(".", format.Extension));
        Assert.Equal(
            manifest.Formats.Count,
            manifest.Formats.Select(format => format.Extension).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void ManifestEntries_RequireTraceableLicenseAndSha256()
    {
        var manifest = ReadManifest();

        FormatCorpusManifestValidator.ValidateStructure(manifest);

        foreach (var sample in manifest.Formats.SelectMany(format => format.Samples))
        {
            Assert.False(Path.IsPathRooted(sample.Path));
            Assert.DoesNotContain("..", sample.Path.Replace('\\', '/').Split('/'));
            Assert.False(string.IsNullOrWhiteSpace(sample.Source));
            Assert.False(string.IsNullOrWhiteSpace(sample.License));
            Assert.Matches("^[A-Fa-f0-9]{64}$", sample.Sha256);
            Assert.Equal(sample.GoldenPath is null, sample.GoldenSha256 is null);
        }
    }

    [Fact]
    public void Deserialize_RejectsUnmappedMember()
    {
        const string json = """
            {
              "schemaVersion": 2,
              "minimumNormalPerExtension": 30,
              "formats": [],
              "unexpected": true
            }
            """;

        Assert.ThrowsAny<System.Text.Json.JsonException>(
            () => FormatCorpusManifestSerializer.Deserialize(json));
    }

    [Theory]
    [InlineData("png")]
    [InlineData(".PNG")]
    [InlineData(".pn g")]
    public void Structure_RejectsExtensionOutsideSchemaPattern(string extension)
    {
        var manifest = SingleFormatManifest(extension, "Png", "official", []);

        Assert.Throws<InvalidDataException>(
            () => FormatCorpusManifestValidator.ValidateStructure(manifest));
    }

    /// <summary>PDF/PSD는 제품에서 제외됨(ADR-0005).
    /// 해당 형식 이름이 코퍼스 계약을 왕복하면 안 됨.</summary>
    [Theory]
    [InlineData("Pdf")]
    [InlineData("Psd")]
    public void Structure_RejectsRemovedDocumentFormats(string format)
    {
        var manifest = SingleFormatManifest(".png", format, "official", []);

        Assert.Throws<InvalidDataException>(
            () => FormatCorpusManifestValidator.ValidateStructure(manifest));
    }

    [Fact]
    public void Structure_RejectsDuplicateSamplePathWithinFormat()
    {
        var manifest = SingleFormatManifest(".png", "Png", "official",
        [
            Sample("a/one.png", Digest('a')),
            Sample("./a/one.png", Digest('b')),
        ]);

        Assert.Throws<InvalidDataException>(
            () => FormatCorpusManifestValidator.ValidateStructure(manifest));
    }

    [Fact]
    public void Structure_RejectsDuplicateSampleDigestWithinFormat()
    {
        var manifest = SingleFormatManifest(".png", "Png", "official",
        [
            Sample("a/one.png", Digest('a')),
            Sample("a/two.png", Digest('a')),
        ]);

        Assert.Throws<InvalidDataException>(
            () => FormatCorpusManifestValidator.ValidateStructure(manifest));
    }

    [Theory]
    [InlineData("C:\\corpus\\one.png")]
    [InlineData("..\\one.png")]
    [InlineData("a/../../one.png")]
    public void Structure_RejectsRootedOrEscapingSamplePath(string path)
    {
        var manifest = SingleFormatManifest(".png", "Png", "official", [Sample(path, Digest('a'))]);

        Assert.Throws<InvalidDataException>(
            () => FormatCorpusManifestValidator.ValidateStructure(manifest));
    }

    [Fact]
    [Trait("Category", "ExternalCorpus")]
    public void ConfiguredCorpus_HasExpectedCountsAndDigests()
    {
        var manifest = ReadManifest();
        var corpusRoot = Environment.GetEnvironmentVariable(CorpusRootVariable);
        var requireComplete = string.Equals(
            Environment.GetEnvironmentVariable(CompleteGateVariable), "1", StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(corpusRoot))
        {
            Assert.False(requireComplete, $"{CorpusRootVariable} must be set for the complete corpus gate.");
            return;
        }

        var resolvedRoot = Path.GetFullPath(corpusRoot);
        Assert.True(Directory.Exists(resolvedRoot), $"Corpus root does not exist: {resolvedRoot}");
        foreach (var format in manifest.Formats)
        {
            if (requireComplete)
            {
                Assert.True(
                    format.Samples.Count(sample => sample.Kind == FormatCorpusSampleKind.Normal)
                        >= manifest.MinimumNormalPerExtension,
                    $"{format.Extension} has fewer than {manifest.MinimumNormalPerExtension} normal samples.");
            }

            foreach (var sample in format.Samples)
            {
                FormatCorpusFile.VerifyDigest(resolvedRoot, sample.Path, sample.Sha256);
                if (sample.GoldenPath is not null)
                    FormatCorpusFile.VerifyDigest(resolvedRoot, sample.GoldenPath, sample.GoldenSha256!);
            }
        }
    }

    private static string Digest(char fill) => new(fill, 64);

    private static FormatCorpusSample Sample(string path, string sha256) =>
        new(path, FormatCorpusSampleKind.Normal, "test", "CC0-1.0", sha256, null, null);

    private static FormatCorpusManifest SingleFormatManifest(
        string extension,
        string format,
        string supportTier,
        IReadOnlyList<FormatCorpusSample> samples) =>
        new(null, 2, 30, [new FormatCorpusFormat(extension, format, supportTier, samples)]);

    private static FormatCorpusManifest ReadManifest() =>
        FormatCorpusManifestSerializer.ReadTrackedManifest();
}
