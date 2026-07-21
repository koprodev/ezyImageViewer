using EzyImageViewer.Capture.Snipping;
using Xunit;

namespace EzyImageViewer.Tests.Capture;

/// <summary>FR-CAP-001 (Q7=b): the official ms-screenclip request URI and the redirect-uri
/// callback parser, against the documented protocol (v1.2).</summary>
public sealed class SnipProtocolTests
{
    [Fact]
    public void RequestUri_CarriesTheDocumentedShape()
    {
        var uri = SnipProtocol.BuildImageCaptureUri("aaaa0000-bb11-2222-33cc-444444dddddd");

        Assert.Equal("ms-screenclip", uri.Scheme);
        Assert.Equal("capture", uri.Host);
        Assert.Equal("/image", uri.AbsolutePath);
        var query = uri.Query;
        // The mode parameter must be value-less by spec.
        Assert.Contains("?rectangle&", query);
        Assert.Contains("&user-agent=ezyImageViewer", query);
        Assert.Contains("&api-version=1.2", query);
        Assert.Contains("&x-request-correlation-id=aaaa0000-bb11-2222-33cc-444444dddddd", query);
        Assert.EndsWith("&redirect-uri=ezyimageviewer://capture-response", query);
    }

    [Fact]
    public void SuccessResponse_ParsesCodeTokenAndCorrelation()
    {
        var parsed = SnipProtocol.TryParseResponse(new Uri(
            "ezyimageviewer://capture-response?code=200&reason=Success"
            + "&x-request-correlation-id=abc&file-access-token=tok-1"), out var response);

        Assert.True(parsed);
        Assert.Equal(SnipProtocol.CodeSuccess, response.Code);
        Assert.Equal("Success", response.Reason);
        Assert.Equal("abc", response.CorrelationId);
        Assert.Equal("tok-1", response.FileAccessToken);
    }

    [Fact]
    public void CancelResponse_ParsesWithoutAToken_AndUnescapesTheReason()
    {
        var parsed = SnipProtocol.TryParseResponse(new Uri(
            "ezyimageviewer://capture-response?code=499"
            + "&reason=Client%20Closed%20Request%20-%20User%20Cancelled%20the%20Snip"
            + "&x-request-correlation-id=abc"), out var response);

        Assert.True(parsed);
        Assert.Equal(SnipProtocol.CodeUserCancelled, response.Code);
        Assert.Equal("Client Closed Request - User Cancelled the Snip", response.Reason);
        Assert.Null(response.FileAccessToken);
    }

    [Fact]
    public void ForeignSchemeOrHost_IsNotAResponse()
    {
        Assert.False(SnipProtocol.TryParseResponse(
            new Uri("other-app://capture-response?code=200"), out _));
        Assert.False(SnipProtocol.TryParseResponse(
            new Uri("ezyimageviewer://something-else?code=200"), out _));
    }

    [Fact]
    public void MissingOrMalformedCode_IsRejected()
    {
        Assert.False(SnipProtocol.TryParseResponse(
            new Uri("ezyimageviewer://capture-response?reason=Success"), out _));
        Assert.False(SnipProtocol.TryParseResponse(
            new Uri("ezyimageviewer://capture-response?code=abc"), out _));
    }

    [Fact]
    public void ResponseKeys_AreCaseInsensitive()
    {
        var parsed = SnipProtocol.TryParseResponse(new Uri(
            "EZYIMAGEVIEWER://Capture-Response?Code=200&File-Access-Token=tok"), out var response);

        Assert.True(parsed);
        Assert.Equal(SnipProtocol.CodeSuccess, response.Code);
        Assert.Equal("tok", response.FileAccessToken);
    }

    /// <summary>The packaged manifest must register exactly the scheme the code redirects to —
    /// a drift here silently kills the callback ([25차] 후속).</summary>
    [Fact]
    public void ManifestTemplate_RegistersTheRedirectScheme()
    {
        var template = File.ReadAllText(RepoFile("packaging", "AppxManifest.template.xml"));

        Assert.Contains($"<uap:Protocol Name=\"{SnipProtocol.Scheme}\"", template, StringComparison.Ordinal);
        Assert.Contains("Category=\"windows.protocol\"", template, StringComparison.Ordinal);
        Assert.Contains("EntryPoint=\"Windows.FullTrustApplication\"", template, StringComparison.Ordinal);
        Assert.Contains("<rescap:Capability Name=\"runFullTrust\"", template, StringComparison.Ordinal);
    }

    private static string RepoFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (!File.Exists(Path.Combine(directory.FullName, "EzyImageViewer.slnx")))
                continue;
            return Path.Combine([directory.FullName, .. segments]);
        }
        throw new DirectoryNotFoundException("Repository root was not found from the test output directory.");
    }
}
