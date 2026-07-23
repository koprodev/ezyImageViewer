using System.Text.Json;
using System.Text.Json.Serialization;
using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Input;

namespace EzyImageViewer.Infrastructure;

public enum AppTheme
{
    System,
    Light,
    Dark,
}

public enum SingleInstanceBehavior
{
    ReuseExistingWindow,
    OpenNewWindow,
}

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

public sealed record CaptureHotkey
{
    public HotkeyModifiers Modifiers { get; init; } =
        HotkeyModifiers.Control | HotkeyModifiers.Shift;
    public int VirtualKey { get; init; } = 0x45;
}

public sealed record ToolStylePreference
{
    public float StrokeWidth { get; init; } = 3f;
    public float Opacity { get; init; } = 1f;
    public float FontSize { get; init; } = 24f;
}

public sealed record ToolDefaults
{
    public Dictionary<string, ToolStylePreference> Styles { get; init; } = [];
    public uint StrokeArgb { get; init; } = 0xFFE8_3B2E;
    public uint MaskArgb { get; init; } = 0xFF00_0000;
    public bool FillEnabled { get; init; }
    public float MosaicBlockSize { get; init; } = 12f;
    public float BlurSigma { get; init; } = 8f;
    public float CornerRadius { get; init; } = 8f;
    public ArrowheadKind Arrowhead { get; init; } = ArrowheadKind.Triangle;
    public string FontFamily { get; init; } = "Malgun Gothic";
    public bool FontBold { get; init; }
    public bool FontItalic { get; init; }
    public AnnotationTextAlignment TextAlignment { get; init; }
    public bool TextBackgroundEnabled { get; init; }
}

public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 5;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public ToolRailDock ToolRailDock { get; init; } = ToolRailDock.Vertical;
    public bool ClipboardWatchEnabled { get; init; } = true;
    public bool RecentFilesEnabled { get; init; } = true;
    public bool IncludeSubfoldersInNavigation { get; init; }
    public SingleInstanceBehavior SingleInstanceBehavior { get; init; } =
        SingleInstanceBehavior.ReuseExistingWindow;
    public AppTheme Theme { get; init; } = AppTheme.System;
    public CaptureHotkey CaptureHotkey { get; init; } = new();
    public ToolDefaults ToolDefaults { get; init; } = new();
    public bool CaptureAutoSaveEnabled { get; init; }
    /// <summary>UR-010: each rail group collapses into its dropdown/split button independently.</summary>
    public bool ToolbarOpenGroupEnabled { get; init; } = true;
    public bool ToolbarSelectGroupEnabled { get; init; } = true;
    public bool ToolbarTransformGroupEnabled { get; init; } = true;
    public bool ToolbarCropGroupEnabled { get; init; } = true;
    public bool ToolbarZoomGroupEnabled { get; init; } = true;
    public bool ToolbarProtectGroupEnabled { get; init; } = true;
}

/// <summary>Merges stale window snapshots without reverting unrelated concurrent changes.</summary>
public static class AppSettingsMerger
{
    public static AppSettings MergeSettingsDialogChanges(
        AppSettings baseline,
        AppSettings edited,
        AppSettings current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(current);
        return current with
        {
            Theme = Changed(baseline.Theme, edited.Theme) ? edited.Theme : current.Theme,
            SingleInstanceBehavior = Changed(
                baseline.SingleInstanceBehavior,
                edited.SingleInstanceBehavior)
                ? edited.SingleInstanceBehavior
                : current.SingleInstanceBehavior,
            ClipboardWatchEnabled = Changed(
                baseline.ClipboardWatchEnabled,
                edited.ClipboardWatchEnabled)
                ? edited.ClipboardWatchEnabled
                : current.ClipboardWatchEnabled,
            RecentFilesEnabled = Changed(
                baseline.RecentFilesEnabled,
                edited.RecentFilesEnabled)
                ? edited.RecentFilesEnabled
                : current.RecentFilesEnabled,
            IncludeSubfoldersInNavigation = Changed(
                baseline.IncludeSubfoldersInNavigation,
                edited.IncludeSubfoldersInNavigation)
                ? edited.IncludeSubfoldersInNavigation
                : current.IncludeSubfoldersInNavigation,
            CaptureHotkey = Changed(baseline.CaptureHotkey, edited.CaptureHotkey)
                ? edited.CaptureHotkey
                : current.CaptureHotkey,
            ToolbarOpenGroupEnabled = Changed(
                baseline.ToolbarOpenGroupEnabled,
                edited.ToolbarOpenGroupEnabled)
                ? edited.ToolbarOpenGroupEnabled
                : current.ToolbarOpenGroupEnabled,
            ToolbarSelectGroupEnabled = Changed(
                baseline.ToolbarSelectGroupEnabled,
                edited.ToolbarSelectGroupEnabled)
                ? edited.ToolbarSelectGroupEnabled
                : current.ToolbarSelectGroupEnabled,
            ToolbarTransformGroupEnabled = Changed(
                baseline.ToolbarTransformGroupEnabled,
                edited.ToolbarTransformGroupEnabled)
                ? edited.ToolbarTransformGroupEnabled
                : current.ToolbarTransformGroupEnabled,
            ToolbarCropGroupEnabled = Changed(
                baseline.ToolbarCropGroupEnabled,
                edited.ToolbarCropGroupEnabled)
                ? edited.ToolbarCropGroupEnabled
                : current.ToolbarCropGroupEnabled,
            ToolbarZoomGroupEnabled = Changed(
                baseline.ToolbarZoomGroupEnabled,
                edited.ToolbarZoomGroupEnabled)
                ? edited.ToolbarZoomGroupEnabled
                : current.ToolbarZoomGroupEnabled,
            ToolbarProtectGroupEnabled = Changed(
                baseline.ToolbarProtectGroupEnabled,
                edited.ToolbarProtectGroupEnabled)
                ? edited.ToolbarProtectGroupEnabled
                : current.ToolbarProtectGroupEnabled,
        };
    }

    public static ToolDefaults MergeToolDefaultChanges(
        ToolDefaults baseline,
        ToolDefaults edited,
        ToolDefaults current)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(edited);
        ArgumentNullException.ThrowIfNull(current);
        var styles = new Dictionary<string, ToolStylePreference>(
            current.Styles,
            StringComparer.Ordinal);
        foreach (var key in baseline.Styles.Keys.Union(edited.Styles.Keys, StringComparer.Ordinal))
        {
            var hadBaseline = baseline.Styles.TryGetValue(key, out var baselineStyle);
            var hasEdited = edited.Styles.TryGetValue(key, out var editedStyle);
            if (hadBaseline == hasEdited && Equals(baselineStyle, editedStyle))
                continue;
            if (hasEdited)
                styles[key] = editedStyle!;
            else
                styles.Remove(key);
        }

        return current with
        {
            Styles = styles,
            StrokeArgb = Changed(baseline.StrokeArgb, edited.StrokeArgb)
                ? edited.StrokeArgb : current.StrokeArgb,
            MaskArgb = Changed(baseline.MaskArgb, edited.MaskArgb)
                ? edited.MaskArgb : current.MaskArgb,
            FillEnabled = Changed(baseline.FillEnabled, edited.FillEnabled)
                ? edited.FillEnabled : current.FillEnabled,
            MosaicBlockSize = Changed(baseline.MosaicBlockSize, edited.MosaicBlockSize)
                ? edited.MosaicBlockSize : current.MosaicBlockSize,
            BlurSigma = Changed(baseline.BlurSigma, edited.BlurSigma)
                ? edited.BlurSigma : current.BlurSigma,
            CornerRadius = Changed(baseline.CornerRadius, edited.CornerRadius)
                ? edited.CornerRadius : current.CornerRadius,
            Arrowhead = Changed(baseline.Arrowhead, edited.Arrowhead)
                ? edited.Arrowhead : current.Arrowhead,
            FontFamily = Changed(baseline.FontFamily, edited.FontFamily)
                ? edited.FontFamily : current.FontFamily,
            FontBold = Changed(baseline.FontBold, edited.FontBold)
                ? edited.FontBold : current.FontBold,
            FontItalic = Changed(baseline.FontItalic, edited.FontItalic)
                ? edited.FontItalic : current.FontItalic,
            TextAlignment = Changed(baseline.TextAlignment, edited.TextAlignment)
                ? edited.TextAlignment : current.TextAlignment,
            TextBackgroundEnabled = Changed(
                baseline.TextBackgroundEnabled,
                edited.TextBackgroundEnabled)
                ? edited.TextBackgroundEnabled
                : current.TextBackgroundEnabled,
        };
    }

    private static bool Changed<T>(T baseline, T edited) =>
        !EqualityComparer<T>.Default.Equals(baseline, edited);
}

internal sealed record LegacyViewerLayoutPreferences
{
    public int SchemaVersion { get; init; }
    public ToolRailDock ToolRailDock { get; init; }
}

internal sealed record LegacyAppSettingsV2
{
    public int SchemaVersion { get; init; }
    public ToolRailDock ToolRailDock { get; init; } = ToolRailDock.Vertical;
    public bool ClipboardWatchEnabled { get; init; } = true;
    public bool RecentFilesEnabled { get; init; } = true;
    public bool UpdateChecksEnabled { get; init; }
    public bool IncludeSubfoldersInNavigation { get; init; }
    public SingleInstanceBehavior SingleInstanceBehavior { get; init; } =
        SingleInstanceBehavior.ReuseExistingWindow;
    public AppTheme Theme { get; init; } = AppTheme.System;
    public CaptureHotkey CaptureHotkey { get; init; } = new();
    public ToolDefaults ToolDefaults { get; init; } = new();
    public bool CaptureAutoSaveEnabled { get; init; }

    public AppSettings ToCurrent() => new()
    {
        ToolRailDock = ToolRailDock,
        ClipboardWatchEnabled = ClipboardWatchEnabled,
        RecentFilesEnabled = RecentFilesEnabled,
        IncludeSubfoldersInNavigation = IncludeSubfoldersInNavigation,
        SingleInstanceBehavior = SingleInstanceBehavior,
        Theme = Theme,
        CaptureHotkey = CaptureHotkey,
        ToolDefaults = ToolDefaults,
        CaptureAutoSaveEnabled = CaptureAutoSaveEnabled,
    };
}

internal sealed record LegacyAppSettingsV3
{
    public int SchemaVersion { get; init; }
    public ToolRailDock ToolRailDock { get; init; } = ToolRailDock.Vertical;
    public bool ClipboardWatchEnabled { get; init; } = true;
    public bool RecentFilesEnabled { get; init; } = true;
    public bool IncludeSubfoldersInNavigation { get; init; }
    public SingleInstanceBehavior SingleInstanceBehavior { get; init; } =
        SingleInstanceBehavior.ReuseExistingWindow;
    public AppTheme Theme { get; init; } = AppTheme.System;
    public CaptureHotkey CaptureHotkey { get; init; } = new();
    public ToolDefaults ToolDefaults { get; init; } = new();
    public bool CaptureAutoSaveEnabled { get; init; }

    public AppSettings ToCurrent() => new()
    {
        ToolRailDock = ToolRailDock,
        ClipboardWatchEnabled = ClipboardWatchEnabled,
        RecentFilesEnabled = RecentFilesEnabled,
        IncludeSubfoldersInNavigation = IncludeSubfoldersInNavigation,
        SingleInstanceBehavior = SingleInstanceBehavior,
        Theme = Theme,
        CaptureHotkey = CaptureHotkey,
        ToolDefaults = ToolDefaults,
        CaptureAutoSaveEnabled = CaptureAutoSaveEnabled,
    };
}

internal sealed record LegacyAppSettingsV4
{
    public int SchemaVersion { get; init; }
    public ToolRailDock ToolRailDock { get; init; } = ToolRailDock.Vertical;
    public bool ClipboardWatchEnabled { get; init; } = true;
    public bool RecentFilesEnabled { get; init; } = true;
    public bool IncludeSubfoldersInNavigation { get; init; }
    public SingleInstanceBehavior SingleInstanceBehavior { get; init; } =
        SingleInstanceBehavior.ReuseExistingWindow;
    public AppTheme Theme { get; init; } = AppTheme.System;
    public CaptureHotkey CaptureHotkey { get; init; } = new();
    public ToolDefaults ToolDefaults { get; init; } = new();
    public bool CaptureAutoSaveEnabled { get; init; }
    public bool ToolbarDropdownsEnabled { get; init; } = true;

    // The single v4 dropdown preference seeds every per-group toggle it used to control.
    public AppSettings ToCurrent() => new()
    {
        ToolRailDock = ToolRailDock,
        ClipboardWatchEnabled = ClipboardWatchEnabled,
        RecentFilesEnabled = RecentFilesEnabled,
        IncludeSubfoldersInNavigation = IncludeSubfoldersInNavigation,
        SingleInstanceBehavior = SingleInstanceBehavior,
        Theme = Theme,
        CaptureHotkey = CaptureHotkey,
        ToolDefaults = ToolDefaults,
        CaptureAutoSaveEnabled = CaptureAutoSaveEnabled,
        ToolbarOpenGroupEnabled = ToolbarDropdownsEnabled,
        ToolbarSelectGroupEnabled = ToolbarDropdownsEnabled,
        ToolbarTransformGroupEnabled = ToolbarDropdownsEnabled,
        ToolbarCropGroupEnabled = ToolbarDropdownsEnabled,
        ToolbarZoomGroupEnabled = ToolbarDropdownsEnabled,
        ToolbarProtectGroupEnabled = ToolbarDropdownsEnabled,
    };
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(LegacyViewerLayoutPreferences))]
[JsonSerializable(typeof(LegacyAppSettingsV2))]
[JsonSerializable(typeof(LegacyAppSettingsV3))]
[JsonSerializable(typeof(LegacyAppSettingsV4))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext;

public sealed class AppSettingsStore
{
    private const int MaximumSettingsBytes = 64 * 1024;
    private readonly string _path;
    private readonly object _sync;
    private static readonly HashSet<string> AllowedToolStyleNames = new(
        [
            "Pen", "Highlighter", "Line", "Arrow", "Rectangle", "RoundedRectangle",
            "Ellipse", "Text", "Number", "SpeechBubble", "Mosaic", "Blur", "Mask", "Eyedropper",
        ],
        StringComparer.Ordinal);

    public AppSettingsStore(IAppDataPaths paths)
        : this((paths ?? throw new ArgumentNullException(nameof(paths))).SettingsFile)
    {
    }

    public AppSettingsStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
        _sync = FileStoreSynchronization.ForPath(_path);
    }

    public static AppSettingsStore CreateDefault() => new(AppDataPaths.CreateDefault());

    public AppSettings Load()
    {
        lock (_sync)
            return LoadCore();
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Validate(settings);
        lock (_sync)
            SaveCore(settings);
    }

    public AppSettings Update(Func<AppSettings, AppSettings> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_sync)
        {
            var updated = update(LoadCore())
                ?? throw new InvalidOperationException("The settings update returned null.");
            Validate(updated);
            SaveCore(updated);
            return updated;
        }
    }

    private AppSettings LoadCore()
    {
        try
        {
            byte[] json;
            try
            {
                using var stream = new FileStream(
                    _path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.SequentialScan);
                if (stream.Length is <= 0 or > MaximumSettingsBytes)
                    return PrivacySafeFallback();
                json = new byte[stream.Length];
                stream.ReadExactly(json);
            }
            catch (Exception ex) when (ex is FileNotFoundException
                or DirectoryNotFoundException)
            {
                return new AppSettings();
            }

            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16,
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("schemaVersion", out var versionElement)
                || !versionElement.TryGetInt32(out var version))
                return PrivacySafeFallback();

            if (version == ViewerLayoutPreferences.CurrentSchemaVersion)
            {
                var legacy = JsonSerializer.Deserialize(
                    json, AppSettingsJsonContext.Default.LegacyViewerLayoutPreferences);
                return legacy is { SchemaVersion: ViewerLayoutPreferences.CurrentSchemaVersion }
                    && Enum.IsDefined(legacy.ToolRailDock)
                    ? new AppSettings { ToolRailDock = legacy.ToolRailDock }
                    : PrivacySafeFallback();
            }

            if (version == 2)
            {
                var legacy = JsonSerializer.Deserialize(
                    json, AppSettingsJsonContext.Default.LegacyAppSettingsV2);
                var migrated = legacy is { SchemaVersion: 2 }
                    ? legacy.ToCurrent()
                    : null;
                return migrated is not null && IsValid(migrated)
                    ? migrated
                    : PrivacySafeFallback();
            }

            if (version == 3)
            {
                var legacy = JsonSerializer.Deserialize(
                    json, AppSettingsJsonContext.Default.LegacyAppSettingsV3);
                var migrated = legacy is { SchemaVersion: 3 }
                    ? legacy.ToCurrent()
                    : null;
                return migrated is not null && IsValid(migrated)
                    ? migrated
                    : PrivacySafeFallback();
            }

            if (version == 4)
            {
                var legacy = JsonSerializer.Deserialize(
                    json, AppSettingsJsonContext.Default.LegacyAppSettingsV4);
                var migrated = legacy is { SchemaVersion: 4 }
                    ? legacy.ToCurrent()
                    : null;
                return migrated is not null && IsValid(migrated)
                    ? migrated
                    : PrivacySafeFallback();
            }

            if (version != AppSettings.CurrentSchemaVersion)
                return PrivacySafeFallback();

            var settings = JsonSerializer.Deserialize(
                json, AppSettingsJsonContext.Default.AppSettings);
            return settings is not null && IsValid(settings)
                ? settings
                : PrivacySafeFallback();
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or JsonException)
        {
            return PrivacySafeFallback();
        }
    }

    private static AppSettings PrivacySafeFallback() => new()
    {
        ClipboardWatchEnabled = false,
        RecentFilesEnabled = false,
    };

    private void SaveCore(AppSettings settings)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            settings, AppSettingsJsonContext.Default.AppSettings);
        AtomicFileWriter.Write(_path, bytes, AtomicFileProtection.CurrentUserAndSystem);
    }

    private static bool IsValid(AppSettings settings)
    {
        return settings.SchemaVersion == AppSettings.CurrentSchemaVersion
            && Enum.IsDefined(settings.ToolRailDock)
            && Enum.IsDefined(settings.SingleInstanceBehavior)
            && Enum.IsDefined(settings.Theme)
            && settings.CaptureHotkey is not null
            && CaptureHotkeyPolicy.IsSupportedChord(
                (uint)settings.CaptureHotkey.Modifiers,
                settings.CaptureHotkey.VirtualKey)
            && IsValid(settings.ToolDefaults)
            && !settings.CaptureAutoSaveEnabled;
    }

    private static bool IsValid(ToolDefaults? defaults)
    {
        if (defaults is null
            || defaults.Styles is null
            || defaults.Styles.Count > AllowedToolStyleNames.Count
            || (defaults.StrokeArgb >> 24) != 0xFF
            || (defaults.MaskArgb >> 24) != 0xFF
            || !float.IsFinite(defaults.MosaicBlockSize)
            || defaults.MosaicBlockSize is < 2f or > 1024f
            || !float.IsFinite(defaults.BlurSigma)
            || defaults.BlurSigma is < 1f or > 80f
            || !float.IsFinite(defaults.CornerRadius)
            || defaults.CornerRadius is < 0f or > 1_000_000f
            || !Enum.IsDefined(defaults.Arrowhead)
            || string.IsNullOrWhiteSpace(defaults.FontFamily)
            || defaults.FontFamily.Length > 128
            || !Enum.IsDefined(defaults.TextAlignment))
        {
            return false;
        }

        foreach (var pair in defaults.Styles)
        {
            var style = pair.Value;
            if (!AllowedToolStyleNames.Contains(pair.Key)
                || style is null
                || !float.IsFinite(style.StrokeWidth)
                || style.StrokeWidth is < 1f or > 1000f
                || !float.IsFinite(style.Opacity)
                || style.Opacity is < 0.01f or > 1f
                || !float.IsFinite(style.FontSize)
                || style.FontSize is < 6f or > 10_000f)
            {
                return false;
            }
        }
        return true;
    }

    public static void Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsValid(settings))
            throw new ArgumentException("Application settings are invalid.", nameof(settings));
    }
}
