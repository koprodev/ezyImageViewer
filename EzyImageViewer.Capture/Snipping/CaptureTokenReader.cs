using EzyImageViewer.Capture.Clipboard;
using Windows.ApplicationModel.DataTransfer;

namespace EzyImageViewer.Capture.Snipping;

/// <summary>
/// 캡처 도구 파일 접근 토큰을 소유권 있는 메모리 데이터로 교환.
/// OS 계약상 토큰은 한 번 쓰면 끝. 읽기 실패는 재시도 대신 호출자에게 전달.
/// </summary>
public static class CaptureTokenReader
{
    public static async Task<ClipboardImagePayload?> RedeemAsync(
        string token, long maxBytes, CancellationToken cancellationToken)
    {
        var file = await SharedStorageAccessManager.RedeemTokenForFileAsync(token)
            .AsTask(cancellationToken).ConfigureAwait(false);
        using var stream = await file.OpenStreamForReadAsync().ConfigureAwait(false);
        return await ReadWithinBudgetAsync(stream, maxBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>경계값을 단위 테스트하도록 WinRT와 분리한 용량 문턱. null이면 비었거나 한도 초과.</summary>
    public static async Task<ClipboardImagePayload?> ReadWithinBudgetAsync(
        Stream stream, long maxBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (stream.Length <= 0 || stream.Length > maxBytes)
            return null;

        var bytes = new byte[stream.Length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        // 캡처 도구는 PNG 생성. 다른 형식 판별은 열기 단계에 맡김.
        return new ClipboardImagePayload(bytes, ClipboardImagePayload.Png);
    }
}
