using System.Text;
using EzyImageViewer.CodecProtocol;
using EzyImageViewer.Tests.Codec;
using Xunit;

namespace EzyImageViewer.Tests.Spikes;

public sealed class PdfRenderSpikeTests
{
    [Fact]
    public async Task CodecHost_InspectReadsMetadataWithoutRasterPayload()
    {
        var pdf = BuildPdf(pageCount: 3);
        var request = CodecHostTestClient.Request(
            CodecOperation.Inspect,
            CodecFormat.Pdf,
            pdf.Length);

        var result = await CodecHostTestClient.RunAsync(request, pdf);

        var response = AssertHostSuccess(result);
        Assert.Equal((200, 100, 3),
            (response.NativeWidth, response.NativeHeight, response.PageCount));
        Assert.Equal((0, 0, 0, 0L),
            (response.Width, response.Height, response.Stride, response.PayloadLength));
        Assert.Equal("pdf-page", response.Diagnostic);
        Assert.Empty(result.Payload);
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(1, 1)]
    [InlineData(2, 0)]
    public async Task CodecHost_DecodeRendersRequestedFirstMiddleLastPageAtBoundingSize(
        int pageIndex,
        int dominantBgraChannel)
    {
        var pdf = BuildPdf(pageCount: 3);
        var request = CodecHostTestClient.Request(
            CodecOperation.Decode,
            CodecFormat.Pdf,
            pdf.Length,
            pageIndex,
            targetWidth: 40,
            targetHeight: 40);

        var result = await CodecHostTestClient.RunAsync(request, pdf);

        var response = AssertHostSuccess(result);
        Assert.Equal((40, 20, 200, 100, 3),
            (response.Width, response.Height, response.NativeWidth, response.NativeHeight, response.PageCount));
        Assert.Equal((long)response.Stride * response.Height, result.Payload.Length);
        AssertDominantBgra(
            result.Payload,
            response.Stride,
            x: 20,
            y: 10,
            dominantBgraChannel);
    }

    [Fact]
    public async Task CodecHost_PdfDecodePreservesTransparentPageBackground()
    {
        var pdf = BuildPdf(pageCount: 1, fillPages: false);
        var request = CodecHostTestClient.Request(
            CodecOperation.Decode,
            CodecFormat.Pdf,
            pdf.Length,
            pageIndex: 0,
            targetWidth: 20,
            targetHeight: 20);

        var result = await CodecHostTestClient.RunAsync(request, pdf);

        var response = AssertHostSuccess(result);
        Assert.Equal((20, 10), (response.Width, response.Height));
        Assert.All(
            Enumerable.Range(0, response.Width * response.Height),
            pixel => Assert.Equal(0, result.Payload[pixel * 4 + 3]));
    }

    [Fact]
    public async Task CodecHost_PdfRejectsOutOfRangePageBeforeRender()
    {
        var pdf = BuildPdf(pageCount: 2);
        var outOfRange = CodecHostTestClient.Request(
            CodecOperation.Decode,
            CodecFormat.Pdf,
            pdf.Length,
            pageIndex: 2);
        var outOfRangeResult = await CodecHostTestClient.RunAsync(outOfRange, pdf);

        Assert.Equal(CodecResultCode.InvalidRequest, AssertResponse(outOfRangeResult).Result);
        Assert.Empty(outOfRangeResult.Payload);
    }

    [Fact]
    public async Task CodecHost_PdfRejectsMismatchedSignatureWithoutNativeDecode()
    {
        var invalid = BuildPdf(pageCount: 1);
        invalid[0] = (byte)'!';
        var request = CodecHostTestClient.Request(
            CodecOperation.Inspect,
            CodecFormat.Pdf,
            invalid.Length);

        var result = await CodecHostTestClient.RunAsync(request, invalid);

        var response = AssertResponse(result);
        Assert.Equal(CodecResultCode.CorruptInput, response.Result);
        Assert.Equal("pdf-signature", response.Diagnostic);
    }

    [Fact]
    public async Task CodecHost_RejectsTrailingStandardInputBytes()
    {
        var pdf = BuildPdf(pageCount: 1);
        var request = CodecHostTestClient.Request(
            CodecOperation.Inspect,
            CodecFormat.Pdf,
            pdf.Length);

        var result = await CodecHostTestClient.RunAsync(
            request,
            pdf,
            trailingInput: new byte[] { 0xA5 });

        var response = AssertResponse(result);
        Assert.Equal(CodecResultCode.InvalidRequest, response.Result);
        Assert.Equal("trailing-input", response.Diagnostic);
        Assert.Empty(result.Payload);
    }

    private static CodecResponse AssertHostSuccess(CodecHostTestResult result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        var response = AssertResponse(result);
        Assert.Equal(CodecResultCode.Success, response.Result);
        return response;
    }

    private static CodecResponse AssertResponse(CodecHostTestResult result)
    {
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardError);
        return Assert.IsType<CodecResponse>(result.Response);
    }

    private static void AssertDominantBgra(
        byte[] pixels,
        int stride,
        int x,
        int y,
        int dominantBgraChannel)
    {
        var offset = checked(y * stride + x * 4);
        for (var channel = 0; channel < 3; channel++)
        {
            if (channel == dominantBgraChannel)
                Assert.True(pixels[offset + channel] > 180);
            else
                Assert.True(pixels[offset + channel] < 80);
        }
        Assert.Equal(255, pixels[offset + 3]);
    }

    private static byte[] BuildPdf(int pageCount, bool fillPages = true)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pageCount).Select(i => $"{i + 3} 0 R"))}] /Count {pageCount} >>",
        };
        for (var page = 0; page < pageCount; page++)
        {
            var contentId = 3 + pageCount + page;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 100] /Contents {contentId} 0 R >>");
        }
        for (var page = 0; page < pageCount; page++)
        {
            var color = (page % 3) switch
            {
                0 => "0.9 0.1 0.1",
                1 => "0.1 0.9 0.1",
                _ => "0.1 0.1 0.9",
            };
            var content = fillPages ? $"{color} rg 0 0 200 100 re f\n" : string.Empty;
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}endstream");
        }

        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true)
        {
            NewLine = "\n",
        };
        var offsets = new List<long>();
        void Write(string value)
        {
            writer.Write(value);
            writer.Flush();
        }

        Write("%PDF-1.4\n");
        for (var index = 0; index < objects.Count; index++)
        {
            offsets.Add(stream.Position);
            Write($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }

        var xref = stream.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets)
            Write($"{offset:D10} 00000 n \n");
        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return stream.ToArray();
    }
}
