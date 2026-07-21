using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class RecoveryStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ezy-recovery-tests", Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 7, 19, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SessionAndPerWindowRecovery_RoundTripAndClearOnNormalCompletion()
    {
        var paths = new AppDataPaths(_directory);
        var store = new RecoveryStore(paths, timeProvider: new FixedTimeProvider(_now));
        var sessionId = Guid.NewGuid();
        var firstWindow = Guid.NewGuid();
        var secondWindow = Guid.NewGuid();
        store.BeginSession(sessionId);
        store.Save(CreateRecord(sessionId, firstWindow, [1, 2], [3, 4, 5]));
        store.Save(CreateRecord(sessionId, secondWindow, [6], [7, 8]));

        Assert.Equal(sessionId, Assert.Single(store.EnumerateCrashMarkers()).SessionId);
        var summaries = store.EnumerateSummaries();
        Assert.Equal(2, summaries.Count);
        Assert.Equal(3, summaries.Single(summary => summary.WindowId == firstWindow).PayloadLength);
        var selected = store.TryLoad(sessionId, firstWindow);
        Assert.NotNull(selected);
        Assert.Equal(new byte[] { 3, 4, 5 }, selected.Payload);
        var records = store.Enumerate();
        Assert.Equal(2, records.Count);
        Assert.Equal(new byte[] { 3, 4, 5 },
            records.Single(record => record.WindowId == firstWindow).Payload);

        store.ClearWindow(sessionId, firstWindow);
        Assert.Equal(secondWindow, Assert.Single(store.Enumerate()).WindowId);

        store.CompleteSession(sessionId);
        Assert.Empty(store.Enumerate());
        Assert.Empty(store.EnumerateCrashMarkers());
    }

    [Fact]
    public void CorruptRecovery_IsQuarantinedWithoutHidingValidRecords()
    {
        var paths = new AppDataPaths(_directory);
        var store = new RecoveryStore(paths);
        var valid = CreateRecord(Guid.NewGuid(), Guid.NewGuid(), [9], [1, 2, 3]);
        store.Save(valid);
        Directory.CreateDirectory(paths.RecoveryDirectory);
        var corruptPath = Path.Combine(paths.RecoveryDirectory, "junk.recovery");
        File.WriteAllBytes(corruptPath, [0, 1, 2, 3]);

        var records = store.Enumerate();

        Assert.Equal(valid.WindowId, Assert.Single(records).WindowId);
        Assert.False(File.Exists(corruptPath));
        Assert.Single(Directory.EnumerateFiles(paths.RecoveryQuarantineDirectory, "*.corrupt"));
    }

    [Fact]
    public void IntegrityFailure_IsQuarantinedAndNeverReturned()
    {
        var paths = new AppDataPaths(_directory);
        var store = new RecoveryStore(paths);
        store.Save(CreateRecord(Guid.NewGuid(), Guid.NewGuid(), [], [1, 2, 3]));
        var path = Assert.Single(Directory.EnumerateFiles(paths.RecoveryDirectory));
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.Empty(store.Enumerate());
        Assert.False(File.Exists(path));
        Assert.Single(Directory.EnumerateFiles(paths.RecoveryQuarantineDirectory));
    }

    [Fact]
    public void TryLoad_IntegrityFailureIsQuarantinedAfterHeaderOnlyEnumeration()
    {
        var paths = new AppDataPaths(_directory);
        var store = new RecoveryStore(paths);
        var record = CreateRecord(Guid.NewGuid(), Guid.NewGuid(), [], [1, 2, 3]);
        store.Save(record);
        var path = Assert.Single(Directory.EnumerateFiles(paths.RecoveryDirectory));
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.Equal(record.WindowId, Assert.Single(store.EnumerateSummaries()).WindowId);
        Assert.Null(store.TryLoad(record.SessionId, record.WindowId));
        Assert.False(File.Exists(path));
        Assert.Single(Directory.EnumerateFiles(paths.RecoveryQuarantineDirectory));
    }

    [Fact]
    public void TruncatedRecovery_IsQuarantinedAndNeverReturned()
    {
        var paths = new AppDataPaths(_directory);
        var store = new RecoveryStore(paths);
        store.Save(CreateRecord(Guid.NewGuid(), Guid.NewGuid(), [4, 5], [1, 2, 3]));
        var path = Assert.Single(Directory.EnumerateFiles(paths.RecoveryDirectory));
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            stream.SetLength(stream.Length - 1);

        Assert.Empty(store.Enumerate());
        Assert.False(File.Exists(path));
        Assert.Single(Directory.EnumerateFiles(paths.RecoveryQuarantineDirectory));
    }

    [Fact]
    public void QuarantineCleanup_BoundsFileCountAndTotalBytes()
    {
        var paths = new AppDataPaths(_directory);
        var store = new RecoveryStore(paths, new RecoveryStoreOptions
        {
            MaximumQuarantineFiles = 2,
            MaximumQuarantineBytes = 1024 * 1024,
        }, new FixedTimeProvider(_now));
        Directory.CreateDirectory(paths.RecoveryDirectory);
        for (var index = 0; index < 5; index++)
        {
            File.WriteAllBytes(
                Path.Combine(paths.RecoveryDirectory, $"junk-{index}.recovery"),
                [0, 1, 2, 3]);
        }

        Assert.Empty(store.Enumerate());

        var quarantined = Directory.EnumerateFiles(
            paths.RecoveryQuarantineDirectory,
            "*.corrupt").ToArray();
        Assert.Equal(2, quarantined.Length);
        Assert.True(quarantined.Sum(path => new FileInfo(path).Length) <= 1024 * 1024);
    }

    [Fact]
    public void QuarantineCleanup_RemovesExpiredEntriesAfterANewCorruption()
    {
        var paths = new AppDataPaths(_directory);
        var store = new RecoveryStore(paths, new RecoveryStoreOptions
        {
            QuarantineRetention = TimeSpan.FromDays(1),
        }, new FixedTimeProvider(_now));
        Directory.CreateDirectory(paths.RecoveryQuarantineDirectory);
        var expired = Path.Combine(paths.RecoveryQuarantineDirectory, "expired.corrupt");
        File.WriteAllBytes(expired, [1]);
        File.SetLastWriteTimeUtc(expired, (_now - TimeSpan.FromDays(2)).UtcDateTime);
        Directory.CreateDirectory(paths.RecoveryDirectory);
        File.WriteAllBytes(
            Path.Combine(paths.RecoveryDirectory, "new.recovery"),
            [0, 1, 2, 3]);

        Assert.Empty(store.Enumerate());

        Assert.False(File.Exists(expired));
        Assert.Single(Directory.EnumerateFiles(
            paths.RecoveryQuarantineDirectory,
            "*.corrupt"));
    }

    [Fact]
    public void Save_PreservesBinaryV1HeaderAndSha256Contract()
    {
        var paths = new AppDataPaths(_directory);
        var store = new RecoveryStore(paths);
        var record = CreateRecord(Guid.NewGuid(), Guid.NewGuid(), [6, 7], [8, 9, 10]);

        store.Save(record);

        var bytes = File.ReadAllBytes(Assert.Single(
            Directory.EnumerateFiles(paths.RecoveryDirectory)));
        Assert.Equal(105, bytes.Length);
        Assert.Equal("EZYRCV1", Encoding.ASCII.GetString(bytes, 0, 7));
        Assert.Equal(0, bytes[7]);
        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4)));
        Assert.True(bytes.AsSpan(12, 16).SequenceEqual(record.SessionId.ToByteArray()));
        Assert.True(bytes.AsSpan(28, 16).SequenceEqual(record.WindowId.ToByteArray()));
        Assert.Equal(_now.UtcTicks,
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(44, 8)));
        Assert.Equal(_now.UtcTicks,
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(52, 8)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(60, 4)));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(64, 4)));
        Assert.Equal(new byte[] { 6, 7, 8, 9, 10 }, bytes.AsSpan(100).ToArray());
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(bytes.AsSpan(0, 68));
        hash.AppendData(bytes.AsSpan(100));
        Assert.True(CryptographicOperations.FixedTimeEquals(
            bytes.AsSpan(68, 32), hash.GetHashAndReset()));
    }

    [Fact]
    public void LargePayload_SaveAndReadAllocateNoAdditionalWholeFileBuffer()
    {
        const int payloadLength = 16 * 1024 * 1024;
        var paths = new AppDataPaths(_directory);
        var store = new RecoveryStore(paths, new RecoveryStoreOptions
        {
            MaximumPayloadBytes = payloadLength,
        });
        var warmup = CreateRecord(Guid.NewGuid(), Guid.NewGuid(), [], [1]);
        store.Save(warmup);
        Assert.Single(store.Enumerate());
        store.ClearWindow(warmup.SessionId, warmup.WindowId);

        var payload = new byte[payloadLength];
        payload[0] = 11;
        payload[^1] = 12;
        var record = CreateRecord(Guid.NewGuid(), Guid.NewGuid(), [13], payload);

        var allocatedBeforeSave = GC.GetAllocatedBytesForCurrentThread();
        store.Save(record);
        var saveAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeSave;

        var allocatedBeforeRead = GC.GetAllocatedBytesForCurrentThread();
        var restored = Assert.Single(store.Enumerate());
        var readAllocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBeforeRead;

        Assert.True(saveAllocated < payloadLength / 4,
            $"Save allocated {saveAllocated:N0} bytes for a {payloadLength:N0}-byte payload.");
        Assert.True(readAllocated < payloadLength + (payloadLength / 4),
            $"Read allocated {readAllocated:N0} bytes for a {payloadLength:N0}-byte payload.");
        Assert.Equal(payloadLength, restored.Payload.Length);
        Assert.Equal(11, restored.Payload[0]);
        Assert.Equal(12, restored.Payload[^1]);
    }

    [Fact]
    public void LargePayload_SummaryEnumerationDoesNotAllocateThePayload()
    {
        const int payloadLength = 16 * 1024 * 1024;
        var paths = new AppDataPaths(_directory);
        var store = new RecoveryStore(paths, new RecoveryStoreOptions
        {
            MaximumPayloadBytes = payloadLength,
        });
        var record = CreateRecord(
            Guid.NewGuid(), Guid.NewGuid(), [7], new byte[payloadLength]);
        store.Save(record);

        _ = store.EnumerateSummaries();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var summary = Assert.Single(store.EnumerateSummaries());
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;

        Assert.Equal(payloadLength, summary.PayloadLength);
        Assert.True(allocated < 1024 * 1024,
            $"Summary enumeration allocated {allocated:N0} bytes.");
        var selected = store.TryLoad(record.SessionId, record.WindowId);
        Assert.NotNull(selected);
        Assert.Equal(payloadLength, selected.Payload.Length);
    }

    [Fact]
    public async Task ConcurrentSameWindowSaves_LeaveOneCompleteAtomicRecord()
    {
        var paths = new AppDataPaths(_directory);
        var store = new RecoveryStore(paths);
        var sessionId = Guid.NewGuid();
        var windowId = Guid.NewGuid();

        await Task.WhenAll(Enumerable.Range(1, 40).Select(value => Task.Run(() =>
            store.Save(CreateRecord(sessionId, windowId, [(byte)value],
                Enumerable.Repeat((byte)value, 128).ToArray())))));

        var record = Assert.Single(store.Enumerate());
        Assert.Equal(128, record.Payload.Length);
        Assert.All(record.Payload, value => Assert.Equal(record.Payload[0], value));
        Assert.Empty(Directory.EnumerateFiles(paths.RecoveryDirectory, "*.tmp"));
    }

    [Fact]
    public void CorruptCrashMarker_IsQuarantinedAndValidMarkerSurvives()
    {
        var paths = new AppDataPaths(_directory);
        var store = new RecoveryStore(paths, timeProvider: new FixedTimeProvider(_now));
        var sessionId = Guid.NewGuid();
        store.BeginSession(sessionId);
        Directory.CreateDirectory(paths.CrashMarkersDirectory);
        File.WriteAllText(Path.Combine(paths.CrashMarkersDirectory, "bad.marker.json"), "bad");

        Assert.Equal(sessionId, Assert.Single(store.EnumerateCrashMarkers()).SessionId);
        Assert.Single(Directory.EnumerateFiles(paths.RecoveryQuarantineDirectory, "*.corrupt"));
    }

    [Fact]
    public void Summary_EmptyWindowIdentityIsQuarantinedBeforeItCanReachStartupUi()
    {
        var paths = new AppDataPaths(_directory);
        var store = new RecoveryStore(paths);
        var record = CreateRecord(Guid.NewGuid(), Guid.NewGuid(), [], [1]);
        store.Save(record);
        var original = Assert.Single(Directory.EnumerateFiles(paths.RecoveryDirectory));
        var bytes = File.ReadAllBytes(original);
        bytes.AsSpan(28, 16).Clear();
        var malformed = Path.Combine(
            paths.RecoveryDirectory,
            $"{record.SessionId:N}-{Guid.Empty:N}.recovery");
        File.Delete(original);
        File.WriteAllBytes(malformed, bytes);

        Assert.Empty(store.EnumerateSummaries());
        Assert.False(File.Exists(malformed));
        Assert.Single(Directory.EnumerateFiles(paths.RecoveryQuarantineDirectory, "*.corrupt"));
    }

    [Fact]
    public void TemporaryReadFailure_PreservesRecoveryAndReportsForRetry()
    {
        var paths = new AppDataPaths(_directory);
        var errors = new List<Exception>();
        var store = new RecoveryStore(paths, reportError: errors.Add);
        var record = CreateRecord(Guid.NewGuid(), Guid.NewGuid(), [], [1, 2, 3]);
        store.Save(record);
        var path = Assert.Single(Directory.EnumerateFiles(paths.RecoveryDirectory));

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var enumeration = store.EnumerateSummaryState();
            Assert.Empty(enumeration.Summaries);
            Assert.False(enumeration.IsComplete);
            Assert.True(File.Exists(path));
            Assert.False(Directory.Exists(paths.RecoveryQuarantineDirectory));
        }

        Assert.NotEmpty(errors);
        Assert.All(errors, error => Assert.True(
            error is IOException or UnauthorizedAccessException,
            error.GetType().FullName));
        Assert.NotNull(store.TryLoad(record.SessionId, record.WindowId));
    }

    [Fact]
    public void DiscardCandidates_CompleteEnumerationClearsVisibleCheckpointsAndMarker()
    {
        var paths = new AppDataPaths(_directory);
        var store = new RecoveryStore(paths);
        var sessionId = Guid.NewGuid();
        store.BeginSession(sessionId);
        store.Save(CreateRecord(sessionId, Guid.NewGuid(), [], [1]));
        store.Save(CreateRecord(sessionId, Guid.NewGuid(), [], [2]));
        var visible = store.EnumerateSummaryState();
        Assert.True(visible.IsComplete);

        var remaining = store.DiscardCandidates(visible.Summaries);

        Assert.True(remaining.IsComplete);
        Assert.Empty(remaining.Summaries);
        Assert.Empty(store.EnumerateCrashMarkers());
    }

    [Fact]
    public void DiscardCandidates_IncompleteEnumerationPreservesHiddenCheckpointAndMarker()
    {
        var paths = new AppDataPaths(_directory);
        var store = new RecoveryStore(paths);
        var sessionId = Guid.NewGuid();
        var visibleWindowId = Guid.NewGuid();
        var hiddenWindowId = Guid.NewGuid();
        store.BeginSession(sessionId);
        store.Save(CreateRecord(sessionId, visibleWindowId, [], [1]));
        store.Save(CreateRecord(sessionId, hiddenWindowId, [], [2]));
        var hiddenPath = Path.Combine(
            paths.RecoveryDirectory,
            $"{sessionId:N}-{hiddenWindowId:N}.recovery");

        using (new FileStream(
            hiddenPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            var partial = store.EnumerateSummaryState();
            Assert.False(partial.IsComplete);
            Assert.Equal(visibleWindowId, Assert.Single(partial.Summaries).WindowId);

            var remaining = store.DiscardCandidates(partial.Summaries);

            Assert.False(remaining.IsComplete);
            Assert.Empty(remaining.Summaries);
            Assert.True(File.Exists(hiddenPath));
            Assert.Null(store.TryLoad(sessionId, visibleWindowId));
            Assert.Equal(sessionId, Assert.Single(store.EnumerateCrashMarkers()).SessionId);
        }

        Assert.Equal(hiddenWindowId, store.TryLoad(sessionId, hiddenWindowId)!.WindowId);
    }

    [Fact]
    public void InvalidRecord_IsRejectedBeforeAnyFileIsWritten()
    {
        var paths = new AppDataPaths(_directory);
        var store = new RecoveryStore(paths, new RecoveryStoreOptions
        {
            MaximumMetadataBytes = 4,
            MaximumPayloadBytes = 16,
        });

        Assert.Throws<ArgumentException>(() => store.Save(CreateRecord(
            Guid.NewGuid(), Guid.NewGuid(), [1, 2, 3, 4, 5], [1])));
        Assert.False(Directory.Exists(paths.RecoveryDirectory));
    }

    private RecoveryRecord CreateRecord(
        Guid sessionId,
        Guid windowId,
        byte[] metadata,
        byte[] payload)
    {
        return new RecoveryRecord
        {
            SessionId = sessionId,
            WindowId = windowId,
            CreatedAtUtc = _now,
            UpdatedAtUtc = _now,
            Metadata = metadata,
            Payload = payload,
        };
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
