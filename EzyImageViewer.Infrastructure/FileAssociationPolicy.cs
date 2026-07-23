using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Infrastructure;

/// <summary>A user-facing extension group shown in the file-association settings page.</summary>
public sealed record FileAssociationGroup(string Key, IReadOnlyList<string> Extensions);

/// <summary>
/// Registry shape and extension policy for per-user "Open With" registration. Values must stay
/// in parity with installer/common/Product.wxs so the app and Setup manage the same keys
/// (FR-APP-001: candidates only, the user's existing default app is never overridden).
/// </summary>
public static class FileAssociationPolicy
{
    public const string ProgId = "ezyImageViewer.Image";
    public const string ProgIdDisplayName = "ezy Image Viewer Image";
    public const string ProgIdKeyPath = @"Software\Classes\" + ProgId;
    public const string CapabilitiesKeyPath = @"Software\koprodev\ezy Image Viewer\Capabilities";
    public const string FileAssociationsKeyPath = CapabilitiesKeyPath + @"\FileAssociations";
    public const string RegisteredApplicationsKeyPath = @"Software\RegisteredApplications";
    public const string RegisteredApplicationName = "ezy Image Viewer";
    public const string ApplicationDescription = "이미지 보기 및 편집";
    public const string DefaultAppsSettingsUri = "ms-settings:defaultapps";

    /// <summary>Windows 11 (build 22000+) deep-links straight to this app's default-app page;
    /// Windows 10 does not support the parameter, so it gets the plain default-apps page.</summary>
    public static Uri GetDefaultAppsSettingsUri() =>
        Environment.OSVersion.Version.Build >= 22000
            ? new Uri(DefaultAppsSettingsUri + "?registeredAppUser="
                + Uri.EscapeDataString(RegisteredApplicationName))
            : new Uri(DefaultAppsSettingsUri);

    /// <summary>The Setup default set (FR-APP-001) surfaced as "필수 파일" in the settings page.</summary>
    public static readonly IReadOnlyList<string> EssentialExtensions =
    [
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff",
    ];

    public static readonly IReadOnlyList<FileAssociationGroup> Groups =
    [
        new("raster",
        [
            ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".dib",
            ".rle", ".gif", ".tif", ".tiff", ".ico", ".webp",
        ]),
        new("codec", [".avif", ".heic", ".heif", ".hif"]),
        new("vector", [".svg", ".svgz"]),
    ];

    public static readonly IReadOnlyList<string> SelectableExtensions =
        Groups.SelectMany(group => group.Extensions).ToArray();

    private static readonly IReadOnlySet<string> SelectableSet = new HashSet<string>(
        SelectableExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>Groups must track the product's viewable formats; verified here and by tests.</summary>
    static FileAssociationPolicy()
    {
        if (!SelectableSet.SetEquals(ImageFormatCatalog.ViewableExtensions))
            throw new InvalidOperationException(
                "FileAssociationPolicy groups are out of sync with ImageFormatCatalog.ViewableExtensions.");
    }

    public static bool IsSelectable(string extension) => SelectableSet.Contains(extension);

    public static string OpenWithProgidsKeyPath(string extension)
    {
        if (!IsSelectable(extension))
            throw new ArgumentException(
                $"'{extension}' is not a selectable association extension.", nameof(extension));
        return $@"Software\Classes\{extension}\OpenWithProgids";
    }
}
