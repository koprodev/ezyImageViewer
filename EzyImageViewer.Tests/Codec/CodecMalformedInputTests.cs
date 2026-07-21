using System.Buffers.Binary;
using System.Text;
using EzyImageViewer.CodecProtocol;
using Xunit;

namespace EzyImageViewer.Tests.Codec;

public sealed class CodecMalformedInputTests
{
    private const int MalformedProtocolExitCode = 65;

    [Fact]
    public async Task CodecHost_DeterministicallyRejectsMalformedProtocolFramesWithoutHanging()
    {
        var request = CodecHostTestClient.Request(
            CodecOperation.Probe,
            CodecFormat.None,
            inputLength: 0);
        var valid = await CodecHostGateTestClient.EncodeRequestAsync(request, []);
        var mutations = new (string Name, Action<byte[]> Apply)[]
        {
            ("magic", bytes => bytes[0] ^= 0x40),
            ("version", bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), 2)),
            ("message-kind", bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), 2)),
            ("frame-length", bytes => BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(8), 79)),
            ("operation", bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(40), ushort.MaxValue)),
            ("format", bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(42), ushort.MaxValue)),
            ("transport", bytes => BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(44), ushort.MaxValue)),
            ("flags", bytes => bytes[46] = 1),
            ("negative-input", bytes => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(48), -1)),
            ("reserved", bytes => bytes[76] = 1),
        };

        foreach (var mutation in mutations)
        {
            var frame = valid.ToArray();
            mutation.Apply(frame);

            var result = await CodecHostGateTestClient.RunRawAsync(
                frame,
                TimeSpan.FromSeconds(2));

            Assert.True(
                result.ExitCode == MalformedProtocolExitCode,
                $"mutation={mutation.Name}, exit={result.ExitCode}, stderr={result.StandardError}");
            Assert.Empty(result.StandardOutput);
            Assert.Contains("EIV_CODEC_HOST:malformed-request", result.StandardError);
            Assert.InRange(Encoding.UTF8.GetByteCount(result.StandardError), 1, 128);
        }
        foreach (var length in new[] { 0, 17, CodecWireProtocol.RequestHeaderSize - 1 })
        {
            var result = await CodecHostGateTestClient.RunRawAsync(
                valid.AsMemory(0, length),
                TimeSpan.FromSeconds(2));

            Assert.Equal(MalformedProtocolExitCode, result.ExitCode);
            Assert.Empty(result.StandardOutput);
            Assert.Contains("EIV_CODEC_HOST:malformed-request", result.StandardError);
        }
    }

    [Fact]
    public async Task CodecHost_RejectsPdfStructuralBombsAndCorruptionWithoutHanging()
    {
        var cases = new (string Name, byte[] Input, CodecResultCode Expected)[]
        {
            ("truncated-signature", "%PDF"u8.ToArray(), CodecResultCode.CorruptInput),
            ("corrupt-xref", CodecSyntheticDocumentFactory.BuildCorruptXrefPdf(), CodecResultCode.CorruptInput),
            ("huge-page", CodecSyntheticDocumentFactory.BuildPdf(1, width: 70_000), CodecResultCode.ResourceLimitExceeded),
            ("page-count", CodecSyntheticDocumentFactory.BuildPdf(10_001), CodecResultCode.ResourceLimitExceeded),
        };
        var signature = CodecSyntheticDocumentFactory.BuildPdf(1);
        signature[0] = (byte)'!';
        cases = [.. cases, ("signature", signature, CodecResultCode.CorruptInput)];

        foreach (var item in cases)
        {
            var request = CodecHostTestClient.Request(
                CodecOperation.Inspect,
                CodecFormat.Pdf,
                item.Input.Length);

            var result = await CodecHostGateTestClient.RunAsync(
                request,
                item.Input,
                TimeSpan.FromSeconds(8));

            AssertStructuredRefusal(item.Name, result, item.Expected);
        }
    }

    [Fact]
    public async Task CodecHost_RejectsPsdLengthCompressionAndAreaBombsWithoutHanging()
    {
        var sectionOne = CodecSyntheticDocumentFactory.BuildRgbPsd(2, 2);
        BinaryPrimitives.WriteUInt32BigEndian(sectionOne.AsSpan(26), uint.MaxValue);
        var sectionTwo = CodecSyntheticDocumentFactory.BuildRgbPsd(2, 2);
        BinaryPrimitives.WriteUInt32BigEndian(sectionTwo.AsSpan(30), uint.MaxValue);
        var sectionThree = CodecSyntheticDocumentFactory.BuildRgbPsd(2, 2);
        BinaryPrimitives.WriteUInt32BigEndian(sectionThree.AsSpan(34), uint.MaxValue);
        var invalidCompression = CodecSyntheticDocumentFactory.BuildRgbPsd(2, 2);
        BinaryPrimitives.WriteUInt16BigEndian(invalidCompression.AsSpan(38), 4);
        var truncatedRle = CodecSyntheticDocumentFactory.BuildRgbPsd(
            2,
            2,
            includePixels: false,
            compression: 1);
        Array.Resize(ref truncatedRle, truncatedRle.Length + 1);
        var invalidVersion = CodecSyntheticDocumentFactory.BuildRgbPsd(2, 2);
        BinaryPrimitives.WriteUInt16BigEndian(invalidVersion.AsSpan(4), 2);
        var nonZeroReserved = CodecSyntheticDocumentFactory.BuildRgbPsd(2, 2);
        nonZeroReserved[6] = 1;

        var cases = new (string Name, byte[] Input, CodecResultCode Expected)[]
        {
            ("color-mode-length", sectionOne, CodecResultCode.CorruptInput),
            ("image-resource-length", sectionTwo, CodecResultCode.CorruptInput),
            ("layer-mask-length", sectionThree, CodecResultCode.CorruptInput),
            ("compression", invalidCompression, CodecResultCode.CorruptInput),
            ("rle-row-table", truncatedRle, CodecResultCode.CorruptInput),
            ("version", invalidVersion, CodecResultCode.CorruptInput),
            ("reserved", nonZeroReserved, CodecResultCode.CorruptInput),
            ("dimension", CodecSyntheticDocumentFactory.BuildRgbPsd(65_501, 1, includePixels: false), CodecResultCode.ResourceLimitExceeded),
            ("area", CodecSyntheticDocumentFactory.BuildRgbPsd(65_500, 65_500, includePixels: false), CodecResultCode.ResourceLimitExceeded),
        };

        foreach (var item in cases)
        {
            var request = CodecHostTestClient.Request(
                CodecOperation.Inspect,
                CodecFormat.Psd,
                item.Input.Length);

            var result = await CodecHostGateTestClient.RunAsync(
                request,
                item.Input,
                TimeSpan.FromSeconds(4));

            AssertStructuredRefusal(item.Name, result, item.Expected);
        }
    }

    private static void AssertStructuredRefusal(
        string caseName,
        CodecHostGateResult result,
        CodecResultCode expected)
    {
        Assert.True(
            result.Process.ExitCode == 0,
            $"case={caseName}, exit={result.Process.ExitCode}, stderr={result.Process.StandardError}");
        Assert.Equal(string.Empty, result.Process.StandardError);
        var response = Assert.IsType<CodecResponse>(result.Response);
        Assert.True(
            response.Result == expected,
            $"case={caseName}, result={response.Result}, diagnostic={response.Diagnostic}");
        Assert.NotEqual(CodecResultCode.Success, response.Result);
        Assert.Equal(0L, response.PayloadLength);
        Assert.Empty(result.Payload);
    }
}
