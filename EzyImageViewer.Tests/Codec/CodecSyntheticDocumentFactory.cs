using System.Buffers.Binary;
using System.Text;

namespace EzyImageViewer.Tests.Codec;

internal static class CodecSyntheticDocumentFactory
{
    public static byte[] BuildPdf(
        int pageCount,
        int width = 612,
        int height = 792,
        bool fillPage = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageCount, 10_001);
        return BuildPdf(
            Enumerable.Repeat((Width: width, Height: height), pageCount).ToArray(),
            fillPage);
    }

    public static byte[] BuildPdf(
        IReadOnlyList<(int Width, int Height)> pageSizes,
        bool fillPage = true)
    {
        ArgumentNullException.ThrowIfNull(pageSizes);
        ArgumentOutOfRangeException.ThrowIfZero(pageSizes.Count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pageSizes.Count, 10_001);
        foreach (var (width, height) in pageSizes)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 70_000);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(height, 70_000);
        }

        var contentId = checked(pageSizes.Count + 3);
        var content = fillPage ? "0.15 0.45 0.75 rg 0 0 612 792 re f\n" : string.Empty;
        var objects = new List<string>(checked(pageSizes.Count + 3))
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pageSizes.Count).Select(index => $"{index + 3} 0 R"))}] /Count {pageSizes.Count} >>",
        };
        foreach (var (width, height) in pageSizes)
        {
            objects.Add(
                $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Contents {contentId} 0 R >>");
        }
        objects.Add($"<< /Length {content.Length} >>\nstream\n{content}endstream");

        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true)
        {
            NewLine = "\n",
        };
        var offsets = new List<long>(objects.Count);
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

    public static byte[] BuildCorruptXrefPdf() => Encoding.ASCII.GetBytes(
        "%PDF-1.7\n" +
        "xref\n0 1\n0000000000 65535 f \n" +
        "trailer\n<< /Size 1 /Root 99 0 R >>\n" +
        "startxref\n9999999999\n%%EOF");

    public static byte[] BuildRgbPsd(
        int width,
        int height,
        bool includePixels = true,
        ushort compression = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, 65_501);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, 65_501);
        var pixelCount = checked((long)width * height);
        if (includePixels && pixelCount > 4_000_000)
            throw new ArgumentOutOfRangeException(nameof(width), "Synthetic PSD payload is too large.");
        var pixels = includePixels ? checked((int)pixelCount) : 0;
        var bytes = new byte[checked(40 + pixels * 3)];
        "8BPS"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(12), 3);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(14), (uint)height);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(18), (uint)width);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(22), 8);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(24), 3);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(38), compression);
        if (includePixels)
            bytes.AsSpan(40, pixels).Fill(255);
        return bytes;
    }
}
