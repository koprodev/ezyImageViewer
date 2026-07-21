using System.Text.Json;
using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class AppSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "ezy-settings-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Defaults_AreSafeAndMatchTheEstablishedCaptureContract()
    {
        var settings = new AppSettingsStore(Path.Combine(_directory, "settings.json")).Load();

        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(ToolRailDock.Vertical, settings.ToolRailDock);
        Assert.True(settings.ClipboardWatchEnabled);
        Assert.True(settings.RecentFilesEnabled);
        Assert.False(settings.IncludeSubfoldersInNavigation);
        Assert.Equal(SingleInstanceBehavior.ReuseExistingWindow, settings.SingleInstanceBehavior);
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal(
            HotkeyModifiers.Control | HotkeyModifiers.Shift,
            settings.CaptureHotkey.Modifiers);
        Assert.Equal(0x45, settings.CaptureHotkey.VirtualKey);
        Assert.False(settings.CaptureAutoSaveEnabled);
    }

    [Fact]
    public void SchemaV1_MigratesAndLegacyLayoutWritesPreserveUnifiedSettings()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, """{"schemaVersion":1,"toolRailDock":1}""");
        var store = new AppSettingsStore(path);

        var migrated = store.Load();
        Assert.Equal(ToolRailDock.Horizontal, migrated.ToolRailDock);
        store.Save(migrated with
        {
            Theme = AppTheme.Dark,
            ToolDefaults = new ToolDefaults
            {
                StrokeArgb = 0xFF12_3456,
                MosaicBlockSize = 32f,
                Styles = new Dictionary<string, ToolStylePreference>
                {
                    ["Pen"] = new()
                    {
                        StrokeWidth = 9f,
                        Opacity = 0.75f,
                        FontSize = 24f,
                    },
                },
            },
        });

        var legacyStore = new ViewerLayoutPreferencesStore(path);
        legacyStore.Save(new ViewerLayoutPreferences { ToolRailDock = ToolRailDock.Vertical });

        var final = store.Load();
        Assert.Equal(ToolRailDock.Vertical, final.ToolRailDock);
        Assert.Equal(AppTheme.Dark, final.Theme);
        Assert.Equal(0xFF12_3456u, final.ToolDefaults.StrokeArgb);
        Assert.Equal(32f, final.ToolDefaults.MosaicBlockSize);
        Assert.Equal(9f, final.ToolDefaults.Styles["Pen"].StrokeWidth);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(3, document.RootElement.GetProperty("schemaVersion").GetInt32());
    }

    [Fact]
    public void SchemaV2_MigratesAndDropsRetiredAutomaticUpdatePreference()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, """
            {
              "schemaVersion": 2,
              "toolRailDock": 0,
              "clipboardWatchEnabled": false,
              "recentFilesEnabled": true,
              "updateChecksEnabled": true,
              "includeSubfoldersInNavigation": true,
              "singleInstanceBehavior": 0,
              "theme": 2,
              "captureHotkey": {
                "modifiers": 6,
                "virtualKey": 69
              },
              "toolDefaults": {
                "styles": {},
                "strokeArgb": 4278190080,
                "maskArgb": 4278190080,
                "fillEnabled": false,
                "mosaicBlockSize": 12,
                "blurSigma": 8,
                "cornerRadius": 8,
                "arrowhead": 2,
                "fontFamily": "Malgun Gothic",
                "fontBold": false,
                "fontItalic": false,
                "textAlignment": 0,
                "textBackgroundEnabled": false
              },
              "captureAutoSaveEnabled": false
            }
            """);
        var store = new AppSettingsStore(path);

        var migrated = store.Load();

        Assert.Equal(3, migrated.SchemaVersion);
        Assert.False(migrated.ClipboardWatchEnabled);
        Assert.True(migrated.RecentFilesEnabled);
        Assert.True(migrated.IncludeSubfoldersInNavigation);
        Assert.Equal(AppTheme.Dark, migrated.Theme);
        store.Save(migrated);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        Assert.False(document.RootElement.TryGetProperty("updateChecksEnabled", out _));
    }

    [Theory]
    [InlineData("{ not-json }")]
    [InlineData("""{"schemaVersion":99,"toolRailDock":1}""")]
    [InlineData("""{"schemaVersion":1,"toolRailDock":1,"extra":true}""")]
    [InlineData("""{"schemaVersion":2,"toolRailDock":0,"clipboardWatchEnabled":true,"recentFilesEnabled":true,"updateChecksEnabled":true,"includeSubfoldersInNavigation":false,"singleInstanceBehavior":0,"theme":0,"captureHotkey":{"modifiers":6,"virtualKey":69},"captureAutoSaveEnabled":true}""")]
    public void CorruptUnsupportedOrUnsafeSettings_FallBackToPrivacySafeDefaults(string json)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, json);

        var settings = new AppSettingsStore(path).Load();

        Assert.False(settings.ClipboardWatchEnabled);
        Assert.False(settings.RecentFilesEnabled);
    }

    [Fact]
    public void LockedOrOversizedExistingSettings_FallBackToPrivacySafeDefaults()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        File.WriteAllText(path, "{}", System.Text.Encoding.UTF8);
        using (var locked = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var lockedSettings = new AppSettingsStore(path).Load();
            Assert.False(lockedSettings.ClipboardWatchEnabled);
            Assert.False(lockedSettings.RecentFilesEnabled);
        }

        File.WriteAllBytes(path, new byte[64 * 1024 + 1]);
        var oversizedSettings = new AppSettingsStore(path).Load();
        Assert.False(oversizedSettings.ClipboardWatchEnabled);
        Assert.False(oversizedSettings.RecentFilesEnabled);
    }

    [Fact]
    public void InvalidSave_DoesNotReplacePreviousAtomicFile()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new AppSettingsStore(path);
        store.Save(new AppSettings { Theme = AppTheme.Dark });
        var before = File.ReadAllBytes(path);

        Assert.Throws<ArgumentException>(() => store.Save(new AppSettings
        {
            CaptureAutoSaveEnabled = true,
        }));

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tmp"));
    }

    [Theory]
    [InlineData(0x30)]
    [InlineData(0x39)]
    [InlineData(0x41)]
    [InlineData(0x5A)]
    [InlineData(0x70)]
    [InlineData(0x87)]
    public void SupportedCaptureHotkeyBoundaries_RoundTrip(int virtualKey)
    {
        var store = new AppSettingsStore(Path.Combine(_directory, "settings.json"));
        store.Save(new AppSettings
        {
            CaptureHotkey = new CaptureHotkey
            {
                Modifiers = HotkeyModifiers.Control,
                VirtualKey = virtualKey,
            },
        });

        var loaded = store.Load();

        Assert.Equal(HotkeyModifiers.Control, loaded.CaptureHotkey.Modifiers);
        Assert.Equal(virtualKey, loaded.CaptureHotkey.VirtualKey);
    }

    [Theory]
    [InlineData(0x01)]
    [InlineData(0x20)]
    [InlineData(0x3A)]
    [InlineData(0x5B)]
    [InlineData(0x6F)]
    [InlineData(0x88)]
    [InlineData(0xFF)]
    public void UnsupportedPersistedHotkey_FallsBackToPrivacySafeDefaults(int virtualKey)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        var json = JsonSerializer.Serialize(new AppSettings
        {
            ClipboardWatchEnabled = true,
            RecentFilesEnabled = true,
            CaptureHotkey = new CaptureHotkey
            {
                Modifiers = HotkeyModifiers.Control,
                VirtualKey = virtualKey,
            },
        }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
        File.WriteAllText(path, json);

        var loaded = new AppSettingsStore(path).Load();

        Assert.False(loaded.ClipboardWatchEnabled);
        Assert.False(loaded.RecentFilesEnabled);
        Assert.Equal(0x45, loaded.CaptureHotkey.VirtualKey);
    }

    [Theory]
    [InlineData("Unknown", 3, 1, 24)]
    [InlineData("Pen", 0, 1, 24)]
    [InlineData("Pen", 3, 0, 24)]
    [InlineData("Pen", 3, 1, 5)]
    public void InvalidToolDefaults_AreRejected(
        string name,
        float strokeWidth,
        float opacity,
        float fontSize)
    {
        var store = new AppSettingsStore(Path.Combine(_directory, "settings.json"));
        var settings = new AppSettings
        {
            ToolDefaults = new ToolDefaults
            {
                Styles = new Dictionary<string, ToolStylePreference>
                {
                    [name] = new()
                    {
                        StrokeWidth = strokeWidth,
                        Opacity = opacity,
                        FontSize = fontSize,
                    },
                },
            },
        };

        Assert.Throws<ArgumentException>(() => store.Save(settings));
    }

    [Fact]
    public async Task ConcurrentUpdates_DoNotLoseIndependentChanges()
    {
        var store = new AppSettingsStore(Path.Combine(_directory, "settings.json"));
        await Task.WhenAll(
            Task.Run(() => store.Update(value => value with { Theme = AppTheme.Dark })),
            Task.Run(() => store.Update(value => value with { ClipboardWatchEnabled = false })),
            Task.Run(() => store.Update(value => value with { RecentFilesEnabled = false })),
            Task.Run(() => store.Update(value => value with
            {
                IncludeSubfoldersInNavigation = true,
            })));

        var loaded = store.Load();
        Assert.Equal(AppTheme.Dark, loaded.Theme);
        Assert.False(loaded.ClipboardWatchEnabled);
        Assert.False(loaded.RecentFilesEnabled);
        Assert.True(loaded.IncludeSubfoldersInNavigation);
    }

    [Fact]
    public void SettingsDialogMerge_PreservesConcurrentUneditedChanges()
    {
        var baseline = new AppSettings();
        var edited = baseline with { Theme = AppTheme.Dark };
        var current = baseline with
        {
            RecentFilesEnabled = false,
            CaptureHotkey = new CaptureHotkey
            {
                Modifiers = HotkeyModifiers.Alt,
                VirtualKey = 'Q',
            },
        };

        var merged = AppSettingsMerger.MergeSettingsDialogChanges(
            baseline,
            edited,
            current);

        Assert.Equal(AppTheme.Dark, merged.Theme);
        Assert.False(merged.RecentFilesEnabled);
        Assert.Equal(current.CaptureHotkey, merged.CaptureHotkey);
    }

    [Fact]
    public void ToolDefaultsMerge_ChangesOnlyTheFieldsEditedByThatWindow()
    {
        static ToolStylePreference Style(float width) => new()
        {
            StrokeWidth = width,
            Opacity = 1,
            FontSize = 24,
        };
        var baseline = new ToolDefaults
        {
            Styles = new Dictionary<string, ToolStylePreference>
            {
                ["Pen"] = Style(3),
            },
        };
        var edited = baseline with { MosaicBlockSize = 24 };
        var current = baseline with
        {
            StrokeArgb = 0xFF12_3456,
            Styles = new Dictionary<string, ToolStylePreference>
            {
                ["Pen"] = Style(5),
            },
        };

        var merged = AppSettingsMerger.MergeToolDefaultChanges(baseline, edited, current);

        Assert.Equal(24, merged.MosaicBlockSize);
        Assert.Equal(0xFF12_3456u, merged.StrokeArgb);
        Assert.Equal(5, merged.Styles["Pen"].StrokeWidth);
    }

    [Fact]
    public void ToolDefaultsMerge_ExplicitStyleEditWinsOverAConcurrentValue()
    {
        static ToolStylePreference Style(float width) => new()
        {
            StrokeWidth = width,
            Opacity = 1,
            FontSize = 24,
        };
        var baseline = new ToolDefaults
        {
            Styles = new Dictionary<string, ToolStylePreference> { ["Pen"] = Style(3) },
        };
        var edited = baseline with
        {
            Styles = new Dictionary<string, ToolStylePreference> { ["Pen"] = Style(4) },
        };
        var current = baseline with
        {
            Styles = new Dictionary<string, ToolStylePreference> { ["Pen"] = Style(5) },
        };

        var merged = AppSettingsMerger.MergeToolDefaultChanges(baseline, edited, current);

        Assert.Equal(4, merged.Styles["Pen"].StrokeWidth);
    }

    [Fact]
    public void AppDataPaths_KeepEveryStoreUnderTheInjectedRoot()
    {
        var paths = new AppDataPaths(_directory);

        Assert.Equal(Path.GetFullPath(_directory), paths.RootDirectory);
        Assert.All(new[]
        {
            paths.SettingsFile,
            paths.RecentFilesFile,
            paths.LogsDirectory,
            paths.RecoveryDirectory,
            paths.RecoveryQuarantineDirectory,
            paths.CrashMarkersDirectory,
            paths.StartupHealthFile,
        }, path =>
        {
            var relative = Path.GetRelativePath(paths.RootDirectory, path);
            Assert.False(relative.StartsWith("..", StringComparison.Ordinal));
            Assert.False(Path.IsPathRooted(relative));
        });
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }
}
