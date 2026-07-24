namespace EzyImageViewer.Capture.Snipping;

/// <summary>
/// FR-CAP-001: Windows 캡처 오버레이 실행.
/// 패키지(Q7=b)는 공식 프로토콜과 redirect-uri 콜백 사용.
/// 비패키지 개발 실행은 구형 URI를 최선형으로 사용. 실패하면 Win+Shift+S를 안내하고 클립보드가 결과를 자동으로 엶.
/// </summary>
public static class CaptureLauncher
{
    public const string SnippingUri = "ms-screenclip:";

    /// <summary>공식 캡처 요청. 호출자 패키지 ID를 실어 응답을 돌려받으려면 Launcher.LaunchUriAsync가 필수.</summary>
    public static async Task<bool> LaunchOfficialAsync(string correlationId)
    {
        try
        {
            return await Windows.System.Launcher.LaunchUriAsync(
                SnipProtocol.BuildImageCaptureUri(correlationId));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UriFormatException)
        {
            return false;
        }
    }

    public static async Task<bool> LaunchSnippingAsync()
    {
        try
        {
            return await Windows.System.Launcher.LaunchUriAsync(new Uri(SnippingUri));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UriFormatException)
        {
            return false;
        }
    }

    /// <summary>오버레이를 띄우지 않는 지원 여부 확인. 현재 OS의 최선형 계약을 조용히 실측.</summary>
    public static async Task<bool> IsSnippingAvailableAsync()
    {
        try
        {
            var status = await Windows.System.Launcher.QueryUriSupportAsync(
                new Uri(SnippingUri), Windows.System.LaunchQuerySupportType.Uri);
            return status == Windows.System.LaunchQuerySupportStatus.Available;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return false;
        }
    }
}
