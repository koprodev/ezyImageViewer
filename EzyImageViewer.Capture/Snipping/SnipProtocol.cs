namespace EzyImageViewer.Capture.Snipping;

/// <summary>Parsed Snipping Tool callback (response parameters arrive as the redirect URI's query).</summary>
public sealed record SnipResponse(int Code, string Reason, string? CorrelationId, string? FileAccessToken);

/// <summary>
/// Official Snipping Tool launch protocol (FR-CAP-001, Q7=b): request URIs against
/// ms-screenclip://capture and the redirect-uri callback contract. Pure string work — launching
/// and token redemption live elsewhere. The scheme below must match the package manifest's
/// windows.protocol registration; responses are only delivered to packaged callers.
/// </summary>
public static class SnipProtocol
{
    public const string Scheme = "ezyimageviewer";
    public const string ResponseHost = "capture-response";
    public const string UserAgent = "ezyImageViewer";

    /// <summary>Pinned protocol version: requests stay on known semantics across Snipping Tool updates.</summary>
    public const string ApiVersion = "1.2";

    public const int CodeSuccess = 200;
    public const int CodeUserCancelled = 499;

    public static string RedirectUri => $"{Scheme}://{ResponseHost}";

    /// <summary>Rectangle pre-selected with every snip mode available; mode parameters are
    /// value-less by spec. The redirect URI carries no query of its own, so the callback's query
    /// is exactly the response parameters.</summary>
    public static Uri BuildImageCaptureUri(string correlationId) => new(
        "ms-screenclip://capture/image?rectangle&enabledModes=SnippingAllModes"
        + $"&user-agent={UserAgent}&api-version={ApiVersion}"
        + $"&x-request-correlation-id={Uri.EscapeDataString(correlationId)}"
        + $"&redirect-uri={RedirectUri}");

    public static bool IsResponse(Uri uri) =>
        string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, ResponseHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>False for foreign schemes/hosts or a malformed/missing status code.</summary>
    public static bool TryParseResponse(Uri uri, out SnipResponse response)
    {
        response = null!;
        if (!IsResponse(uri))
            return false;

        int? code = null;
        var reason = "";
        string? correlationId = null;
        string? token = null;
        var query = uri.Query;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            var key = split < 0 ? pair : pair[..split];
            var value = split < 0 ? "" : Uri.UnescapeDataString(pair[(split + 1)..]);
            if (key.Equals("code", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(value, out var parsed))
                    code = parsed;
            }
            else if (key.Equals("reason", StringComparison.OrdinalIgnoreCase))
                reason = value;
            else if (key.Equals("x-request-correlation-id", StringComparison.OrdinalIgnoreCase))
                correlationId = value;
            else if (key.Equals("file-access-token", StringComparison.OrdinalIgnoreCase))
                token = value;
        }

        if (code is null)
            return false;
        response = new SnipResponse(code.Value, reason, correlationId, token);
        return true;
    }
}
