namespace EzyImageViewer.Infrastructure;

public static class ReleaseDistributionPolicy
{
    public const string LatestReleasePageUrl =
        "https://github.com/koprodev/ezy-image-viewer-releases/releases/latest";

    public static Uri LatestReleasePage { get; } =
        new(LatestReleasePageUrl, UriKind.Absolute);
}
