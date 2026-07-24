using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Infrastructure;

/// <summary>파일 연결 설정 화면에 보이는 사용자용 확장자 그룹.</summary>
public sealed record FileAssociationGroup(string Key, IReadOnlyList<string> Extensions);

/// <summary>
/// 사용자별 "연결 프로그램" 등록의 레지스트리 구조와 확장자 정책.
/// 앱과 설치 프로그램이 같은 키를 다루도록 installer/common/Product.wxs와 맞춰야 함.
/// 후보만 등록하며 기존 기본 앱은 덮어쓰지 않음(FR-APP-001).
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

    /// <summary>Windows 11(빌드 22000+)은 이 앱의 기본 앱 페이지로 바로 이동.
    /// 매개변수를 지원하지 않는 Windows 10은 일반 기본 앱 페이지 사용.</summary>
    public static Uri GetDefaultAppsSettingsUri() =>
        Environment.OSVersion.Version.Build >= 22000
            ? new Uri(DefaultAppsSettingsUri + "?registeredAppUser="
                + Uri.EscapeDataString(RegisteredApplicationName))
            : new Uri(DefaultAppsSettingsUri);

    /// <summary>설치 프로그램 기본 묶음(FR-APP-001). 설정 화면에는 "필수 파일"로 표시.</summary>
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

    /// <summary>그룹은 제품이 여는 형식과 맞아야 함. 여기와 테스트에서 이중 확인.</summary>
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
