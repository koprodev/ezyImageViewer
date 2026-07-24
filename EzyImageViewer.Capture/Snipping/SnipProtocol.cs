namespace EzyImageViewer.Capture.Snipping;

/// <summary>캡처 도구 콜백 해석 결과. 응답 매개변수는 리디렉션 URI 쿼리로 도착.</summary>
public sealed record SnipResponse(int Code, string Reason, string? CorrelationId, string? FileAccessToken);

/// <summary>
/// 공식 캡처 도구 실행 프로토콜(FR-CAP-001, Q7=b).
/// ms-screenclip://capture 요청과 redirect-uri 콜백 계약만 담당.
/// 실행·토큰 교환은 다른 곳의 몫. 스킴은 패키지 매니페스트의 windows.protocol 등록과 같아야 함.
/// 응답은 패키지 ID가 있는 호출자에게만 전달.
/// </summary>
public static class SnipProtocol
{
    public const string Scheme = "ezyimageviewer";
    public const string ResponseHost = "capture-response";
    public const string UserAgent = "ezyImageViewer";

    /// <summary>프로토콜 버전 고정. 캡처 도구가 갱신돼도 아는 규칙으로 요청.</summary>
    public const string ApiVersion = "1.2";

    public const int CodeSuccess = 200;
    public const int CodeUserCancelled = 499;

    public static string RedirectUri => $"{Scheme}://{ResponseHost}";

    /// <summary>사각형을 기본 선택하고 모든 캡처 모드 허용. 모드 매개변수는 규격상 값 없음.
    /// 리디렉션 URI 자체 쿼리가 없어 콜백 쿼리 전체가 곧 응답 매개변수.</summary>
    public static Uri BuildImageCaptureUri(string correlationId) => new(
        "ms-screenclip://capture/image?rectangle&enabledModes=SnippingAllModes"
        + $"&user-agent={UserAgent}&api-version={ApiVersion}"
        + $"&x-request-correlation-id={Uri.EscapeDataString(correlationId)}"
        + $"&redirect-uri={RedirectUri}");

    public static bool IsResponse(Uri uri) =>
        string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Host, ResponseHost, StringComparison.OrdinalIgnoreCase);

    /// <summary>스킴·호스트가 다르거나 상태 코드가 없거나 깨졌으면 false.</summary>
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
