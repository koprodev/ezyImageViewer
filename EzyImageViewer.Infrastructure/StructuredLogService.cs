using System.Threading.Channels;

namespace EzyImageViewer.Infrastructure;

public static class StructuredLogEventNames
{
    public const string AppStarted = nameof(AppStarted);
    public const string AppStopped = nameof(AppStopped);
    public const string DocumentOpened = nameof(DocumentOpened);
    public const string DocumentOpenFailed = nameof(DocumentOpenFailed);
    public const string RecoverySaved = nameof(RecoverySaved);
    public const string RecoveryOperationFailed = nameof(RecoveryOperationFailed);
    public const string RecoveryRestored = nameof(RecoveryRestored);
    public const string RecoveryCleanupFailed = nameof(RecoveryCleanupFailed);
    public const string RecentFileOperationFailed = nameof(RecentFileOperationFailed);
    public const string AppDataProtectionFailed = nameof(AppDataProtectionFailed);
    public const string StartupFailureRecorded = nameof(StartupFailureRecorded);
    public const string SafeModeEnabled = nameof(SafeModeEnabled);
    public const string SettingsSaved = nameof(SettingsSaved);
    public const string ReleasePageLaunchFailed = nameof(ReleasePageLaunchFailed);

    internal static bool IsAllowed(string? name)
    {
        return name is AppStarted
            or AppStopped
            or DocumentOpened
            or DocumentOpenFailed
            or RecoverySaved
            or RecoveryOperationFailed
            or RecoveryRestored
            or RecoveryCleanupFailed
            or RecentFileOperationFailed
            or AppDataProtectionFailed
            or StartupFailureRecorded
            or SafeModeEnabled
            or SettingsSaved
            or ReleasePageLaunchFailed;
    }
}

public sealed class StructuredLogService : IAsyncDisposable
{
    private const int DefaultCapacity = 256;
    private readonly Channel<PendingLogEntry> _channel;
    private readonly Func<LocalLogLevel, StructuredLogEvent, Exception?, bool> _writer;
    private readonly Action<Exception>? _failureReporter;
    private readonly Task _worker;
    private int _completionStarted;
    private long _droppedCount;

    public StructuredLogService(
        StructuredLocalLogger logger,
        int capacity = DefaultCapacity,
        Action<Exception>? failureReporter = null)
        : this(CreateWriter(logger), capacity, failureReporter)
    {
    }

    public StructuredLogService(
        Func<LocalLogLevel, StructuredLogEvent, Exception?, bool> writer,
        int capacity = DefaultCapacity,
        Action<Exception>? failureReporter = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _writer = writer;
        _failureReporter = failureReporter;
        _channel = Channel.CreateBounded<PendingLogEntry>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            // TryWrite stays nonblocking while exposing a full queue for drop-newest accounting.
            FullMode = BoundedChannelFullMode.Wait,
        });
        _worker = RunWorkerAsync();
    }

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public bool TryEnqueue(
        LocalLogLevel level,
        StructuredLogEvent logEvent,
        Exception? exception = null)
    {
        if (!Enum.IsDefined(level))
            throw new ArgumentOutOfRangeException(nameof(level));
        ArgumentNullException.ThrowIfNull(logEvent);
        if (!StructuredLogEventNames.IsAllowed(logEvent.Name))
        {
            throw new ArgumentException(
                "The structured log event name is not allowlisted.",
                nameof(logEvent));
        }

        if (Volatile.Read(ref _completionStarted) != 0)
            return false;

        if (_channel.Writer.TryWrite(new PendingLogEntry(level, logEvent, exception)))
            return true;

        if (Volatile.Read(ref _completionStarted) == 0)
            Interlocked.Increment(ref _droppedCount);
        return false;
    }

    public ValueTask DrainAsync()
    {
        return new ValueTask(CompleteAndDrainAsync());
    }

    public ValueTask DisposeAsync()
    {
        return DrainAsync();
    }

    private static Func<LocalLogLevel, StructuredLogEvent, Exception?, bool> CreateWriter(
        StructuredLocalLogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return logger.TryWrite;
    }

    private Task CompleteAndDrainAsync()
    {
        if (Interlocked.Exchange(ref _completionStarted, 1) == 0)
            _channel.Writer.TryComplete();
        return _worker;
    }

    private async Task RunWorkerAsync()
    {
        try
        {
            while (await _channel.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var entry))
                {
                    try
                    {
                        if (!_writer(entry.Level, entry.LogEvent, entry.Exception))
                        {
                            ReportFailure(new IOException(
                                "The structured log writer reported a failure."));
                        }
                    }
                    catch (Exception exception)
                    {
                        ReportFailure(exception);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            ReportFailure(exception);
        }
    }

    private void ReportFailure(Exception exception)
    {
        try
        {
            _failureReporter?.Invoke(exception);
        }
        catch (Exception)
        {
        }
    }

    private readonly record struct PendingLogEntry(
        LocalLogLevel Level,
        StructuredLogEvent LogEvent,
        Exception? Exception);
}
