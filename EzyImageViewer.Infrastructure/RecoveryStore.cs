using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EzyImageViewer.Infrastructure;

public sealed record RecoveryRecord
{
    public required Guid SessionId { get; init; }
    public required Guid WindowId { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public byte[] Metadata { get; init; } = [];
    public required byte[] Payload { get; init; }
}

/// <summary>Header-only recovery candidate used during startup. The payload is authenticated
/// and allocated only after the user selects the candidate through <see cref="RecoveryStore.TryLoad"/>.</summary>
public sealed record RecoveryRecordSummary
{
    public required Guid SessionId { get; init; }
    public required Guid WindowId { get; init; }
    public required DateTimeOffset CreatedAtUtc { get; init; }
    public required DateTimeOffset UpdatedAtUtc { get; init; }
    public required int MetadataLength { get; init; }
    public required int PayloadLength { get; init; }
}

public sealed record RecoverySummaryEnumeration(
    IReadOnlyList<RecoveryRecordSummary> Summaries,
    bool IsComplete);

public sealed record CrashSessionMarker
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required Guid SessionId { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
}

public sealed record RecoveryStoreOptions
{
    public int MaximumMetadataBytes { get; init; } = 64 * 1024;
    public int MaximumPayloadBytes { get; init; } = 512 * 1024 * 1024;
    public int MaximumQuarantineFiles { get; init; } = 20;
    public long MaximumQuarantineBytes { get; init; } = 512L * 1024 * 1024;
    public TimeSpan QuarantineRetention { get; init; } = TimeSpan.FromDays(30);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CrashSessionMarker))]
internal sealed partial class CrashMarkerJsonContext : JsonSerializerContext;

public sealed class RecoveryStore
{
    private const int CurrentRecoveryVersion = 1;
    private const int HeaderWithoutHashBytes = 68;
    private const int HeaderBytes = 100;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("EZYRCV1\0");

    private readonly IAppDataPaths _paths;
    private readonly RecoveryStoreOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Action<Exception> _reportError;
    private readonly object _sync;

    private readonly record struct RecoveryHeader(
        Guid SessionId,
        Guid WindowId,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        int MetadataLength,
        int PayloadLength);

    public RecoveryStore(
        IAppDataPaths paths,
        RecoveryStoreOptions? options = null,
        TimeProvider? timeProvider = null,
        Action<Exception>? reportError = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _options = options ?? new RecoveryStoreOptions();
        if (_options.MaximumMetadataBytes is < 0 or > 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (_options.MaximumPayloadBytes is < 1 or > 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (_options.MaximumQuarantineFiles is < 1 or > 1_000)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (_options.MaximumQuarantineBytes is < 1024 * 1024
            or > 10L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options));
        if (_options.QuarantineRetention <= TimeSpan.Zero
            || _options.QuarantineRetention > TimeSpan.FromDays(3650))
            throw new ArgumentOutOfRangeException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _reportError = reportError ?? (_ => { });
        _sync = FileStoreSynchronization.ForPath(
            Path.Combine(_paths.RootDirectory, ".recovery-writer"));
    }

    public CrashSessionMarker BeginSession(Guid sessionId)
    {
        ValidateId(sessionId, nameof(sessionId));
        var marker = new CrashSessionMarker
        {
            SessionId = sessionId,
            StartedAtUtc = _timeProvider.GetUtcNow(),
        };
        lock (_sync)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                marker, CrashMarkerJsonContext.Default.CrashSessionMarker);
            AtomicFileWriter.Write(GetMarkerPath(sessionId), bytes);
        }
        return marker;
    }

    public void Save(RecoveryRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        ValidateRecord(record);
        lock (_sync)
        {
            AtomicFileWriter.Write(
                GetRecoveryPath(record.SessionId, record.WindowId),
                stream => Serialize(record, stream));
        }
    }

    public IReadOnlyList<RecoveryRecord> Enumerate()
    {
        lock (_sync)
        {
            if (!Directory.Exists(_paths.RecoveryDirectory))
                return [];
            var records = new List<RecoveryRecord>();
            foreach (var path in EnumerateFilesSafely(
                _paths.RecoveryDirectory, "*.recovery"))
            {
                try
                {
                    var record = Deserialize(path);
                    if (!StringComparer.OrdinalIgnoreCase.Equals(
                        Path.GetFullPath(path), GetRecoveryPath(record.SessionId, record.WindowId)))
                        throw new InvalidDataException("Recovery identity does not match its file name.");
                    records.Add(record);
                }
                catch (Exception ex) when (ex is InvalidDataException
                    or ArgumentException
                    or CryptographicException)
                {
                    Quarantine(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Report(ex);
                }
            }
            return records
                .OrderByDescending(record => record.UpdatedAtUtc)
                .ThenBy(record => record.SessionId)
                .ThenBy(record => record.WindowId)
                .ToList();
        }
    }

    /// <summary>Enumerates structurally valid recovery headers without reading or allocating
    /// their metadata and payload bodies. Full SHA-256 validation happens in <see cref="TryLoad"/>.</summary>
    public IReadOnlyList<RecoveryRecordSummary> EnumerateSummaries()
        => EnumerateSummaryState().Summaries;

    /// <summary>Returns whether every candidate file could be classified. Callers must not
    /// delete orphan markers from an incomplete result because a temporarily locked snapshot
    /// is deliberately omitted for retry.</summary>
    public RecoverySummaryEnumeration EnumerateSummaryState()
    {
        lock (_sync)
        {
            if (!Directory.Exists(_paths.RecoveryDirectory))
                return new RecoverySummaryEnumeration([], IsComplete: true);

            IReadOnlyList<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(
                    _paths.RecoveryDirectory,
                    "*.recovery",
                    SearchOption.TopDirectoryOnly).ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report(ex);
                return new RecoverySummaryEnumeration([], IsComplete: false);
            }

            var summaries = new List<RecoveryRecordSummary>();
            var isComplete = true;
            foreach (var path in paths)
            {
                try
                {
                    var summary = ReadSummary(path);
                    if (!StringComparer.OrdinalIgnoreCase.Equals(
                        Path.GetFullPath(path), GetRecoveryPath(summary.SessionId, summary.WindowId)))
                        throw new InvalidDataException("Recovery identity does not match its file name.");
                    summaries.Add(summary);
                }
                catch (Exception ex) when (ex is InvalidDataException
                    or ArgumentException)
                {
                    if (!Quarantine(path))
                        isComplete = false;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Report(ex);
                    isComplete = false;
                }
            }
            return new RecoverySummaryEnumeration(
                summaries
                    .OrderByDescending(summary => summary.UpdatedAtUtc)
                    .ThenBy(summary => summary.SessionId)
                    .ThenBy(summary => summary.WindowId)
                    .ToList(),
                isComplete);
        }
    }

    /// <summary>Loads and authenticates one selected recovery candidate. Corrupt or concurrently
    /// replaced content is quarantined and reported as unavailable.</summary>
    public RecoveryRecord? TryLoad(Guid sessionId, Guid windowId)
    {
        ValidateId(sessionId, nameof(sessionId));
        ValidateId(windowId, nameof(windowId));
        lock (_sync)
        {
            var path = GetRecoveryPath(sessionId, windowId);
            if (!File.Exists(path))
                return null;
            try
            {
                var record = Deserialize(path);
                if (record.SessionId != sessionId || record.WindowId != windowId)
                    throw new InvalidDataException("Recovery identity changed while loading.");
                return record;
            }
            catch (Exception ex) when (ex is InvalidDataException
                or ArgumentException
                or CryptographicException)
            {
                Quarantine(path);
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report(ex);
                return null;
            }
        }
    }

    public IReadOnlyList<CrashSessionMarker> EnumerateCrashMarkers()
    {
        lock (_sync)
        {
            if (!Directory.Exists(_paths.CrashMarkersDirectory))
                return [];
            var markers = new List<CrashSessionMarker>();
            foreach (var path in EnumerateFilesSafely(
                _paths.CrashMarkersDirectory, "*.marker.json"))
            {
                try
                {
                    if (new FileInfo(path).Length > 16 * 1024)
                        throw new InvalidDataException("Crash marker is too large.");
                    var marker = JsonSerializer.Deserialize(
                        File.ReadAllText(path), CrashMarkerJsonContext.Default.CrashSessionMarker);
                    if (marker is null
                        || marker.SchemaVersion != CrashSessionMarker.CurrentSchemaVersion
                        || marker.SessionId == Guid.Empty
                        || marker.StartedAtUtc.Offset != TimeSpan.Zero
                        || !StringComparer.OrdinalIgnoreCase.Equals(
                            Path.GetFullPath(path), GetMarkerPath(marker.SessionId)))
                        throw new InvalidDataException("Crash marker is invalid.");
                    markers.Add(marker);
                }
                catch (Exception ex) when (ex is InvalidDataException
                    or JsonException
                    or ArgumentException)
                {
                    Quarantine(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Report(ex);
                }
            }
            return markers.OrderBy(marker => marker.StartedAtUtc).ToList();
        }
    }

    public void ClearWindow(Guid sessionId, Guid windowId)
    {
        ValidateId(sessionId, nameof(sessionId));
        ValidateId(windowId, nameof(windowId));
        lock (_sync)
            DeleteIfPresent(GetRecoveryPath(sessionId, windowId));
    }

    /// <summary>Deletes only the candidates the user actually saw. Session-wide cleanup is
    /// allowed only after a fresh complete enumeration proves that the session has no remaining
    /// checkpoint.</summary>
    public RecoverySummaryEnumeration DiscardCandidates(
        IReadOnlyList<RecoveryRecordSummary> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var identities = new HashSet<(Guid SessionId, Guid WindowId)>();
        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ValidateId(candidate.SessionId, nameof(candidates));
            ValidateId(candidate.WindowId, nameof(candidates));
            identities.Add((candidate.SessionId, candidate.WindowId));
        }

        lock (_sync)
        {
            foreach (var identity in identities)
                DeleteIfPresent(GetRecoveryPath(identity.SessionId, identity.WindowId));

            var remaining = EnumerateSummaryState();
            if (!remaining.IsComplete)
                return remaining;

            var remainingSessions = remaining.Summaries
                .Select(summary => summary.SessionId)
                .ToHashSet();
            foreach (var sessionId in identities
                .Select(identity => identity.SessionId)
                .Distinct())
            {
                if (!remainingSessions.Contains(sessionId))
                    CompleteSession(sessionId);
            }
            return remaining;
        }
    }

    public void CompleteSession(Guid sessionId)
    {
        ValidateId(sessionId, nameof(sessionId));
        lock (_sync)
        {
            if (Directory.Exists(_paths.RecoveryDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(
                    _paths.RecoveryDirectory,
                    $"{sessionId:N}-*.recovery",
                    SearchOption.TopDirectoryOnly))
                    DeleteIfPresent(path);
            }
            DeleteIfPresent(GetMarkerPath(sessionId));
        }
    }

    private static void Serialize(RecoveryRecord record, Stream stream)
    {
        Span<byte> prefix = stackalloc byte[HeaderWithoutHashBytes];
        Magic.CopyTo(prefix);
        BinaryPrimitives.WriteInt32LittleEndian(prefix[8..12], CurrentRecoveryVersion);
        _ = record.SessionId.TryWriteBytes(prefix[12..28]);
        _ = record.WindowId.TryWriteBytes(prefix[28..44]);
        BinaryPrimitives.WriteInt64LittleEndian(prefix[44..52], record.CreatedAtUtc.UtcTicks);
        BinaryPrimitives.WriteInt64LittleEndian(prefix[52..60], record.UpdatedAtUtc.UtcTicks);
        BinaryPrimitives.WriteInt32LittleEndian(prefix[60..64], record.Metadata.Length);
        BinaryPrimitives.WriteInt32LittleEndian(prefix[64..68], record.Payload.Length);

        stream.Write(prefix);
        Span<byte> hashPlaceholder = stackalloc byte[32];
        hashPlaceholder.Clear();
        stream.Write(hashPlaceholder);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(prefix);
        WriteAndHash(stream, record.Metadata, hash);
        WriteAndHash(stream, record.Payload, hash);
        var endPosition = stream.Position;
        stream.Position = HeaderWithoutHashBytes;
        stream.Write(hash.GetHashAndReset());
        stream.Position = endPosition;
    }

    private RecoveryRecord Deserialize(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[HeaderBytes];
        var parsed = ReadHeader(stream, header);

        var metadata = GC.AllocateUninitializedArray<byte>(parsed.MetadataLength);
        var payload = GC.AllocateUninitializedArray<byte>(parsed.PayloadLength);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(header[..HeaderWithoutHashBytes]);
        ReadAndHash(stream, metadata, hash);
        ReadAndHash(stream, payload, hash);
        if (!CryptographicOperations.FixedTimeEquals(
            header[HeaderWithoutHashBytes..HeaderBytes],
            hash.GetHashAndReset()))
            throw new InvalidDataException("Recovery integrity check failed.");

        var record = new RecoveryRecord
        {
            SessionId = parsed.SessionId,
            WindowId = parsed.WindowId,
            CreatedAtUtc = parsed.CreatedAtUtc,
            UpdatedAtUtc = parsed.UpdatedAtUtc,
            Metadata = metadata,
            Payload = payload,
        };
        ValidateRecord(record);
        return record;
    }

    private RecoveryRecordSummary ReadSummary(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            HeaderBytes,
            FileOptions.SequentialScan);
        Span<byte> header = stackalloc byte[HeaderBytes];
        var parsed = ReadHeader(stream, header);
        return new RecoveryRecordSummary
        {
            SessionId = parsed.SessionId,
            WindowId = parsed.WindowId,
            CreatedAtUtc = parsed.CreatedAtUtc,
            UpdatedAtUtc = parsed.UpdatedAtUtc,
            MetadataLength = parsed.MetadataLength,
            PayloadLength = parsed.PayloadLength,
        };
    }

    private RecoveryHeader ReadHeader(Stream stream, Span<byte> header)
    {
        var maximumLength = checked((long)HeaderBytes
            + _options.MaximumMetadataBytes
            + _options.MaximumPayloadBytes);
        if (stream.Length is < HeaderBytes || stream.Length > maximumLength)
            throw new InvalidDataException("Recovery file size is invalid.");
        stream.ReadExactly(header);
        if (!header[..Magic.Length].SequenceEqual(Magic))
            throw new InvalidDataException("Recovery magic is invalid.");
        if (BinaryPrimitives.ReadInt32LittleEndian(header[8..12]) != CurrentRecoveryVersion)
            throw new InvalidDataException("Recovery version is unsupported.");

        var createdAt = ReadUtcTimestamp(BinaryPrimitives.ReadInt64LittleEndian(header[44..52]));
        var updatedAt = ReadUtcTimestamp(BinaryPrimitives.ReadInt64LittleEndian(header[52..60]));
        var metadataLength = BinaryPrimitives.ReadInt32LittleEndian(header[60..64]);
        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(header[64..68]);
        var sessionId = new Guid(header[12..28]);
        var windowId = new Guid(header[28..44]);
        if (sessionId == Guid.Empty
            || windowId == Guid.Empty
            || metadataLength is < 0 || metadataLength > _options.MaximumMetadataBytes
            || payloadLength is < 1 || payloadLength > _options.MaximumPayloadBytes
            || stream.Length != checked((long)HeaderBytes + metadataLength + payloadLength)
            || updatedAt < createdAt)
            throw new InvalidDataException("Recovery header is invalid.");

        return new RecoveryHeader(
            sessionId,
            windowId,
            createdAt,
            updatedAt,
            metadataLength,
            payloadLength);
    }

    private void ValidateRecord(RecoveryRecord record)
    {
        ValidateId(record.SessionId, nameof(record.SessionId));
        ValidateId(record.WindowId, nameof(record.WindowId));
        ArgumentNullException.ThrowIfNull(record.Metadata);
        ArgumentNullException.ThrowIfNull(record.Payload);
        if (record.Metadata.Length > _options.MaximumMetadataBytes)
            throw new ArgumentException("Recovery metadata is too large.", nameof(record));
        if (record.Payload.Length is < 1 || record.Payload.Length > _options.MaximumPayloadBytes)
            throw new ArgumentException("Recovery payload size is invalid.", nameof(record));
        if (record.CreatedAtUtc.Offset != TimeSpan.Zero
            || record.UpdatedAtUtc.Offset != TimeSpan.Zero
            || record.UpdatedAtUtc < record.CreatedAtUtc)
            throw new ArgumentException("Recovery timestamps are invalid.", nameof(record));
    }

    private bool Quarantine(string path)
    {
        try
        {
            Directory.CreateDirectory(_paths.RecoveryQuarantineDirectory);
            var destination = Path.Combine(
                _paths.RecoveryQuarantineDirectory,
                $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.corrupt");
            File.Move(path, destination);
            try
            {
                File.SetLastWriteTimeUtc(
                    destination,
                    _timeProvider.GetUtcNow().UtcDateTime);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report(ex);
            }
            CleanupQuarantine();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(ex);
            return false;
        }
    }

    private void CleanupQuarantine()
    {
        FileInfo[] files;
        try
        {
            files = Directory.EnumerateFiles(
                    _paths.RecoveryQuarantineDirectory,
                    "*.corrupt",
                    SearchOption.TopDirectoryOnly)
                .Select(path => new FileInfo(path))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(ex);
            return;
        }

        var cutoff = _timeProvider.GetUtcNow() - _options.QuarantineRetention;
        var retainedFiles = 0;
        long retainedBytes = 0;
        foreach (var file in files
            .OrderByDescending(value => value.LastWriteTimeUtc)
            .ThenBy(value => value.Name, StringComparer.Ordinal))
        {
            try
            {
                var lastWrite = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);
                var length = file.Length;
                var canRetain = lastWrite >= cutoff
                    && retainedFiles < _options.MaximumQuarantineFiles
                    && length <= _options.MaximumQuarantineBytes - retainedBytes;
                if (canRetain)
                {
                    retainedFiles++;
                    retainedBytes += length;
                }
                else
                {
                    file.Delete();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Report(ex);
            }
        }
    }

    private IReadOnlyList<string> EnumerateFilesSafely(string directory, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(
                directory, pattern, SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(ex);
            return [];
        }
    }

    private void Report(Exception exception)
    {
        try
        {
            _reportError(exception);
        }
        catch
        {
        }
    }

    private string GetRecoveryPath(Guid sessionId, Guid windowId)
    {
        return Path.Combine(
            _paths.RecoveryDirectory,
            $"{sessionId:N}-{windowId:N}.recovery");
    }

    private string GetMarkerPath(Guid sessionId)
    {
        return Path.Combine(_paths.CrashMarkersDirectory, $"{sessionId:N}.marker.json");
    }

    private static void ReadAndHash(
        Stream stream,
        byte[] destination,
        IncrementalHash hash)
    {
        const int chunkBytes = 64 * 1024;
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = stream.Read(
                destination.AsSpan(offset, Math.Min(chunkBytes, destination.Length - offset)));
            if (read == 0)
                throw new EndOfStreamException();
            hash.AppendData(destination.AsSpan(offset, read));
            offset += read;
        }
    }

    private static void WriteAndHash(
        Stream stream,
        byte[] source,
        IncrementalHash hash)
    {
        const int chunkBytes = 64 * 1024;
        if (source.Length == 0)
            return;

        var buffer = ArrayPool<byte>.Shared.Rent(Math.Min(chunkBytes, source.Length));
        try
        {
            var offset = 0;
            while (offset < source.Length)
            {
                var count = Math.Min(chunkBytes, source.Length - offset);
                source.AsSpan(offset, count).CopyTo(buffer);
                var chunk = buffer.AsSpan(0, count);
                hash.AppendData(chunk);
                stream.Write(chunk);
                offset += count;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static DateTimeOffset ReadUtcTimestamp(long ticks)
    {
        try
        {
            return new DateTimeOffset(ticks, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidDataException("Recovery timestamp is invalid.", ex);
        }
    }

    private static void ValidateId(Guid value, string parameterName)
    {
        if (value == Guid.Empty)
            throw new ArgumentException("Recovery identifiers cannot be empty.", parameterName);
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
            File.Delete(path);
    }
}
