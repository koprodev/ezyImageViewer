using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class ViewerLayoutPreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ezy-layout-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingAndCorruptSettings_FallBackToVertical()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new ViewerLayoutPreferencesStore(path);
        Assert.Equal(ToolRailDock.Vertical, store.Load().ToolRailDock);

        Directory.CreateDirectory(_directory);
        File.WriteAllText(path, "{ not-json }");

        Assert.Equal(ToolRailDock.Vertical, store.Load().ToolRailDock);
    }

    [Fact]
    public void HorizontalOrientation_RoundTripsAndLeavesNoTemporaryFile()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new ViewerLayoutPreferencesStore(path);

        store.Save(new ViewerLayoutPreferences { ToolRailDock = ToolRailDock.Horizontal });

        Assert.Equal(ToolRailDock.Horizontal, store.Load().ToolRailDock);
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
