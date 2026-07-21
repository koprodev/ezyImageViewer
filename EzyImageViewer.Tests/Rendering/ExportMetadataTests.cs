using EzyImageViewer.Core.Documents;
using EzyImageViewer.Core.Imaging;
using EzyImageViewer.Rendering;
using SkiaSharp;
using Xunit;

namespace EzyImageViewer.Tests.Rendering;

/// <summary>
/// FR-OUT-008 keep option (Q6 = b): the scrub rebuilds EXIF through a per-IFD allowlist — GPS,
/// MakerNote, serials, free-text/author tags and the thumbnail IFD never survive; structure tags
/// are normalized to the exported raster (Orientation=1, pixel dimensions = output). Aliased or
/// duplicate structures fail closed. Container embed/extract round-trips byte-exactly through
/// JPEG APP1, PNG eXIf (CRC-verified) and WebP VP8X+EXIF, and an independent reader agrees.
/// </summary>
public sealed class ExportMetadataTests
{
    private static readonly byte[] GpsLatitude =
    [
        37, 0, 0, 0, 1, 0, 0, 0, 33, 0, 0, 0, 1, 0, 0, 0, 55, 0, 0, 0, 1, 0, 0, 0,
    ];
    private static readonly byte[] MakerNote = "MAKER123"u8.ToArray();
    private static readonly byte[] CameraMake = "TestCamera\0"u8.ToArray();
    private static readonly byte[] Description = "Secret Address 42\0"u8.ToArray();
    private static readonly byte[] UserComment = "ASCII\0\0\0who took this"u8.ToArray();

    private sealed record Spec(ushort Tag, ushort Type, uint Count, byte[] Value);

    /// <summary>Little-endian TIFF writer for tests: IFD0 [+ Exif IFD, + GPS IFD] + data area.
    /// Sub-IFD pointer entries are appended automatically when the sub-IFD exists.</summary>
    private static byte[] BuildExif(
        IReadOnlyList<Spec> ifd0Entries,
        IReadOnlyList<Spec>? exifEntries = null,
        IReadOnlyList<Spec>? gpsEntries = null)
    {
        var ifd0 = new List<Spec>(ifd0Entries);
        if (exifEntries is not null)
            ifd0.Add(new Spec(0x8769, 4, 1, new byte[4]));
        if (gpsEntries is not null)
            ifd0.Add(new Spec(0x8825, 4, 1, new byte[4]));

        static int Block(IReadOnlyList<Spec> e) => 2 + e.Count * 12 + 4;
        const int ifd0At = 8;
        var exifAt = exifEntries is null ? 0 : ifd0At + Block(ifd0);
        var gpsAt = gpsEntries is null ? 0
            : (exifAt == 0 ? ifd0At + Block(ifd0) : exifAt + Block(exifEntries!));
        var dataAt = ifd0At + Block(ifd0)
            + (exifEntries is null ? 0 : Block(exifEntries))
            + (gpsEntries is null ? 0 : Block(gpsEntries));

        var total = dataAt;
        foreach (var list in new[] { ifd0, exifEntries, gpsEntries })
        {
            if (list is null)
                continue;
            foreach (var e in list)
            {
                if (e.Value.Length > 4)
                {
                    total = (total + 1) & ~1;
                    total += e.Value.Length;
                }
            }
        }

        var blob = new byte[total];
        void W16(int at, ushort v) { blob[at] = (byte)v; blob[at + 1] = (byte)(v >> 8); }
        void W32(int at, uint v)
        {
            blob[at] = (byte)v; blob[at + 1] = (byte)(v >> 8);
            blob[at + 2] = (byte)(v >> 16); blob[at + 3] = (byte)(v >> 24);
        }
        blob[0] = blob[1] = (byte)'I';
        W16(2, 42);
        W32(4, ifd0At);

        var data = dataAt;
        void WriteBlock(IReadOnlyList<Spec> entries, int at)
        {
            W16(at, (ushort)entries.Count);
            for (var i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                var slot = at + 2 + i * 12;
                W16(slot, e.Tag);
                W16(slot + 2, e.Type);
                W32(slot + 4, e.Count);
                if (e.Tag == 0x8769 && exifAt > 0)
                {
                    W32(slot + 8, (uint)exifAt);
                }
                else if (e.Tag == 0x8825 && gpsAt > 0)
                {
                    W32(slot + 8, (uint)gpsAt);
                }
                else if (e.Value.Length <= 4)
                {
                    e.Value.CopyTo(blob, slot + 8);
                }
                else
                {
                    data = (data + 1) & ~1;
                    W32(slot + 8, (uint)data);
                    e.Value.CopyTo(blob, data);
                    data += e.Value.Length;
                }
            }
            W32(at + 2 + entries.Count * 12, 0);
        }
        WriteBlock(ifd0, ifd0At);
        if (exifEntries is not null)
            WriteBlock(exifEntries, exifAt);
        if (gpsEntries is not null)
            WriteBlock(gpsEntries, gpsAt);
        return blob;
    }

    /// <summary>Realistic hostile-ish input: keepers + GPS + MakerNote + serial + free text +
    /// Orientation 6 + stale pixel dimensions.</summary>
    private static byte[] BuildFullExif() => BuildExif(
        ifd0Entries:
        [
            new Spec(0x010E, 2, (uint)Description.Length, Description),
            new Spec(0x010F, 2, (uint)CameraMake.Length, CameraMake),
            new Spec(0x0112, 3, 1, [6, 0]),
            new Spec(0x013B, 2, 3, "AB\0"u8.ToArray()),
            new Spec(0x9C9C, 1, 6, "secret"u8.ToArray()),
            new Spec(0xA431, 2, 4, "SN9\0"u8.ToArray()),
        ],
        exifEntries:
        [
            new Spec(0x8827, 3, 1, [144, 1]), // ISO 400
            new Spec(0x9286, 7, (uint)UserComment.Length, UserComment),
            new Spec(0x927C, 7, (uint)MakerNote.Length, MakerNote),
            new Spec(0xA002, 3, 1, [0x40, 6]), // stale 1600
            new Spec(0xA003, 3, 1, [0x84, 3]), // stale 900
        ],
        gpsEntries:
        [
            new Spec(0x0001, 2, 2, "N\0"u8.ToArray()),
            new Spec(0x0002, 5, 3, GpsLatitude),
        ]);

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle) =>
        haystack.IndexOf(needle) >= 0;

    private static SKImage SolidImage(int width, int height, bool withAlpha = false)
    {
        using var surface = SKSurface.Create(new SKImageInfo(
            width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(withAlpha ? new SKColor(0xC0, 0x30, 0x30, 0x80) : new SKColor(0xC0, 0x30, 0x30));
        return surface.Snapshot();
    }

    [Fact]
    public void Scrub_DropsSensitiveAndFreeTextTags_KeepsCaptureParameters()
    {
        var scrubbed = ExportMetadata.ScrubSensitive(BuildFullExif(), 16, 8);

        Assert.NotNull(scrubbed);
        Assert.True(Contains(scrubbed, CameraMake), "camera make must survive the scrub");
        Assert.True(Contains(scrubbed, new byte[] { 0x27, 0x88, 3, 0 }), "ISO entry must survive");
        // The allowlist rebuild never copies dropped data — nothing can hide as padding.
        Assert.False(Contains(scrubbed, GpsLatitude), "GPS coordinates leaked");
        Assert.False(Contains(scrubbed, MakerNote), "MakerNote leaked");
        Assert.False(Contains(scrubbed, "SN9"u8), "serial number leaked");
        Assert.False(Contains(scrubbed, "Secret Address"u8), "ImageDescription leaked");
        Assert.False(Contains(scrubbed, "who took this"u8), "UserComment leaked");
        Assert.False(Contains(scrubbed, "secret"u8), "XPComment leaked");
        Assert.False(Contains(scrubbed, "AB\0"u8), "Artist leaked");
        // Structure normalization: Orientation → 1, pixel dimensions → the exported raster.
        Assert.True(Contains(scrubbed, new byte[] { 0x12, 0x01, 3, 0, 1, 0, 0, 0, 1, 0 }),
            "orientation must be normalized to 1");
        Assert.True(Contains(scrubbed, new byte[] { 0x02, 0xA0, 4, 0, 1, 0, 0, 0, 16, 0, 0, 0 }),
            "PixelXDimension must be rewritten to the output width");
        Assert.True(Contains(scrubbed, new byte[] { 0x03, 0xA0, 4, 0, 1, 0, 0, 0, 8, 0, 0, 0 }),
            "PixelYDimension must be rewritten to the output height");
        // Still a valid TIFF for a second pass.
        Assert.NotNull(ExportMetadata.ScrubSensitive(scrubbed, 16, 8));
    }

    [Fact]
    public void Scrub_FailsClosed_OnAliasedDuplicateOrMalformedStructures()
    {
        // Alias: the kept Make points at the same bytes a dropped XPComment owns.
        var alias = BuildExif(
        [
            new Spec(0x010F, 2, (uint)CameraMake.Length, CameraMake),
            new Spec(0x9C9C, 1, 6, "secret"u8.ToArray()),
        ]);
        // Entry value-offset fields sit at +8 inside each 12-byte entry (IFD0 at 8, count at 8..10).
        var makeOffsetAt = 8 + 2 + 0 * 12 + 8;
        var xpOffsetAt = 8 + 2 + 1 * 12 + 8;
        Array.Copy(alias, xpOffsetAt, alias, makeOffsetAt, 4);
        Assert.Null(ExportMetadata.ScrubSensitive(alias));

        // Duplicate tags make keep/drop ambiguous.
        var duplicate = BuildExif(
        [
            new Spec(0x010F, 2, (uint)CameraMake.Length, CameraMake),
            new Spec(0x010F, 2, (uint)CameraMake.Length, CameraMake),
        ]);
        Assert.Null(ExportMetadata.ScrubSensitive(duplicate));

        // A sub-IFD pointer that is not LONG/count=1 is hostile shape.
        var badPointer = BuildExif(
        [
            new Spec(0x010F, 2, (uint)CameraMake.Length, CameraMake),
            new Spec(0x8769, 3, 1, [8, 0, 0, 0]),
        ]);
        Assert.Null(ExportMetadata.ScrubSensitive(badPointer));

        Assert.Null(ExportMetadata.ScrubSensitive(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }));
        Assert.Null(ExportMetadata.TryExtractExif(new byte[] { 0x42, 0x4D, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
    }

    [Fact]
    public void Jpeg_EmbedRoundTrips_AndRespectsTheApp1Boundary()
    {
        var scrubbed = ExportMetadata.ScrubSensitive(BuildFullExif(), 16, 8)!;
        using var image = SolidImage(8, 8);
        var plain = ImageExporter.Encode(image, ExportFormat.Jpeg);

        var embedded = ExportMetadata.Embed(plain, ExportFormat.Jpeg, scrubbed);
        Assert.Equal(scrubbed, ExportMetadata.TryExtractExif(embedded));
        using var decoded = SKBitmap.Decode(embedded);
        Assert.Equal(8, decoded.Width);

        // 65,527 bytes is the largest Exif payload one APP1 can carry; one more skips by contract.
        var atLimit = ExportMetadata.Embed(plain, ExportFormat.Jpeg, new byte[65_527]);
        Assert.Equal(65_527, ExportMetadata.TryExtractExif(atLimit)!.Length);
        var overLimit = ExportMetadata.Embed(plain, ExportFormat.Jpeg, new byte[65_528]);
        Assert.Same(plain, overLimit);
    }

    [Fact]
    public void Png_EmbedRoundTrips_WithAValidCrc_AndExtractRejectsACorruptOne()
    {
        var scrubbed = ExportMetadata.ScrubSensitive(BuildFullExif(), 16, 8)!;
        using var image = SolidImage(8, 8);
        var plain = ImageExporter.Encode(image, ExportFormat.Png);

        var embedded = ExportMetadata.Embed(plain, ExportFormat.Png, scrubbed);
        Assert.Equal(scrubbed, ExportMetadata.TryExtractExif(embedded));
        using var decoded = SKBitmap.Decode(embedded);
        Assert.Equal(8, decoded.Width);

        // The eXIf chunk sits right after IHDR; verify its CRC with an independent table-driven
        // CRC-32, then corrupt one payload byte — extraction must refuse it.
        const int chunkAt = 33;
        var span = embedded.AsSpan();
        Assert.True(span.Slice(chunkAt + 4, 4).SequenceEqual("eXIf"u8));
        var length = span[chunkAt] << 24 | span[chunkAt + 1] << 16 | span[chunkAt + 2] << 8 | span[chunkAt + 3];
        Assert.Equal(scrubbed.Length, length);
        var stored = (uint)(span[chunkAt + 8 + length] << 24 | span[chunkAt + 9 + length] << 16
            | span[chunkAt + 10 + length] << 8 | span[chunkAt + 11 + length]);
        Assert.Equal(TableCrc32(span.Slice(chunkAt + 4, 4 + length)), stored);

        var corrupt = (byte[])embedded.Clone();
        corrupt[chunkAt + 8] ^= 0xFF;
        Assert.Null(ExportMetadata.TryExtractExif(corrupt));
    }

    [Fact]
    public void WebP_Lossy_GainsVp8xWithExifFlag_AndTrailingDataOutsideRiffIsNotMetadata()
    {
        var scrubbed = ExportMetadata.ScrubSensitive(BuildFullExif(), 16, 8)!;
        using var image = SolidImage(16, 16);
        var plain = ImageExporter.Encode(image, ExportFormat.WebP, new ExportOptions { Quality = 80 });

        var embedded = ExportMetadata.Embed(plain, ExportFormat.WebP, scrubbed);
        Assert.True(embedded.AsSpan(12, 4).SequenceEqual("VP8X"u8));
        Assert.Equal(0x08, embedded[20] & 0x08);
        Assert.Equal(scrubbed, ExportMetadata.TryExtractExif(embedded));
        using var decoded = SKBitmap.Decode(embedded);
        Assert.Equal(16, decoded.Width);
        Assert.Equal(16, decoded.Height);

        // An EXIF chunk appended after the declared RIFF size is trailing garbage, not metadata.
        var trailing = new byte[plain.Length + 8 + scrubbed.Length];
        plain.CopyTo(trailing, 0);
        "EXIF"u8.CopyTo(trailing.AsSpan(plain.Length));
        trailing[plain.Length + 4] = (byte)scrubbed.Length;
        trailing[plain.Length + 5] = (byte)(scrubbed.Length >> 8);
        scrubbed.CopyTo(trailing.AsSpan(plain.Length + 8));
        Assert.Null(ExportMetadata.TryExtractExif(trailing));
    }

    [Fact]
    public void WebP_WithAlpha_EmbedsThroughWhateverSkiaEmits_LossyAndLossless()
    {
        var scrubbed = ExportMetadata.ScrubSensitive(BuildFullExif(), 16, 8)!;
        foreach (var options in new[]
        {
            new ExportOptions { Quality = 80 },
            new ExportOptions { WebPLossless = true },
        })
        {
            using var image = SolidImage(16, 16, withAlpha: true);
            var plain = ImageExporter.Encode(image, ExportFormat.WebP, options);

            var embedded = ExportMetadata.Embed(plain, ExportFormat.WebP, scrubbed);

            Assert.True(embedded.AsSpan(12, 4).SequenceEqual("VP8X"u8));
            Assert.Equal(0x08, embedded[20] & 0x08);
            Assert.Equal(scrubbed, ExportMetadata.TryExtractExif(embedded));
            using var decoded = SKBitmap.Decode(embedded);
            Assert.Equal(16, decoded.Width);
        }
    }

    [Fact]
    public void BigEndianExif_NormalizesOrientation_InTheSameByteOrder()
    {
        // Minimal MM blob: IFD0 with one inline SHORT (Orientation = 6).
        var blob = new byte[] {
            (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8,
            0, 1,
            0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, 6, 0, 0,
            0, 0, 0, 0,
        };

        var scrubbed = ExportMetadata.ScrubSensitive(blob);

        Assert.NotNull(scrubbed);
        Assert.Equal((byte)'M', scrubbed[0]);
        Assert.True(Contains(scrubbed, new byte[] { 0x01, 0x12, 0, 3, 0, 0, 0, 1, 0, 1 }),
            "orientation must survive normalized to 1 in big-endian layout");
    }

    [Fact]
    public void IndependentReader_SeesNormalizedOrientation_AndNoGps()
    {
        var scrubbed = ExportMetadata.ScrubSensitive(BuildFullExif(), 16, 8)!;
        using var image = SolidImage(16, 8);
        var embedded = ExportMetadata.Embed(
            ImageExporter.Encode(image, ExportFormat.Jpeg), ExportFormat.Jpeg, scrubbed);

        using var magick = new ImageMagick.MagickImage(embedded);
        var profile = magick.GetExifProfile();

        Assert.NotNull(profile);
        Assert.Equal("TestCamera", profile.GetValue(ImageMagick.ExifTag.Make)?.Value);
        Assert.Equal((ushort)1, profile.GetValue(ImageMagick.ExifTag.Orientation)?.Value);
        Assert.DoesNotContain(profile.Values, v =>
            v.Tag.ToString()!.StartsWith("GPS", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProductLoader_ReopensAllThreeExports_WithoutASecondRotation()
    {
        // A 16x8 JPEG carrying Orientation=6 opens as 8x16 (the loader applies EXIF once).
        var orientationSix = BuildExif([new Spec(0x0112, 3, 1, [6, 0])]);
        using var landscape = SolidImage(16, 8);
        var sourceJpeg = ExportMetadata.Embed(
            ImageExporter.Encode(landscape, ExportFormat.Jpeg), ExportFormat.Jpeg, orientationSix);
        var loader = new EzyImageViewer.Imaging.DocumentLoader();
        using (var opened = await loader.LoadMemoryAsync(
            sourceJpeg, DocumentSource.FromClipboard(), CancellationToken.None))
        {
            Assert.Equal(new PixelSize(8, 16), opened.NativeSize);
        }

        // The export writes the upright 8x16 raster; the kept metadata must say Orientation=1,
        // so reopening any of the three formats must NOT rotate again ([18차] 필수 1 계약).
        var scrubbed = ExportMetadata.ScrubSensitive(orientationSix, 8, 16)!;
        using var upright = SolidImage(8, 16);
        foreach (var format in new[] { ExportFormat.Jpeg, ExportFormat.Png, ExportFormat.WebP })
        {
            var export = ExportMetadata.Embed(
                ImageExporter.Encode(upright, format), format, scrubbed);
            using var reopened = await loader.LoadMemoryAsync(
                export, DocumentSource.FromClipboard(), CancellationToken.None);
            Assert.Equal(new PixelSize(8, 16), reopened.NativeSize);
        }
    }

    private static uint TableCrc32(ReadOnlySpan<byte> data)
    {
        Span<uint> table = stackalloc uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
                c = (c & 1) != 0 ? 0xEDB8_8320 ^ c >> 1 : c >> 1;
            table[(int)n] = c;
        }
        var crc = 0xFFFF_FFFFu;
        foreach (var b in data)
            crc = table[(int)((crc ^ b) & 0xFF)] ^ crc >> 8;
        return ~crc;
    }
}
