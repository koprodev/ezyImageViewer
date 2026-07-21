using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Tests.Codec;
using Xunit;

namespace EzyImageViewer.Tests.Imaging;

public class FormatCorpusManifestTests
{
    private const string CorpusRootVariable = "EZYIMAGEVIEWER_FORMAT_CORPUS";
    private const string CompleteGateVariable = "EZYIMAGEVIEWER_REQUIRE_COMPLETE_FORMAT_CORPUS";

    [Fact]
    public void Manifest_CoversEveryM8AExtension_AndUsesThirtySampleGate()
    {
        var manifest = ReadManifest();

        CodecCorpusManifestValidator.ValidateStructure(manifest);
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

        CodecCorpusManifestValidator.ValidateStructure(manifest);

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
                    format.Samples.Count(sample => sample.Kind == CodecCorpusSampleKind.Normal)
                        >= manifest.MinimumNormalPerExtension,
                    $"{format.Extension} has fewer than {manifest.MinimumNormalPerExtension} normal samples.");
            }

            foreach (var sample in format.Samples)
            {
                CodecCorpusFile.VerifyDigest(resolvedRoot, sample.Path, sample.Sha256);
                if (sample.GoldenPath is not null)
                    CodecCorpusFile.VerifyDigest(resolvedRoot, sample.GoldenPath, sample.GoldenSha256!);
                foreach (var golden in sample.Goldens ?? [])
                    CodecCorpusFile.VerifyDigest(resolvedRoot, golden.Path, golden.Sha256);
            }
        }
    }

    private static CodecCorpusManifest ReadManifest() =>
        CodecCorpusManifestSerializer.ReadTrackedManifest();
}
