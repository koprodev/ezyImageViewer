namespace EzyImageViewer.Infrastructure;

public static class ReleaseDistributionPolicy
{
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
}
