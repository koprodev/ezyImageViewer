using System.Buffers.Binary;

namespace EzyImageViewer.Capture.Clipboard;

/// <summary>
/// 클립보드 DIB/DIBv5에는 BITMAPFILEHEADER가 없음.
/// 앞에 헤더를 붙여 표준 BMP로 만들면 별도 픽셀 경로 없이 일반 디코더가 판별·해석.
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
        // 40바이트 헤더 + BI_BITFIELDS는 헤더 뒤에 RGB 마스크 3개 저장. v5는 내부에 품음.
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
