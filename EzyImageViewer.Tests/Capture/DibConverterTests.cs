using System.Buffers.Binary;
using EzyImageViewer.Capture.Clipboard;
using EzyImageViewer.Core.Imaging;
using Xunit;

namespace EzyImageViewer.Tests.Capture;

public class DibConverterTests
{
    /// <summary>Minimal 2x2 32bpp bottom-up DIB (BITMAPINFOHEADER, BI_RGB).</summary>
    private static byte[] MakeDib32(ushort bitCount = 32, uint compression = 0, uint colorsUsed = 0, int paletteBytes = 0)
    {
        var pixels = 2 * 2 * 4;
        var dib = new byte[40 + paletteBytes + (compression == 3 ? 12 : 0) + pixels];
        BinaryPrimitives.WriteInt32LittleEndian(dib, 40);                 // biSize
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4), 2);        // biWidth
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8), 2);        // biHeight
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(12), 1);      // biPlanes
        BinaryPrimitives.WriteUInt16LittleEndian(dib.AsSpan(14), bitCount);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(16), compression);
        BinaryPrimitives.WriteUInt32LittleEndian(dib.AsSpan(32), colorsUsed);
        for (var i = dib.Length - pixels; i < dib.Length; i += 4)
        {
            dib[i] = 0x00; dib[i + 1] = 0x00; dib[i + 2] = 0xFF; dib[i + 3] = 0xFF; // red BGRA
        }
        return dib;
    }

    [Fact]
    public void DibToBmp_PrependsFileHeaderWithCorrectOffsets()
    {
        var dib = MakeDib32();
        var bmp = DibConverter.DibToBmp(dib);

        Assert.Equal((byte)'B', bmp[0]);
        Assert.Equal((byte)'M', bmp[1]);
        Assert.Equal(bmp.Length, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(2)));
        Assert.Equal(54, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(10))); // 14 + 40
        Assert.Equal(dib.Length + 14, bmp.Length);
    }

    [Fact]
    public void DibToBmp_BitfieldsCompression_AddsMaskBytesToOffset()
    {
        var dib = MakeDib32(compression: 3);
        var bmp = DibConverter.DibToBmp(dib);
        Assert.Equal(14 + 40 + 12, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(10)));
    }

    [Fact]
    public void DibToBmp_PalettedImage_AccountsForColorTable()
    {
        var dib = MakeDib32(bitCount: 8, colorsUsed: 16, paletteBytes: 64);
        var bmp = DibConverter.DibToBmp(dib);
        Assert.Equal(14 + 40 + 64, BinaryPrimitives.ReadInt32LittleEndian(bmp.AsSpan(10)));
    }

    [Fact]
    public void DibToBmp_TruncatedInput_IsRejected()
    {
        Assert.Throws<InvalidDataException>(() => DibConverter.DibToBmp(new byte[10]));
    }

    [Fact]
    public void DibToBmp_Output_SniffsAsBmp()
    {
        var bmp = DibConverter.DibToBmp(MakeDib32());
        var sniff = FormatSniffer.Sniff(bmp.AsSpan(0, Math.Min(bmp.Length, 32)));
        Assert.Equal(ImageFormat.Bmp, sniff.Format);
        Assert.Equal(SniffStatus.Supported, sniff.Status);
    }

    [Fact]
    public async Task DibToBmp_Output_DecodesThroughDocumentLoader()
    {
        var bmp = DibConverter.DibToBmp(MakeDib32());
        var loader = new EzyImageViewer.Imaging.DocumentLoader();

        using var document = await loader.LoadMemoryAsync(
            bmp, EzyImageViewer.Core.Documents.DocumentSource.FromClipboard(), CancellationToken.None);

        Assert.Equal(2, document.Frame.Width);
        Assert.Equal(2, document.Frame.Height);
        // Red pixel in BGRA
        Assert.Equal(0xFF, document.Frame.Pixels[2]);
    }
}
