using EzyImageViewer.Capture.Snipping;
using Xunit;

namespace EzyImageViewer.Tests.Capture;

/// <summary>FR-CAP-001(Q7=b): 공식 ms-screenclip 요청 URI와 redirect-uri 콜백 해석기를 문서 규격(v1.2)과 대조.</summary>
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
        // 모드 매개변수는 규격상 값 없이 존재해야 함.
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

    /// <summary>패키지 매니페스트 스킴은 코드의 리디렉션 스킴과 정확히 같아야 함.
    /// 어긋나면 콜백이 소리 없이 죽음.</summary>
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
