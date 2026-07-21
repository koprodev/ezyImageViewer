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
            "https://github.com/koprodev/ezy-image-viewer-releases/releases/latest",
            page.OriginalString);
        Assert.Equal(Uri.UriSchemeHttps, page.Scheme);
        Assert.Equal("github.com", page.IdnHost);
        Assert.True(page.IsDefaultPort);
        Assert.Empty(page.UserInfo);
        Assert.Empty(page.Query);
        Assert.Empty(page.Fragment);
        Assert.Equal(
            "/koprodev/ezy-image-viewer-releases/releases/latest",
            page.AbsolutePath);
    }
}
