namespace EzyImageViewer.Infrastructure;

/// <summary>
/// Store MSIX 매니페스트가 등록하는 파일 형식과 Windows 기본 앱 설정 진입점.
/// 앱은 레지스트리나 UserChoice를 직접 변경하지 않음.
/// </summary>
public static class FileAssociationPolicy
{
    public const string RegisteredApplicationName = "ezy Image Viewer";
    public const string DefaultAppsSettingsUri = "ms-settings:defaultapps";

    /// <summary>Windows 11(빌드 22000+)은 이 앱의 기본 앱 페이지로 바로 이동.
    /// 매개변수를 지원하지 않는 Windows 10은 일반 기본 앱 페이지 사용.</summary>
    public static Uri GetDefaultAppsSettingsUri() =>
        Environment.OSVersion.Version.Build >= 22000
            ? new Uri(DefaultAppsSettingsUri + "?registeredAppUser="
                + Uri.EscapeDataString(RegisteredApplicationName))
            : new Uri(DefaultAppsSettingsUri);

    /// <summary>AppxManifest.template.xml의 SupportedFileTypes와 같은 목록.</summary>
    public static readonly IReadOnlyList<string> EssentialExtensions =
    [
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff",
    ];

}
