using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class RecentFileStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ezy-recent-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Add_IsBoundedNewestFirstAndPrunesMissingFiles()
    {
        Directory.CreateDirectory(_directory);
        var paths = Enumerable.Range(0, 4)
            .Select(index => Path.Combine(_directory, $"image-{index}.png"))
            .ToArray();
        foreach (var path in paths)
            File.WriteAllBytes(path, [1]);
        var time = new MutableTimeProvider(new DateTimeOffset(
            2026, 7, 19, 0, 0, 0, TimeSpan.Zero));
        var store = new RecentFileStore(
            Path.Combine(_directory, "recent.json"),
            capacity: 3,
            timeProvider: time);

        foreach (var path in paths)
        {
            store.Add(path, enabled: true);
            time.Advance(TimeSpan.FromMinutes(1));
        }

        var entries = store.Load(enabled: true);
        Assert.Equal(paths[3], entries[0].Path);
        Assert.Equal(paths[2], entries[1].Path);
        Assert.Equal(paths[1], entries[2].Path);

        File.Delete(paths[2]);
        entries = store.Load(enabled: true);
        Assert.Equal(new[] { paths[3], paths[1] }, entries.Select(entry => entry.Path));
        Assert.DoesNotContain(paths[2], File.ReadAllText(Path.Combine(_directory, "recent.json")));
    }

    [Fact]
    public void DisabledStore_ClearsExistingHistoryAndDoesNotRecord()
    {
        Directory.CreateDirectory(_directory);
        var recentPath = Path.Combine(_directory, "recent.json");
        var documentPath = Path.Combine(_directory, "private.png");
        File.WriteAllBytes(documentPath, [1]);
        var store = new RecentFileStore(recentPath);
        store.Add(documentPath, enabled: true);
        Assert.True(File.Exists(recentPath));

        store.Add(documentPath, enabled: false);

        Assert.False(File.Exists(recentPath));
        Assert.Empty(store.Load(enabled: false));
    }

    [Fact]
    public void CorruptStore_FailsClosedWithoutLeakingIntoAnotherFile()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "recent.json");
        File.WriteAllText(path, "{ not-json }");

        var store = new RecentFileStore(path);

        Assert.Empty(store.Load(enabled: true));
        Assert.Equal(new[] { path }, Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task ConcurrentAdds_RemainUniqueBoundedAndParseable()
    {
        Directory.CreateDirectory(_directory);
        var paths = Enumerable.Range(0, 40)
            .Select(index => Path.Combine(_directory, $"file-{index}.png"))
            .ToArray();
        foreach (var path in paths)
            File.WriteAllBytes(path, [1]);
        var store = new RecentFileStore(Path.Combine(_directory, "recent.json"), capacity: 20);

        await Task.WhenAll(paths.Select(path => Task.Run(() => store.Add(path, enabled: true))));

        var entries = store.Load(enabled: true);
        Assert.Equal(20, entries.Count);
        Assert.Equal(20, entries.Select(entry => entry.Path).Distinct(
            StringComparer.OrdinalIgnoreCase).Count());
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }
}
