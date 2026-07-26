using System.Text.Json;

namespace EzyImageViewer.Infrastructure;

/// <summary>
/// 설정 파일에서 UI 언어 한 줄만 미리 훔쳐본다.
/// WinUI가 자기 컨트롤 문자열(토글의 켬/끔 등)을 첫 XAML 로드 때 확정해 버리므로
/// 창이 생기기 전에 언어를 정해야 한다. 전체 설정 적재는 여전히 백그라운드 몫.
/// </summary>
public static class StartupLanguagePreference
{
    private const string PropertyName = "language";

    /// <summary>못 읽으면 시스템 기본을 뜻하는 빈 문자열. 시작 경로에서 죽을 일은 만들지 않는다.</summary>
    public static string Read() => Read(AppDataPaths.DefaultSettingsFile);

    public static string Read(string settingsFilePath)
    {
        try
        {
            if (!File.Exists(settingsFilePath))
                return LanguagePolicy.SystemDefault;
            using var stream = File.OpenRead(settingsFilePath);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty(PropertyName, out var value)
                || value.ValueKind != JsonValueKind.String)
            {
                return LanguagePolicy.SystemDefault;
            }

            var tag = value.GetString();
            return LanguagePolicy.IsSelectable(tag) ? tag! : LanguagePolicy.SystemDefault;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or NotSupportedException)
        {
            return LanguagePolicy.SystemDefault;
        }
    }
}
