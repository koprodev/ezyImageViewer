using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class ReleaseDistributionPolicyTests
{
    [Fact]
    public void LatestReleasePage_IsTheExactFixedPublicGitHubPage()
    {
        var page = ReleaseDistributionPolicy.LatestReleasePage;

        Assert.Equal(
            "https://github.com/koprodev/ezyImageViewer/releases/latest",
            page.OriginalString);
        Assert.Equal(Uri.UriSchemeHttps, page.Scheme);
        Assert.Equal("github.com", page.IdnHost);
        Assert.True(page.IsDefaultPort);
        Assert.Empty(page.UserInfo);
        Assert.Empty(page.Query);
        Assert.Empty(page.Fragment);
        Assert.Equal(
            "/koprodev/ezyImageViewer/releases/latest",
            page.AbsolutePath);
    }

    [Fact]
    public void ProjectAndSupportPages_AreFixedPublicGitHubPages()
    {
        Assert.Equal(
            "https://github.com/koprodev/ezyImageViewer",
            ReleaseDistributionPolicy.ProjectPage.OriginalString);
        Assert.Equal(
            "https://github.com/sponsors/koprodev",
            ReleaseDistributionPolicy.SupportPage.OriginalString);
        foreach (var page in new[]
        {
            ReleaseDistributionPolicy.ProjectPage,
            ReleaseDistributionPolicy.SupportPage,
        })
        {
            Assert.Equal(Uri.UriSchemeHttps, page.Scheme);
            Assert.Equal("github.com", page.IdnHost);
            Assert.Empty(page.UserInfo);
            Assert.Empty(page.Query);
            Assert.Empty(page.Fragment);
        }
    }

    [Fact]
    public void ReleaseApiAndAcceptedPages_StayInsideTheFixedRepository()
    {
        Assert.Equal(
            "https://api.github.com/repos/koprodev/ezyImageViewer/releases?per_page=20",
            ReleaseDistributionPolicy.ReleasesApi.OriginalString);
        Assert.True(ReleaseDistributionPolicy.IsTrustedReleasePage(new Uri(
            "https://github.com/koprodev/ezyImageViewer/releases/tag/v1.0.43-preview.1")));
        Assert.False(ReleaseDistributionPolicy.IsTrustedReleasePage(new Uri(
            "https://example.com/koprodev/ezyImageViewer/releases/tag/v1.0.43")));
        Assert.False(ReleaseDistributionPolicy.IsTrustedReleasePage(new Uri(
            "https://github.com/other/ezyImageViewer/releases/tag/v1.0.43")));
        Assert.False(ReleaseDistributionPolicy.IsTrustedReleasePage(new Uri(
            "https://github.com/koprodev/ezyImageViewer/releases/tag/v1.0.43?download=1")));
    }
}
