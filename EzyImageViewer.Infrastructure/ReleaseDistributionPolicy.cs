namespace EzyImageViewer.Infrastructure;

public static class ReleaseDistributionPolicy
{
    public const string ReleasesApiUrl =
        "https://api.github.com/repos/koprodev/ezyImageViewer/releases?per_page=20";

    public static Uri ReleasesApi { get; } =
        new(ReleasesApiUrl, UriKind.Absolute);

    public const string LatestReleasePageUrl =
        "https://github.com/koprodev/ezyImageViewer/releases/latest";

    public static Uri LatestReleasePage { get; } =
        new(LatestReleasePageUrl, UriKind.Absolute);

    public const string ProjectPageUrl =
        "https://github.com/koprodev/ezyImageViewer";

    public static Uri ProjectPage { get; } =
        new(ProjectPageUrl, UriKind.Absolute);

    public const string SupportPageUrl = "https://github.com/sponsors/koprodev";

    public static Uri SupportPage { get; } =
        new(SupportPageUrl, UriKind.Absolute);

    public static bool IsTrustedReleasePage(Uri page)
    {
        ArgumentNullException.ThrowIfNull(page);
        return page.IsAbsoluteUri
            && page.Scheme == Uri.UriSchemeHttps
            && page.IdnHost.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && page.IsDefaultPort
            && page.UserInfo.Length == 0
            && page.Query.Length == 0
            && page.Fragment.Length == 0
            && page.AbsolutePath.StartsWith(
                "/koprodev/ezyImageViewer/releases/tag/",
                StringComparison.Ordinal);
    }
}
