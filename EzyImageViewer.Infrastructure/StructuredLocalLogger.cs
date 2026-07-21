using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EzyImageViewer.Infrastructure;

public enum LocalLogLevel
{
    Debug,
    Information,
    Warning,
    Error,
}

public sealed record StructuredLogEvent
{
    public required string Name { get; init; }
    public string? ErrorCode { get; init; }
    public string? Renderer { get; init; }
    public string? Format { get; init; }
    public long? ElapsedMilliseconds { get; init; }
    public string? DocumentPath { get; init; }
}

public sealed record StructuredLocalLoggerOptions
{
    public LocalLogLevel MinimumLevel { get; init; } = LocalLogLevel.Information;
    public int MaximumFileBytes { get; init; } = 1024 * 1024;
    public int MaximumFiles { get; init; } = 5;
    public TimeSpan Retention { get; init; } = TimeSpan.FromDays(7);
    public string ApplicationVersion { get; init; } = "unknown";
    public string OperatingSystem { get; init; } = RuntimeInformation.OSDescription;
}

internal sealed record PersistedLogEntry
{
    public required DateTimeOffset TimestampUtc { get; init; }
    public required string Level { get; init; }
    public required string EventName { get; init; }
    public required string ApplicationVersion { get; init; }
    public required string OperatingSystem { get; init; }
    public string? ErrorCode { get; init; }
    public string? Renderer { get; init; }
    public string? Format { get; init; }
    public long? ElapsedMilliseconds { get; init; }
    public string? DocumentPathId { get; init; }
    public string? ExceptionType { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(PersistedLogEntry))]
internal sealed partial class StructuredLogJsonContext : JsonSerializerContext;

public sealed class PrivacyPathProtector
{
    private readonly byte[] _key;

    public PrivacyPathProtector(ReadOnlySpan<byte> key)
    {
        if (key.Length is < 16 or > 128)
            throw new ArgumentOutOfRangeException(nameof(key));
        _key = key.ToArray();
    }

    public static PrivacyPathProtector CreateRandom()
    {
        return new PrivacyPathProtector(RandomNumberGenerator.GetBytes(32));
    }

    public string HashPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var normalized = Path.GetFullPath(path).Normalize(NormalizationForm.FormC);
        var bytes = Encoding.UTF8.GetBytes(normalized.ToUpperInvariant());
        var hash = HMACSHA256.HashData(_key, bytes);
        return $"path-{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (LooksLikePath(value))
            return "<path:redacted>";
        return value;
    }

    private static bool LooksLikePath(string value)
    {
        if (value.Contains("file://", StringComparison.OrdinalIgnoreCase)
            || value.Contains(new string(Path.DirectorySeparatorChar, 2), StringComparison.Ordinal)
            || value.Contains($":{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            return true;
        try
        {
            return Path.IsPathFullyQualified(value);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return true;
        }
    }
}

public sealed class StructuredLocalLogger
{
    private const int MinimumFileBytes = 512;
    private const int MaximumFileBytes = 64 * 1024 * 1024;
    private readonly string _directory;
    private readonly StructuredLocalLoggerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly PrivacyPathProtector _pathProtector;
    private readonly object _sync;

    public StructuredLocalLogger(
        IAppDataPaths paths,
        StructuredLocalLoggerOptions? options = null,
        TimeProvider? timeProvider = null,
        PrivacyPathProtector? pathProtector = null)
        : this(
            (paths ?? throw new ArgumentNullException(nameof(paths))).LogsDirectory,
            options,
            timeProvider,
            pathProtector)
    {
    }

    public StructuredLocalLogger(
        string directory,
        StructuredLocalLoggerOptions? options = null,
        TimeProvider? timeProvider = null,
        PrivacyPathProtector? pathProtector = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _options = options ?? new StructuredLocalLoggerOptions();
        ValidateOptions(_options);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pathProtector = pathProtector ?? PrivacyPathProtector.CreateRandom();
        _sync = FileStoreSynchronization.ForPath(Path.Combine(_directory, ".writer"));
    }

    public bool TryWrite(
        LocalLogLevel level,
        StructuredLogEvent logEvent,
        Exception? exception = null)
    {
        if (!Enum.IsDefined(level))
            throw new ArgumentOutOfRangeException(nameof(level));
        ArgumentNullException.ThrowIfNull(logEvent);
        if (level < _options.MinimumLevel)
            return true;

        var record = CreatePersistedEntry(level, logEvent, exception);
        var serialized = JsonSerializer.SerializeToUtf8Bytes(
            record, StructuredLogJsonContext.Default.PersistedLogEntry);
        if (serialized.Length + 1 > _options.MaximumFileBytes)
            throw new ArgumentException("The structured log event exceeds the file limit.", nameof(logEvent));

        lock (_sync)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var path = SelectWritableLogPath(record.TimestampUtc, serialized.Length + 1);
                using (var stream = new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.Read,
                    4096,
                    FileOptions.WriteThrough))
                {
                    stream.Write(serialized);
                    stream.WriteByte(10);
                    stream.Flush(flushToDisk: true);
                }
                ApplyRetention(record.TimestampUtc);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private PersistedLogEntry CreatePersistedEntry(
        LocalLogLevel level,
        StructuredLogEvent logEvent,
        Exception? exception)
    {
        if (logEvent.ElapsedMilliseconds is < 0)
            throw new ArgumentOutOfRangeException(nameof(logEvent));
        return new PersistedLogEntry
        {
            TimestampUtc = _timeProvider.GetUtcNow(),
            Level = level.ToString(),
            EventName = ValidateToken(logEvent.Name, nameof(logEvent.Name), 64),
            ApplicationVersion = ValidateToken(
                _options.ApplicationVersion, nameof(_options.ApplicationVersion), 64),
            OperatingSystem = ValidateDiagnosticText(
                _options.OperatingSystem, nameof(_options.OperatingSystem), 256),
            ErrorCode = ValidateOptionalToken(logEvent.ErrorCode, nameof(logEvent.ErrorCode), 64),
            Renderer = ValidateOptionalToken(logEvent.Renderer, nameof(logEvent.Renderer), 64),
            Format = ValidateOptionalToken(logEvent.Format, nameof(logEvent.Format), 32),
            ElapsedMilliseconds = logEvent.ElapsedMilliseconds,
            DocumentPathId = logEvent.DocumentPath is null
                ? null
                : _pathProtector.HashPath(logEvent.DocumentPath),
            ExceptionType = exception is null
                ? null
                : ValidateToken(exception.GetType().FullName ?? exception.GetType().Name,
                    nameof(exception), 128),
        };
    }

    private string SelectWritableLogPath(DateTimeOffset timestampUtc, int entryBytes)
    {
        var prefix = $"ezy-{timestampUtc:yyyyMMdd}-";
        var candidates = Directory.EnumerateFiles(_directory, $"{prefix}*.jsonl")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (candidates.Count > 0)
        {
            var last = candidates[^1];
            if (new FileInfo(last).Length + entryBytes <= _options.MaximumFileBytes)
                return last;
        }

        var sequence = 0;
        while (true)
        {
            var candidate = Path.Combine(_directory, $"{prefix}{sequence:D3}.jsonl");
            if (!File.Exists(candidate))
                return candidate;
            sequence++;
        }
    }

    private void ApplyRetention(DateTimeOffset nowUtc)
    {
        var cutoff = nowUtc.UtcDateTime - _options.Retention;
        foreach (var path in Directory.EnumerateFiles(_directory, "ezy-*.jsonl"))
        {
            if (File.GetLastWriteTimeUtc(path) < cutoff)
                TryDelete(path);
        }

        var retained = Directory.EnumerateFiles(_directory, "ezy-*.jsonl")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .ThenByDescending(path => path, StringComparer.OrdinalIgnoreCase)
            .Skip(_options.MaximumFiles)
            .ToList();
        foreach (var path in retained)
            TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string? ValidateOptionalToken(string? value, string parameterName, int maxLength)
    {
        return value is null ? null : ValidateToken(value, parameterName, maxLength);
    }

    private static string ValidateToken(string value, string parameterName, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            throw new ArgumentException("A log token is empty or too long.", parameterName);
        foreach (var character in value)
        {
            if (!(char.IsAsciiLetterOrDigit(character)
                || character is '.' or '_' or '-'))
                throw new ArgumentException("A log token contains unsafe characters.", parameterName);
        }
        return value;
    }

    private static string ValidateDiagnosticText(
        string value,
        string parameterName,
        int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength)
            throw new ArgumentException("A diagnostic value is empty or too long.", parameterName);
        return value;
    }

    private static void ValidateOptions(StructuredLocalLoggerOptions options)
    {
        if (!Enum.IsDefined(options.MinimumLevel))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaximumFileBytes is < MinimumFileBytes or > MaximumFileBytes)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.MaximumFiles is < 1 or > 30)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (options.Retention < TimeSpan.FromHours(1)
            || options.Retention > TimeSpan.FromDays(90))
            throw new ArgumentOutOfRangeException(nameof(options));
        _ = ValidateToken(options.ApplicationVersion, nameof(options.ApplicationVersion), 64);
        _ = ValidateDiagnosticText(options.OperatingSystem, nameof(options.OperatingSystem), 256);
    }
}
