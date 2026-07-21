using System.Text.Json;
using System.Text.Json.Serialization;

namespace EzyImageViewer.Infrastructure;

public sealed record RecentFileEntry(string Path, DateTimeOffset LastOpenedUtc);

internal sealed record RecentFileDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public List<RecentFileEntry> Entries { get; init; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(RecentFileDocument))]
internal sealed partial class RecentFileJsonContext : JsonSerializerContext;

public sealed class RecentFileStore
{
    public const int DefaultCapacity = 20;
    private const int MaximumCapacity = 100;
    private const int MaximumStoreBytes = 1024 * 1024;

    private readonly string _path;
    private readonly int _capacity;
    private readonly Func<string, bool> _fileExists;
    private readonly TimeProvider _timeProvider;
    private readonly object _sync;

    public RecentFileStore(
        IAppDataPaths paths,
        int capacity = DefaultCapacity,
        Func<string, bool>? fileExists = null,
        TimeProvider? timeProvider = null)
        : this(
            (paths ?? throw new ArgumentNullException(nameof(paths))).RecentFilesFile,
            capacity,
            fileExists,
            timeProvider)
    {
    }

    public RecentFileStore(
        string path,
        int capacity = DefaultCapacity,
        Func<string, bool>? fileExists = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (capacity is < 1 or > MaximumCapacity)
            throw new ArgumentOutOfRangeException(nameof(capacity));
        _path = Path.GetFullPath(path);
        _capacity = capacity;
        _fileExists = fileExists ?? File.Exists;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _sync = FileStoreSynchronization.ForPath(_path);
    }

    public IReadOnlyList<RecentFileEntry> Load(bool enabled)
    {
        lock (_sync)
        {
            if (!enabled)
            {
                ClearCore();
                return [];
            }

            var loaded = LoadCore();
            var pruned = loaded
                .Where(entry => SafeFileExists(entry.Path))
                .Take(_capacity)
                .ToList();
            if (pruned.Count != loaded.Count)
                SaveCore(pruned);
            return pruned;
        }
    }

    public void Add(string path, bool enabled)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (fullPath.Length > 32_767)
            throw new ArgumentException("The recent-file path is too long.", nameof(path));

        lock (_sync)
        {
            if (!enabled)
            {
                ClearCore();
                return;
            }
            if (!SafeFileExists(fullPath))
                return;

            var entries = LoadCore()
                .Where(entry => SafeFileExists(entry.Path)
                    && !StringComparer.OrdinalIgnoreCase.Equals(entry.Path, fullPath))
                .ToList();
            entries.Insert(0, new RecentFileEntry(fullPath, _timeProvider.GetUtcNow()));
            if (entries.Count > _capacity)
                entries.RemoveRange(_capacity, entries.Count - _capacity);
            SaveCore(entries);
        }
    }

    public void DisableAndClear()
    {
        lock (_sync)
            ClearCore();
    }

    private List<RecentFileEntry> LoadCore()
    {
        try
        {
            if (!File.Exists(_path))
                return [];
            if (new FileInfo(_path).Length > MaximumStoreBytes)
                return [];

            var document = JsonSerializer.Deserialize(
                File.ReadAllText(_path), RecentFileJsonContext.Default.RecentFileDocument);
            if (document is not { SchemaVersion: RecentFileDocument.CurrentSchemaVersion }
                || document.Entries.Count > MaximumCapacity)
                return [];

            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var validated = new List<RecentFileEntry>(document.Entries.Count);
            foreach (var entry in document.Entries)
            {
                if (!TryValidate(entry, out var normalized) || !unique.Add(normalized.Path))
                    return [];
                validated.Add(normalized);
            }
            return validated;
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException
            or ArgumentException
            or NotSupportedException)
        {
            return [];
        }
    }

    private void SaveCore(List<RecentFileEntry> entries)
    {
        var document = new RecentFileDocument { Entries = entries };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            document, RecentFileJsonContext.Default.RecentFileDocument);
        if (bytes.Length > MaximumStoreBytes)
            throw new IOException("The recent-file store exceeded its size limit.");
        AtomicFileWriter.Write(_path, bytes);
    }

    private void ClearCore()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private bool SafeFileExists(string path)
    {
        try
        {
            return _fileExists(path);
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryValidate(RecentFileEntry? entry, out RecentFileEntry normalized)
    {
        normalized = null!;
        if (entry is null
            || string.IsNullOrWhiteSpace(entry.Path)
            || entry.Path.Length > 32_767
            || entry.LastOpenedUtc.Offset != TimeSpan.Zero)
            return false;
        try
        {
            var fullPath = Path.GetFullPath(entry.Path);
            if (!Path.IsPathFullyQualified(fullPath))
                return false;
            normalized = entry with { Path = fullPath };
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException
            or NotSupportedException
            or PathTooLongException)
        {
            return false;
        }
    }
}
