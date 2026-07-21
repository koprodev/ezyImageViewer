using System.Buffers.Binary;
using EzyImageViewer.CodecProtocol;
using EzyImageViewer.Tests.Codec;
using ImageMagick;
using Xunit;

namespace EzyImageViewer.Tests.Spikes;

public sealed class PsdCompositeSpikeTests
{
    [Fact]
    public async Task CodecHost_PsdInspectReportsSingleCompositePage()
    {
        var psd = BuildRgbPsd(width: 4, height: 3);
        var request = CodecHostTestClient.Request(
            CodecOperation.Inspect,
            CodecFormat.Psd,
            psd.Length);

        var result = await CodecHostTestClient.RunAsync(request, psd);

        var response = AssertHostSuccess(result);
        Assert.Equal((4, 3, 1),
            (response.NativeWidth, response.NativeHeight, response.PageCount));
        Assert.Equal("psd-composite", response.Diagnostic);
        Assert.Empty(result.Payload);
    }

    [Fact]
    public async Task CodecHost_PsdDecodeReturnsCompositeBgraOnly()
    {
        var psd = BuildRgbPsd(width: 4, height: 3);
        var request = CodecHostTestClient.Request(
            CodecOperation.Decode,
            CodecFormat.Psd,
            psd.Length,
            pageIndex: 0);

        var result = await CodecHostTestClient.RunAsync(request, psd);

        var response = AssertHostSuccess(result);
        Assert.Equal((4, 3, 16, 4, 3, 1),
            (response.Width, response.Height, response.Stride,
                response.NativeWidth, response.NativeHeight, response.PageCount));
        Assert.Equal(48, result.Payload.Length);
        for (var offset = 0; offset < result.Payload.Length; offset += 4)
        {
            Assert.Equal(0, result.Payload[offset]);
            Assert.Equal(0, result.Payload[offset + 1]);
            Assert.Equal(255, result.Payload[offset + 2]);
            Assert.Equal(255, result.Payload[offset + 3]);
        }
    }

    [Fact]
    public async Task CodecHost_PsdDecodePremultipliesTransparentCompositePixels()
    {
        byte[] psd;
        using (var image = new MagickImage(new MagickColor(255, 0, 0, 128), 2, 2))
            psd = image.ToByteArray(MagickFormat.Psd);
        var request = CodecHostTestClient.Request(
            CodecOperation.Decode,
            CodecFormat.Psd,
            psd.Length,
            pageIndex: 0);

        var result = await CodecHostTestClient.RunAsync(request, psd);

        _ = AssertHostSuccess(result);
        for (var offset = 0; offset < result.Payload.Length; offset += 4)
        {
            Assert.Equal(0, result.Payload[offset]);
            Assert.Equal(0, result.Payload[offset + 1]);
            Assert.Equal(128, result.Payload[offset + 2]);
            Assert.Equal(128, result.Payload[offset + 3]);
        }
    }

    [Theory]
    [InlineData(4, "psd-composite-cmyk-to-srgb")]
    [InlineData(9, "psd-composite-lab-to-srgb")]
    public async Task CodecHost_PsdDecodeConvertsColorModeToSrgbAndReportsIt(
        int expectedColorMode,
        string expectedDiagnostic)
    {
        byte[] psd = BuildLabPsd(width: 3, height: 2);
        if (expectedColorMode == 4)
        {
            using var image = new MagickImage(MagickColors.Red, 3, 2);
            image.ColorSpace = ColorSpace.CMYK;
            psd = image.ToByteArray(MagickFormat.Psd);
        }
        Assert.Equal(
            expectedColorMode,
            BinaryPrimitives.ReadUInt16BigEndian(psd.AsSpan(24, 2)));
        var request = CodecHostTestClient.Request(
            CodecOperation.Decode,
            CodecFormat.Psd,
            psd.Length,
            pageIndex: 0);

        var result = await CodecHostTestClient.RunAsync(request, psd);

        var response = AssertHostSuccess(result);
        Assert.Equal(expectedDiagnostic, response.Diagnostic);
        Assert.Equal((3, 2, 12), (response.Width, response.Height, response.Stride));
        Assert.Equal(checked(response.Stride * response.Height), result.Payload.Length);
        Assert.True(result.Payload[2] > 180);
        Assert.True(result.Payload[0] < 80);
        Assert.True(result.Payload[1] < 80);
        Assert.Equal(255, result.Payload[3]);
    }

    [Fact]
    public async Task CodecHost_PsdDecodeIgnoresLayerFramesAndReturnsCompositeFrame()
    {
        byte[] psd;
        using (var images = new MagickImageCollection())
        {
            images.Add(new MagickImage(MagickColors.Red, 4, 3));
            for (var layer = 0; layer < 5; layer++)
                images.Add(new MagickImage(MagickColors.Blue, 4, 3));
            psd = images.ToByteArray(MagickFormat.Psd);
        }
        var request = CodecHostTestClient.Request(
            CodecOperation.Decode,
            CodecFormat.Psd,
            psd.Length,
            pageIndex: 0);

        var result = await CodecHostTestClient.RunAsync(request, psd);

        var response = AssertHostSuccess(result);
        Assert.Equal(1, response.PageCount);
        Assert.Equal("psd-composite", response.Diagnostic);
        Assert.Equal(255, result.Payload[2]);
        Assert.Equal(0, result.Payload[0]);
    }

    [Fact]
    public async Task CodecHost_PsdRejectsNonCompositePageAndOversizedHeader()
    {
        var psd = BuildRgbPsd(width: 4, height: 3);
        var pageOne = CodecHostTestClient.Request(
            CodecOperation.Decode,
            CodecFormat.Psd,
            psd.Length,
            pageIndex: 1);
        var oversized = BuildRgbPsd(width: 65_501, height: 1, includePixels: false);
        var oversizedRequest = CodecHostTestClient.Request(
            CodecOperation.Inspect,
            CodecFormat.Psd,
            oversized.Length);

        var pageResult = await CodecHostTestClient.RunAsync(pageOne, psd);
        var oversizedResult = await CodecHostTestClient.RunAsync(oversizedRequest, oversized);

        Assert.Equal(CodecResultCode.InvalidRequest, AssertResponse(pageResult).Result);
        Assert.Equal(CodecResultCode.ResourceLimitExceeded, AssertResponse(oversizedResult).Result);
        Assert.Empty(pageResult.Payload);
        Assert.Empty(oversizedResult.Payload);
    }

    [Fact]
    public async Task CodecHost_PsdRejectsMismatchedSignatureBeforeMagick()
    {
        var psd = BuildRgbPsd(width: 4, height: 3);
        psd[0] = (byte)'!';
        var request = CodecHostTestClient.Request(
            CodecOperation.Inspect,
            CodecFormat.Psd,
            psd.Length);

        var result = await CodecHostTestClient.RunAsync(request, psd);

        var response = AssertResponse(result);
        Assert.Equal(CodecResultCode.CorruptInput, response.Result);
        Assert.Equal("psd-signature", response.Diagnostic);
    }

    [Fact]
    public async Task CodecHost_PsdRejectsMissingCompositePayloadBeforeMagick()
    {
        var psd = BuildRgbPsd(width: 4, height: 3)[..40];
        var request = CodecHostTestClient.Request(
            CodecOperation.Inspect,
            CodecFormat.Psd,
            psd.Length);

        var result = await CodecHostTestClient.RunAsync(request, psd);

        var response = AssertResponse(result);
        Assert.Equal(CodecResultCode.CorruptInput, response.Result);
        Assert.Equal("psd-composite-missing", response.Diagnostic);
        Assert.Empty(result.Payload);
    }

    private static CodecResponse AssertHostSuccess(CodecHostTestResult result)
    {
        var response = AssertResponse(result);
        Assert.True(
            response.Result == CodecResultCode.Success,
            $"result={response.Result}, diagnostic={response.Diagnostic}");
        return response;
    }

    private static CodecResponse AssertResponse(CodecHostTestResult result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        return Assert.IsType<CodecResponse>(result.Response);
    }

    private static byte[] BuildRgbPsd(int width, int height, bool includePixels = true)
    {
        var pixels = includePixels ? checked(width * height) : 0;
        var bytes = new byte[checked(26 + 4 + 4 + 4 + 2 + pixels * 3)];
        "8BPS"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(14), (uint)height);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(18), (uint)width);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(22), 8);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(24), 3);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(38), 0);
        if (includePixels)
            bytes.AsSpan(40, pixels).Fill(255);
        return bytes;
    }

    private static byte[] BuildLabPsd(int width, int height)
    {
        var psd = BuildRgbPsd(width, height);
        BinaryPrimitives.WriteUInt16BigEndian(psd.AsSpan(24), 9);
        var planeLength = checked(width * height);
        psd.AsSpan(40, planeLength).Fill(136);
        psd.AsSpan(40 + planeLength, planeLength).Fill(208);
        psd.AsSpan(40 + planeLength * 2, planeLength).Fill(195);
        return psd;
    }
}
