using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EzyImageViewer.Infrastructure;

public sealed record StartupHealthStatus
{
    public string? Fingerprint { get; init; }
    public int ConsecutiveFailures { get; init; }
    public DateTimeOffset? LastFailureUtc { get; init; }
    public bool ShouldOfferSafeMode =>
        ConsecutiveFailures >= StartupHealthTracker.SafeModeThreshold;
}

public sealed record StartupHealthTrackerOptions
{
    public TimeSpan RepetitionWindow { get; init; } = TimeSpan.FromDays(7);
    public int MaximumConsecutiveFailures { get; init; } = 100;
}

internal sealed record StartupHealthDocument
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public required string Fingerprint { get; init; }
    public required int ConsecutiveFailures { get; init; }
    public required DateTimeOffset LastFailureUtc { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(StartupHealthDocument))]
internal sealed partial class StartupHealthJsonContext : JsonSerializerContext;

    /// <summary>SHA-256 실패 지문과 제한된 UTC 메타데이터만 기록.
    /// 시작 복구에 예외 문구·경로·문서 내용이 남지 않게 함.</summary>
public sealed class StartupHealthTracker
{
    public const int SafeModeThreshold = 2;

    private const int MaximumStoreBytes = 4 * 1024;
    private const int MaximumStackFrames = 64;
    private const int LockAttemptCount = 40;
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(25);
    private static readonly StartupHealthStatus HealthyStatus = new();

    private readonly string _path;
    private readonly string _lockPath;
    private readonly StartupHealthTrackerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Action<Exception> _reportError;
    private readonly object _sync;

    public StartupHealthTracker(
        IAppDataPaths paths,
        StartupHealthTrackerOptions? options = null,
        TimeProvider? timeProvider = null,
        Action<Exception>? reportError = null)
        : this(
            (paths ?? throw new ArgumentNullException(nameof(paths))).StartupHealthFile,
            options,
            timeProvider,
            reportError)
    {
    }

    public StartupHealthTracker(
        string path,
        StartupHealthTrackerOptions? options = null,
        TimeProvider? timeProvider = null,
        Action<Exception>? reportError = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _lockPath = $"{_path}.lock";
        _options = options ?? new StartupHealthTrackerOptions();
        if (_options.RepetitionWindow <= TimeSpan.Zero
            || _options.RepetitionWindow > TimeSpan.FromDays(365))
            throw new ArgumentOutOfRangeException(nameof(options));
        if (_options.MaximumConsecutiveFailures is < SafeModeThreshold or > 10_000)
            throw new ArgumentOutOfRangeException(nameof(options));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _reportError = reportError ?? (_ => { });
        _sync = FileStoreSynchronization.ForPath(_path);
    }

    public StartupHealthStatus GetStatus()
    {
        lock (_sync)
        {
            try
            {
                using var lease = AcquireCrossProcessLock();
                return ToStatus(LoadCurrentDocument());
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or TimeoutException)
            {
                Report(ex);
                return HealthyStatus;
            }
        }
    }

    public StartupHealthStatus RecordUnhandledException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        lock (_sync)
        {
            try
            {
                var fingerprint = ComputeFingerprint(exception);
                using var lease = AcquireCrossProcessLock();
                var now = _timeProvider.GetUtcNow().ToUniversalTime();
                var previous = LoadCurrentDocument(now);
                var count = previous is not null
                    && StringComparer.Ordinal.Equals(previous.Fingerprint, fingerprint)
                        ? Math.Min(
                            previous.ConsecutiveFailures + 1,
                            _options.MaximumConsecutiveFailures)
                        : 1;
                var document = new StartupHealthDocument
                {
                    Fingerprint = fingerprint,
                    ConsecutiveFailures = count,
                    LastFailureUtc = now,
                };
                SaveCore(document);
                return ToStatus(document);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or TimeoutException)
            {
                Report(ex);
                return HealthyStatus;
            }
        }
    }

    public void MarkHealthy()
    {
        lock (_sync)
        {
            try
            {
                using var lease = AcquireCrossProcessLock();
                DeleteStateCore();
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or TimeoutException)
            {
                Report(ex);
            }
        }
    }

    private StartupHealthDocument? LoadCurrentDocument() =>
        LoadCurrentDocument(_timeProvider.GetUtcNow().ToUniversalTime());

    private StartupHealthDocument? LoadCurrentDocument(DateTimeOffset now)
    {
        if (!File.Exists(_path))
            return null;

        StartupHealthDocument? document;
        try
        {
            var bytes = ReadBoundedBytes(_path);
            document = JsonSerializer.Deserialize(
                bytes, StartupHealthJsonContext.Default.StartupHealthDocument);
        }
        catch (Exception ex) when (ex is JsonException
            or NotSupportedException
            or InvalidDataException)
        {
            DiscardInvalidState(ex);
            return null;
        }

        if (document is null || !IsValid(document))
        {
            DiscardInvalidState(new InvalidDataException(
                "The startup-health state is invalid or uses an unknown schema."));
            return null;
        }

        if (document.LastFailureUtc > now
            || now - document.LastFailureUtc > _options.RepetitionWindow)
        {
            DeleteStateCore();
            return null;
        }

        return document;
    }

    private void SaveCore(StartupHealthDocument document)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            document, StartupHealthJsonContext.Default.StartupHealthDocument);
        if (bytes.Length > MaximumStoreBytes)
            throw new IOException("The startup-health state exceeded its size limit.");
        AtomicFileWriter.Write(_path, bytes, AtomicFileProtection.CurrentUserAndSystem);
    }

    private FileStream AcquireCrossProcessLock()
    {
        var directory = Path.GetDirectoryName(_lockPath);
        if (string.IsNullOrEmpty(directory))
            throw new IOException("The startup-health lock directory could not be resolved.");
        Directory.CreateDirectory(directory);

        IOException? lastError = null;
        for (var attempt = 0; attempt < LockAttemptCount; attempt++)
        {
            try
            {
                return new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException ex)
            {
                lastError = ex;
                if (attempt + 1 < LockAttemptCount)
                    Thread.Sleep(LockRetryDelay);
            }
        }

        throw new TimeoutException(
            "Timed out while acquiring the startup-health state lock.", lastError);
    }

    private void DiscardInvalidState(Exception reason)
    {
        Report(reason);
        try
        {
            DeleteStateCore();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report(ex);
        }
    }

    private void DeleteStateCore()
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

    private void Report(Exception exception)
    {
        try
        {
            _reportError(exception);
        }
        catch
        {
            // 진단 때문에 시작 복구가 또 다른 시작 실패가 되면 곤란함.
        }
    }

    private static StartupHealthStatus ToStatus(StartupHealthDocument? document) =>
        document is null
            ? HealthyStatus
            : new StartupHealthStatus
            {
                Fingerprint = document.Fingerprint,
                ConsecutiveFailures = document.ConsecutiveFailures,
                LastFailureUtc = document.LastFailureUtc,
            };

    private bool IsValid(StartupHealthDocument document)
    {
        return document.SchemaVersion == StartupHealthDocument.CurrentSchemaVersion
            && document.Fingerprint is { Length: 64 } fingerprint
            && document.ConsecutiveFailures >= 1
            && document.ConsecutiveFailures <= _options.MaximumConsecutiveFailures
            && document.LastFailureUtc.Offset == TimeSpan.Zero
            && fingerprint.All(Uri.IsHexDigit);
    }

    private static byte[] ReadBoundedBytes(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096);
        var bytes = new byte[MaximumStoreBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = stream.Read(bytes, length, bytes.Length - length);
            if (read == 0)
                break;
            length += read;
        }
        if (length == 0 || length > MaximumStoreBytes || stream.ReadByte() != -1)
            throw new InvalidDataException("The startup-health state size is invalid.");
        return bytes.AsSpan(0, length).ToArray();
    }

    private static string ComputeFingerprint(Exception exception)
    {
        var identity = new StringBuilder(512);
        AppendTypeIdentity(identity, exception.GetType());

        var frames = new StackTrace(exception, fNeedFileInfo: false).GetFrames();
        if (frames is null || frames.Length == 0)
        {
            identity.Append("\n<no-stack>");
        }
        else
        {
            foreach (var frame in frames.Take(MaximumStackFrames))
            {
                var method = frame.GetMethod();
                if (method is null)
                    continue;
                identity.Append('\n');
                AppendMethodIdentity(identity, method);
            }
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString()));
        return Convert.ToHexString(hash);
    }

    private static void AppendMethodIdentity(StringBuilder target, MethodBase method)
    {
        AppendTypeIdentity(target, method.DeclaringType);
        target.Append("::").Append(method.Name);
        if (method.IsGenericMethod)
            target.Append("#g").Append(method.GetGenericArguments().Length);
        target.Append('(');
        var parameters = method.GetParameters();
        for (var index = 0; index < parameters.Length; index++)
        {
            if (index != 0)
                target.Append(',');
            AppendTypeIdentity(target, parameters[index].ParameterType);
        }
        target.Append(')');
        if (method is MethodInfo methodInfo)
        {
            target.Append("->");
            AppendTypeIdentity(target, methodInfo.ReturnType);
        }
    }

    private static void AppendTypeIdentity(StringBuilder target, Type? type)
    {
        if (type is null)
        {
            target.Append("<global>");
            return;
        }
        target.Append(type.Assembly.GetName().Name)
            .Append(':')
            .Append(type.FullName ?? type.Name);
    }
}
