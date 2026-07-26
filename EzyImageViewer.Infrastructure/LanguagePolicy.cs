using System.Globalization;

namespace EzyImageViewer.Infrastructure;

/// <summary>UI 언어 하나. 표시명은 그 언어 원어민이 읽을 이름이라 번역하지 않는다.</summary>
/// <param name="Tag">BCP-47 태그. `Strings\{Tag}\Resources.resw` 폴더명과 같아야 한다.</param>
/// <param name="NativeName">언어 선택기에 그대로 뿌리는 자국어 표기.</param>
/// <param name="AnnotationFont">주석 텍스트 기본 글꼴. Windows 동봉 글꼴만 고른다.</param>
public sealed record UiLanguage(string Tag, string NativeName, string AnnotationFont);

/// <summary>
/// 지원 언어 SSOT. 리소스 폴더·MSIX 매니페스트·설정 화면·기본 글꼴이 전부 이 목록을 본다.
/// 목록을 늘리면 계약 테스트가 resw 폴더와 매니페스트를 같이 확인한다.
/// </summary>
public static class LanguagePolicy
{
    /// <summary>설정에 저장하는 "Windows 표시 언어 따르기" 값.</summary>
    public const string SystemDefault = "";

    /// <summary>최종 폴백. csproj의 DefaultLanguage와 반드시 같아야 한다.</summary>
    public const string FallbackTag = "en-US";

    /// <summary>라틴·키릴을 두루 덮는 무난한 기본값. 매칭 실패 시 여기로 떨어진다.</summary>
    public const string FallbackAnnotationFont = "Segoe UI";

    /// <summary>en-US가 첫 번째다. 기본이자 최종 폴백이라 선택기 맨 위에 온다.</summary>
    public static readonly IReadOnlyList<UiLanguage> Supported =
    [
        new("en-US", "English (United States)", "Segoe UI"),
        new("ko-KR", "한국어", "Malgun Gothic"),
        new("zh-CN", "简体中文", "Microsoft YaHei UI"),
        new("es-419", "Español (Latinoamérica)", "Segoe UI"),
        new("ja-JP", "日本語", "Yu Gothic UI"),
        new("pt-BR", "Português (Brasil)", "Segoe UI"),
        new("en-IN", "English (India)", "Segoe UI"),
        new("hi-IN", "हिन्दी", "Nirmala UI"),
        new("de-DE", "Deutsch", "Segoe UI"),
        new("fr-FR", "Français", "Segoe UI"),
        new("ru-RU", "Русский", "Segoe UI"),
        new("id-ID", "Indonesia", "Segoe UI"),
    ];

    public static IReadOnlyList<string> SupportedTags =>
        Supported.Select(language => language.Tag).ToArray();

    /// <summary>
    /// App 계층이 실제 적용한 UI 언어를 여기 적어 둔다. 비어 있으면 스레드 UI 컬처로 판단한다.
    /// 새 설정을 만들 때 기본 글꼴을 고르는 용도라 설정 파일이 이미 있으면 쓰이지 않는다.
    /// </summary>
    public static string EffectiveUiLanguage { get; set; } = SystemDefault;

    /// <summary>설정에 저장 가능한 값인가. 시스템 기본이거나 지원 목록에 정확히 있어야 한다.</summary>
    public static bool IsSelectable(string? tag) =>
        string.IsNullOrEmpty(tag)
        || Supported.Any(language => string.Equals(
            language.Tag, tag, StringComparison.OrdinalIgnoreCase));

    /// <summary>태그를 지원 언어로 접는다. 정확 일치 → 기본 하위태그 일치 → 없음.</summary>
    public static UiLanguage? Resolve(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return null;
        foreach (var language in Supported)
        {
            if (string.Equals(language.Tag, tag, StringComparison.OrdinalIgnoreCase))
                return language;
        }

        // ja, pt-PT처럼 변형이 들어와도 같은 언어면 같은 글꼴을 쓴다.
        var primary = PrimarySubtag(tag);
        foreach (var language in Supported)
        {
            if (string.Equals(PrimarySubtag(language.Tag), primary, StringComparison.OrdinalIgnoreCase))
                return language;
        }
        return null;
    }

    public static string DefaultAnnotationFont(string? tag) =>
        Resolve(tag)?.AnnotationFont ?? FallbackAnnotationFont;

    /// <summary>새 설정이 집어 갈 주석 글꼴. 힌디에 한글 글꼴이 붙는 사고를 막는 자리다.</summary>
    public static string CurrentAnnotationFont => DefaultAnnotationFont(
        string.IsNullOrEmpty(EffectiveUiLanguage)
            ? CultureInfo.CurrentUICulture.Name
            : EffectiveUiLanguage);

    private static string PrimarySubtag(string tag)
    {
        var separator = tag.IndexOf('-', StringComparison.Ordinal);
        return separator < 0 ? tag : tag[..separator];
    }
}
