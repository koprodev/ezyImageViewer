using System.Text;
using EzyImageViewer.Core.Imaging;
using Xunit;

namespace EzyImageViewer.Tests.Imaging;

public class FormatSnifferTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 }, ImageFormat.Png)]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0, 0, 0, 0, 0 }, ImageFormat.Jpeg)]
    [InlineData(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a', 0, 0, 0, 0, 0, 0 }, ImageFormat.Gif)]
    [InlineData(new byte[] { (byte)'B', (byte)'M', 0x46, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, ImageFormat.Bmp)]
    [InlineData(new byte[] { 0x49, 0x49, 0x2A, 0x00, 0, 0, 0, 0, 0, 0, 0, 0 }, ImageFormat.Tiff)]
    [InlineData(new byte[] { 0x4D, 0x4D, 0x00, 0x2A, 0, 0, 0, 0, 0, 0, 0, 0 }, ImageFormat.Tiff)]
    [InlineData(new byte[] { 0x00, 0x00, 0x01, 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0 }, ImageFormat.Ico)]
    public void Sniff_DetectsSupportedRasterSignatures(byte[] header, ImageFormat expected)
    {
        var result = FormatSniffer.Sniff(header);
        Assert.Equal(expected, result.Format);
        Assert.Equal(SniffStatus.Supported, result.Status);
    }

    [Fact]
    public void Sniff_DetectsRiffWebP()
    {
        var header = "RIFF"u8.ToArray().Concat(new byte[] { 1, 2, 3, 4 }).Concat("WEBP"u8.ToArray()).ToArray();
        var result = FormatSniffer.Sniff(header);
        Assert.Equal(ImageFormat.WebP, result.Format);
        Assert.Equal(SniffStatus.Supported, result.Status);
    }

    [Theory]
    [InlineData("avif", ImageFormat.Avif)]
    [InlineData("avis", ImageFormat.Avif)]
    [InlineData("heic", ImageFormat.Heif)]
    [InlineData("mif1", ImageFormat.Heif)]
    public void Sniff_DetectsConditionalIsoBaseMediaFormats(string brand, ImageFormat expected)
    {
        var header = new byte[24];
        header[3] = 24;
        "ftyp"u8.CopyTo(header.AsSpan(4));
        Encoding.ASCII.GetBytes(brand).CopyTo(header, 8);

        var result = FormatSniffer.Sniff(header);

        Assert.Equal(expected, result.Format);
        Assert.Equal(SniffStatus.Conditional, result.Status);
    }

    [Fact]
    public void Sniff_DoesNotTreatGenericIsoBaseMediaAsAnImage()
    {
        var header = new byte[24];
        header[3] = 24;
        "ftyp"u8.CopyTo(header.AsSpan(4));
        "mp42"u8.CopyTo(header.AsSpan(8));

        Assert.Equal(ImageFormat.Unknown, FormatSniffer.Sniff(header).Format);
    }

    [Theory]
    [InlineData("%PDF-1.7 something", ImageFormat.Pdf)]
    [InlineData("8BPSxxxxxxxxxxxx", ImageFormat.Psd)]
    public void Sniff_KnownButUnsupportedFormats(string text, ImageFormat expected)
    {
        var result = FormatSniffer.Sniff(Encoding.UTF8.GetBytes(text));
        Assert.Equal(expected, result.Format);
        Assert.Equal(SniffStatus.KnownButUnsupported, result.Status);
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\">")]
    [InlineData("<?xml version=\"1.0\"?><svg>")]
    public void Sniff_SvgIsProductSupported(string text)
    {
        var result = FormatSniffer.Sniff(Encoding.UTF8.GetBytes(text));
        Assert.Equal(ImageFormat.Svg, result.Format);
        Assert.Equal(SniffStatus.Supported, result.Status);
    }

    [Fact]
    public void Sniff_TruncatedInput_IsCorruptOrTruncated()
    {
        Assert.Equal(SniffStatus.CorruptOrTruncated, FormatSniffer.Sniff([0xFF]).Status);
        Assert.Equal(SniffStatus.CorruptOrTruncated, FormatSniffer.Sniff(ReadOnlySpan<byte>.Empty).Status);
    }

    [Fact]
    public void Sniff_UnknownBinary_IsUnknown()
    {
        var header = Enumerable.Repeat((byte)0xAB, 32).ToArray();
        Assert.Equal(SniffStatus.Unknown, FormatSniffer.Sniff(header).Status);
    }

    [Theory]
    [InlineData(ImageFormat.Png, ".png", true)]
    [InlineData(ImageFormat.Png, ".jpg", false)]
    [InlineData(ImageFormat.Jpeg, ".jfif", true)]
    [InlineData(ImageFormat.Bmp, ".dib", true)]
    [InlineData(ImageFormat.WebP, ".png", false)]
    [InlineData(ImageFormat.Avif, ".avif", true)]
    [InlineData(ImageFormat.Heif, ".hif", true)]
    [InlineData(ImageFormat.Unknown, ".xyz", true)]
    public void ExtensionMatches_ChecksConsistency(ImageFormat format, string extension, bool expected)
        => Assert.Equal(expected, FormatSniffer.ExtensionMatches(format, extension));
}
