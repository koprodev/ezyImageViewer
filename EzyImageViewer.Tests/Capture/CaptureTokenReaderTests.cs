using EzyImageViewer.Capture.Clipboard;
using EzyImageViewer.Capture.Snipping;
using Xunit;

namespace EzyImageViewer.Tests.Capture;

/// <summary>The capture read budget is inclusive at the limit and rejects one byte over or an
/// empty file — the boundary the 64MiB product budget rides on ([25차] 보완 3).</summary>
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
