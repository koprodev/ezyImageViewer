using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace EzyImageViewer.Core.Documents;

public sealed record ProjectManifest(string Format, int SchemaVersion, string AppVersion, DateTimeOffset CreatedUtc)
{
    public const string FormatId = "ezyimg";
    // v3: document.json에 페이지별 v2 문서 조각 보관.
    public const int CurrentSchemaVersion = 3;

    public static ProjectManifest Create(string appVersion) =>
        new(FormatId, CurrentSchemaVersion, appVersion, DateTimeOffset.UtcNow);
}

/// <summary>컨테이너 양방향 상한. 쓰기와 읽기가 같은 제한을 적용해 자가 거절 파일 생성 방지.</summary>
public sealed record EzyProjectLimits
{
    public static EzyProjectLimits Default { get; } = new();

    public int MaxEntryCount { get; init; } = 1024;
    /// <summary>프로젝트가 원본 바이트를 내장하므로 로더 최대 파일 크기와 맞춘 상한.</summary>
    public long MaxEntryBytes { get; init; } = Imaging.InputLimits.Default.MaxFileBytes;
    public long MaxTotalBytes { get; init; } = 1024L * 1024 * 1024;
}

public sealed class EzyProject
{
    public required ProjectManifest Manifest { get; init; }
    public required string DocumentJson { get; init; }
    public byte[]? PreviewPng { get; init; }
    /// <summary>내장 배경 원본. 실제 확장자를 이름에 남겨 열 때 다시 형식 판별.</summary>
    public string? SourceName { get; init; }
    public byte[]? SourceBytes { get; init; }
    public IReadOnlyDictionary<string, byte[]> Assets { get; init; } = new Dictionary<string, byte[]>();
}

/// <summary>
/// .ezyimg는 맨 앞 비압축 mimetype으로 전체 해제 없이 판별하는 ZIP 컨테이너.
/// 구성: mimetype, manifest.json, document.json, preview.png?, assets/*.
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

        // 모든 항목을 먼저 생성해 못 읽을 프로젝트는 스트림에 한 바이트도 쓰기 전에 거절.
        var entries = new List<(string Name, byte[] Data, CompressionLevel Level)>
        {
            // mimetype은 판별 위치를 고정하려고 첫 항목·비압축 저장.
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

        // 중복 이름은 ZIP 파서마다 필수 항목 해석을 흐림.
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

    /// <summary>아카이브·디스크 접근 전에 경로 순회와 절대·이상 이름 거절.</summary>
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

    /// <summary>선언 길이까지만 압축 해제. 거짓 헤더면 읽기 중단.</summary>
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
