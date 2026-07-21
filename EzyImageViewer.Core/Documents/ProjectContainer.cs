using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace EzyImageViewer.Core.Documents;

public sealed record ProjectManifest(string Format, int SchemaVersion, string AppVersion, DateTimeOffset CreatedUtc)
{
    public const string FormatId = "ezyimg";
    // v3 (M8-A): document.json carries page-scoped v2 document fragments.
    public const int CurrentSchemaVersion = 3;

    public static ProjectManifest Create(string appVersion) =>
        new(FormatId, CurrentSchemaVersion, appVersion, DateTimeOffset.UtcNow);
}

/// <summary>
/// Container hardening limits, symmetric by contract: <see cref="EzyProjectArchive.Write"/>
/// pre-validates against the same limits <see cref="EzyProjectArchive.Read"/> enforces, so the
/// writer can never produce a project its own reader refuses. ZipArchive itself enforces none
/// (dotnet zip best practices).
/// </summary>
public sealed record EzyProjectLimits
{
    public static EzyProjectLimits Default { get; } = new();

    public int MaxEntryCount { get; init; } = 1024;
    /// <summary>Sized to the largest source file the loader admits (InputLimits.MaxFileBytes),
    /// since a project embeds the original bytes (§7.10 embedded-source).</summary>
    public long MaxEntryBytes { get; init; } = Imaging.InputLimits.Default.MaxFileBytes;
    public long MaxTotalBytes { get; init; } = 1024L * 1024 * 1024;
}

public sealed class EzyProject
{
    public required ProjectManifest Manifest { get; init; }
    public required string DocumentJson { get; init; }
    public byte[]? PreviewPng { get; init; }
    /// <summary>Embedded background (§7.10 embedded-source): the original file bytes for a file
    /// source, or the rendered background for clipboard/capture. Name keeps the real extension so
    /// the open path can re-sniff. External source links are a later option.</summary>
    public string? SourceName { get; init; }
    public byte[]? SourceBytes { get; init; }
    public IReadOnlyDictionary<string, byte[]> Assets { get; init; } = new Dictionary<string, byte[]>();
}

/// <summary>
/// .ezyimg container: ZIP with a leading uncompressed "mimetype" entry (EPUB/ODF convention)
/// so the format is detectable from the first bytes without full extraction.
/// Layout: mimetype, manifest.json, document.json, preview.png?, assets/*.
/// </summary>
public static class EzyProjectArchive
{
    public const string MimeType = "application/x-ezyimg";

    private const string MimeTypeEntryName = "mimetype";
    private const string ManifestEntryName = "manifest.json";
    private const string DocumentEntryName = "document.json";
    private const string PreviewEntryName = "preview.png";
    private const string SourcePrefix = "source/";
    private const string AssetsPrefix = "assets/";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static void Write(Stream target, EzyProject project, EzyProjectLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(project);
        limits ??= EzyProjectLimits.Default;

        // Materialize every entry first: an unreadable project must be refused before any byte
        // lands on the stream, not detected on the next open.
        var entries = new List<(string Name, byte[] Data, CompressionLevel Level)>
        {
            // Stored (not deflated) and first, so the mimetype sits at a fixed offset for sniffing.
            (MimeTypeEntryName, Encoding.ASCII.GetBytes(MimeType), CompressionLevel.NoCompression),
            (ManifestEntryName, JsonSerializer.SerializeToUtf8Bytes(project.Manifest, JsonOptions), CompressionLevel.Optimal),
            (DocumentEntryName, Encoding.UTF8.GetBytes(project.DocumentJson), CompressionLevel.Optimal),
        };
        if (project.PreviewPng is { Length: > 0 })
            entries.Add((PreviewEntryName, project.PreviewPng, CompressionLevel.Optimal));
        if (project.SourceBytes is { Length: > 0 })
        {
            if (string.IsNullOrEmpty(project.SourceName))
                throw new InvalidDataException("Embedded source bytes require a source name.");
            ValidateAssetName(project.SourceName);
            entries.Add((SourcePrefix + project.SourceName, project.SourceBytes, CompressionLevel.Optimal));
        }
        foreach (var (name, data) in project.Assets)
        {
            ValidateAssetName(name);
            entries.Add((AssetsPrefix + name, data, CompressionLevel.Optimal));
        }

        if (entries.Count > limits.MaxEntryCount)
            throw new InvalidDataException($"Project exceeds the entry limit of {limits.MaxEntryCount}.");
        long total = 0;
        foreach (var (name, data, _) in entries)
        {
            if (data.LongLength > limits.MaxEntryBytes)
                throw new InvalidDataException(
                    $"Project entry '{name}' ({data.LongLength:N0} bytes) exceeds the size limit of {limits.MaxEntryBytes:N0} bytes.");
            total = checked(total + data.LongLength);
            if (total > limits.MaxTotalBytes)
                throw new InvalidDataException(
                    $"Project exceeds the total size limit of {limits.MaxTotalBytes:N0} bytes.");
        }

        using var zip = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (name, data, level) in entries)
            WriteEntry(zip, name, data, level);
    }

    public static EzyProject Read(Stream source, EzyProjectLimits? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= EzyProjectLimits.Default;

        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var entries = zip.Entries;

        if (entries.Count == 0)
            throw new InvalidDataException("Not an ezyimg container: archive is empty.");
        if (entries.Count > options.MaxEntryCount)
            throw new InvalidDataException($"Archive exceeds the entry limit of {options.MaxEntryCount}.");

        // Duplicate names make required-entry lookups ambiguous across zip parsers.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long declaredTotal = 0;
        foreach (var entry in entries)
        {
            if (!seen.Add(entry.FullName))
                throw new InvalidDataException($"Duplicate archive entry '{entry.FullName}'.");
            if (entry.Length > options.MaxEntryBytes)
                throw new InvalidDataException($"Entry '{entry.FullName}' exceeds the size limit of {options.MaxEntryBytes} bytes.");
            declaredTotal = checked(declaredTotal + entry.Length);
            if (declaredTotal > options.MaxTotalBytes)
                throw new InvalidDataException($"Archive exceeds the total size limit of {options.MaxTotalBytes} bytes.");
        }

        var first = entries[0];
        if (first.FullName != MimeTypeEntryName || first.CompressedLength != first.Length)
            throw new InvalidDataException("Not an ezyimg container: first entry must be an uncompressed mimetype.");
        if (Encoding.UTF8.GetString(ReadEntry(first, options)) != MimeType)
            throw new InvalidDataException("Not an ezyimg container: mimetype mismatch.");

        var manifestEntry = zip.GetEntry(ManifestEntryName)
            ?? throw new InvalidDataException("Invalid ezyimg container: manifest.json missing.");
        var manifest = JsonSerializer.Deserialize<ProjectManifest>(ReadEntry(manifestEntry, options), JsonOptions)
            ?? throw new InvalidDataException("Invalid ezyimg container: manifest.json empty.");
        if (manifest.Format != ProjectManifest.FormatId)
            throw new InvalidDataException($"Invalid ezyimg container: unknown format id '{manifest.Format}'.");
        if (manifest.SchemaVersion < 1)
            throw new InvalidDataException($"Invalid ezyimg container: schema version {manifest.SchemaVersion}.");
        if (manifest.SchemaVersion > ProjectManifest.CurrentSchemaVersion)
            throw new NotSupportedException(
                $"Project schema {manifest.SchemaVersion} is newer than supported {ProjectManifest.CurrentSchemaVersion}.");

        var documentEntry = zip.GetEntry(DocumentEntryName)
            ?? throw new InvalidDataException("Invalid ezyimg container: document.json missing.");

        var assets = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string? sourceName = null;
        byte[]? sourceBytes = null;
        foreach (var entry in entries)
        {
            if (entry.FullName.StartsWith(SourcePrefix, StringComparison.Ordinal))
            {
                if (sourceName is not null)
                    throw new InvalidDataException("Multiple embedded source entries.");
                sourceName = entry.FullName[SourcePrefix.Length..];
                ValidateAssetName(sourceName);
                sourceBytes = ReadEntry(entry, options);
                continue;
            }
            if (!entry.FullName.StartsWith(AssetsPrefix, StringComparison.Ordinal))
                continue;
            var name = entry.FullName[AssetsPrefix.Length..];
            ValidateAssetName(name);
            assets[name] = ReadEntry(entry, options);
        }

        var previewEntry = zip.GetEntry(PreviewEntryName);
        return new EzyProject
        {
            Manifest = manifest,
            DocumentJson = Encoding.UTF8.GetString(ReadEntry(documentEntry, options)),
            PreviewPng = previewEntry is null ? null : ReadEntry(previewEntry, options),
            SourceName = sourceName,
            SourceBytes = sourceBytes,
            Assets = assets,
        };
    }

    /// <summary>Rejects path traversal and absolute/odd names before they touch the archive or disk.</summary>
    private static void ValidateAssetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('\\', StringComparison.Ordinal)
            || name.Contains("..", StringComparison.Ordinal)
            || name.StartsWith('/')
            || Path.IsPathRooted(name))
        {
            throw new InvalidDataException($"Invalid asset name '{name}'.");
        }
    }

    private static void WriteEntry(ZipArchive zip, string name, byte[] data,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(name, level);
        using var stream = entry.Open();
        stream.Write(data);
    }

    /// <summary>Decompresses at most the declared entry length; lying headers abort the read.</summary>
    private static byte[] ReadEntry(ZipArchiveEntry entry, EzyProjectLimits options)
    {
        var declared = Math.Min(entry.Length, options.MaxEntryBytes);
        using var stream = entry.Open();
        using var buffer = new MemoryStream(declared is > 0 and <= int.MaxValue ? (int)declared : 0);

        var chunk = new byte[81920];
        long copied = 0;
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            copied += read;
            if (copied > declared)
                throw new InvalidDataException($"Entry '{entry.FullName}' contains more data than declared.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }
}
