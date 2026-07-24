namespace EzyImageViewer.Core.Imaging;

/// <summary>
/// 뷰어가 여는 파일 확장자의 플랫폼 중립 목록.
/// 탐색 계층이 Imaging에 역참조하지 않도록 Core에 둠.
/// </summary>
public static class ImageFormatCatalog
{
    /// <summary>M1 래스터 묶음(요건 §8.2 최초 릴리스 형식).</summary>
    public static readonly IReadOnlySet<string> RasterExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".dib", ".rle",
        ".gif", ".tif", ".tiff", ".ico", ".webp",
    };

    /// <summary>코덱 의존 형식(§8.2 조건부 단계). M8부터 코덱 확인 뒤 노출.</summary>
    public static readonly IReadOnlySet<string> ConditionalExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".heic", ".heif", ".hif",
    };

    public static readonly IReadOnlySet<string> VectorExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".svg", ".svgz",
    };

    /// <summary>뷰어가 여는 모든 형식. 패키지 ID나 외부 프로세스 호스트에 기대지 않아 빌드 종류마다 같음.</summary>
    public static readonly IReadOnlySet<string> ViewableExtensions = new HashSet<string>(
        RasterExtensions.Concat(ConditionalExtensions).Concat(VectorExtensions),
        StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlySet<string> KnownExtensions = ViewableExtensions;
}
