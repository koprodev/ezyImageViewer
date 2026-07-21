namespace EzyImageViewer.Infrastructure;

public enum ToolRailDock
{
    Vertical,
    Horizontal,
}

public sealed record ViewerLayoutPreferences
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public ToolRailDock ToolRailDock { get; init; } = ToolRailDock.Vertical;
}

public sealed class ViewerLayoutPreferencesStore(string path)
{
    private readonly string _path = string.IsNullOrWhiteSpace(path)
        ? throw new ArgumentException("Settings path cannot be empty.", nameof(path))
        : Path.GetFullPath(path);

    public static ViewerLayoutPreferencesStore CreateDefault()
    {
        return new ViewerLayoutPreferencesStore(AppDataPaths.CreateDefault().SettingsFile);
    }

    public ViewerLayoutPreferences Load()
    {
        var settings = new AppSettingsStore(_path).Load();
        return new ViewerLayoutPreferences { ToolRailDock = settings.ToolRailDock };
    }

    public void Save(ViewerLayoutPreferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.SchemaVersion != ViewerLayoutPreferences.CurrentSchemaVersion
            || !Enum.IsDefined(value.ToolRailDock))
            throw new ArgumentException("Viewer layout preferences are invalid.", nameof(value));

        new AppSettingsStore(_path).Update(current => current with
        {
            ToolRailDock = value.ToolRailDock,
        });
    }
}
