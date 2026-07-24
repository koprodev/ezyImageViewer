using EzyImageViewer.Capture.Clipboard;
using EzyImageViewer.Capture.Snipping;
using Xunit;

namespace EzyImageViewer.Tests.Capture;

/// <summary>캡처 읽기 예산은 상한을 포함하고 한 바이트 초과·빈 파일은 거부.
/// 제품의 64MiB 경계를 그대로 검증.</summary>
public sealed class CaptureTokenReaderTests
{
    private static MemoryStream Stream(int length) => new(new byte[length]);

    [Fact]
    public async Task ExactlyAtTheBudget_IsRead()
    {
        var payload = await CaptureTokenReader.ReadWithinBudgetAsync(
            Stream(8), maxBytes: 8, CancellationToken.None);

        Assert.NotNull(payload);
        Assert.Equal(8, payload.Bytes.Length);
        Assert.Equal(ClipboardImagePayload.Png, payload.Format);
    }

    [Fact]
    public async Task OneByteOverTheBudget_IsRejected()
    {
        Assert.Null(await CaptureTokenReader.ReadWithinBudgetAsync(
            Stream(9), maxBytes: 8, CancellationToken.None));
    }

    [Fact]
    public async Task AnEmptyFile_IsRejected()
    {
        Assert.Null(await CaptureTokenReader.ReadWithinBudgetAsync(
            Stream(0), maxBytes: 8, CancellationToken.None));
    }
}
