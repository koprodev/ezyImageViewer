namespace EzyImageViewer.Infrastructure;

public static class ExternalLinkPolicy
{
    public const string SupportPageUrl = "https://github.com/sponsors/koprodev";

    public static Uri SupportPage { get; } =
        new(SupportPageUrl, UriKind.Absolute);
}
