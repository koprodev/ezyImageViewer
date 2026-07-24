using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Xunit;

namespace EzyImageViewer.Tests.Imaging;

internal enum FormatCorpusSampleKind
{
    Normal,
    Large,
    Boundary,
    Corrupt,
    Security,
}

internal sealed record FormatCorpusManifest(
    [property: JsonPropertyName("$schema")] string? Schema,
    int SchemaVersion,
    int MinimumNormalPerExtension,
    IReadOnlyList<FormatCorpusFormat> Formats);

internal sealed record FormatCorpusFormat(
    string Extension,
    string Format,
    string SupportTier,
    IReadOnlyList<FormatCorpusSample> Samples);

internal sealed record FormatCorpusSample(
    string Path,
    FormatCorpusSampleKind Kind,
    string Source,
    string License,
    string Sha256,
    string? GoldenPath,
    string? GoldenSha256);

internal static class FormatCorpusManifestSerializer
{
    private static readonly JsonSerializerOptions Options = CreateOptions();

    public static FormatCorpusManifest ReadTrackedManifest()
    {
        var path = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Formats",
            "corpus-manifest.json");
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<FormatCorpusManifest>(stream, Options)
            ?? throw new InvalidDataException("The format corpus manifest is empty.");
    }

    public static FormatCorpusManifest Deserialize(string json) =>
        JsonSerializer.Deserialize<FormatCorpusManifest>(json, Options)
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

internal static class FormatCorpusManifestValidator
{
    private const int SchemaVersion = 2;
    private static readonly Regex Sha256Pattern = new(
        "^[A-Fa-f0-9]{64}$",
        RegexOptions.CultureInvariant);
    private static readonly Regex ExtensionPattern = new(
        "^\\.[a-z0-9]+$",
        RegexOptions.CultureInvariant);
    private static readonly HashSet<string> KnownFormats = new(
        ["Jpeg", "Png", "Bmp", "Gif", "Tiff", "Ico", "WebP", "Avif", "Heif", "Svg"],
        StringComparer.Ordinal);
    private static readonly HashSet<string> KnownSupportTiers = new(
        ["official", "conditional"],
        StringComparer.Ordinal);

    public static void ValidateStructure(FormatCorpusManifest manifest)
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
            if (!KnownSupportTiers.Contains(format.SupportTier))
                throw new InvalidDataException($"Unknown supportTier '{format.SupportTier}'.");
            if (format.Samples is null)
                throw new InvalidDataException($"samples is required for '{format.Extension}'.");

            var samplePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sampleDigests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sample in format.Samples)
            {
                ValidateSample(format, sample);
                if (!samplePaths.Add(NormalizeRelativePathForComparison(sample.Path)))
                {
                    throw new InvalidDataException(
                        $"Sample paths must be unique within '{format.Extension}': '{sample.Path}'.");
                }
                if (!sampleDigests.Add(sample.Sha256))
                {
                    throw new InvalidDataException(
                        $"Sample SHA-256 digests must be unique within '{format.Extension}': '{sample.Sha256}'.");
                }
            }
        }
    }

    private static void ValidateSample(FormatCorpusFormat format, FormatCorpusSample sample)
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
                $"goldenPath and goldenSha256 must both be present or absent for '{sample.Path}'.");
        }
        if (sample.GoldenPath is not null)
        {
            ValidateRelativePath(sample.GoldenPath, "golden path");
            ValidateSha256(sample.GoldenSha256!, $"goldenSha256 for '{sample.Path}'");
        }
    }

    private static void ValidateRelativePath(string? path, string field)
    {
        RequireText(path, field);
        if (System.IO.Path.IsPathRooted(path!)
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
}

internal static class FormatCorpusFile
{
    public static string Resolve(string root, string relativePath)
    {
        var resolvedRoot = System.IO.Path.GetFullPath(root);
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(resolvedRoot, relativePath));
        var rootPrefix = System.IO.Path.TrimEndingDirectorySeparator(resolvedRoot)
            + System.IO.Path.DirectorySeparatorChar;
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
