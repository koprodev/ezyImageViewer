using System.Buffers.Binary;

namespace EzyImageViewer.Capture.Clipboard;

/// <summary>
/// Clipboard DIB/DIBv5 payloads lack the BITMAPFILEHEADER; prepending one yields a standard BMP
/// that the normal decoder dispatch can sniff and decode (no special clipboard pixel path).
/// </summary>
public static class DibConverter
{
    private const int FileHeaderSize = 14;
    private const int MinInfoHeaderSize = 40;
    private const uint BiBitfields = 3;

    public static byte[] DibToBmp(ReadOnlySpan<byte> dib)
    {
        if (dib.Length < MinInfoHeaderSize)
            throw new InvalidDataException("DIB payload smaller than BITMAPINFOHEADER.");

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (headerSize < MinInfoHeaderSize || headerSize > dib.Length)
            throw new InvalidDataException($"Invalid DIB header size {headerSize}.");

        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]);
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib[16..]);
        var colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(dib[32..]);

        long paletteEntries = colorsUsed != 0
            ? colorsUsed
            : bitCount <= 8 ? 1L << bitCount : 0;
        // 40-byte header + BI_BITFIELDS stores three RGB masks after the header (v5 embeds them).
        long maskBytes = compression == BiBitfields && headerSize == MinInfoHeaderSize ? 12 : 0;
        var pixelOffset = checked(FileHeaderSize + headerSize + maskBytes + paletteEntries * 4);
        if (pixelOffset > FileHeaderSize + (long)dib.Length)
            throw new InvalidDataException("DIB palette/mask layout exceeds the payload.");

        var bmp = new byte[checked(FileHeaderSize + dib.Length)];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(2), bmp.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bmp.AsSpan(10), (int)pixelOffset);
        dib.CopyTo(bmp.AsSpan(FileHeaderSize));
        return bmp;
    }
}
