namespace EzyImageViewer.Rendering;

/// <summary>
/// FR-OUT-008 keep-option (Q6 = b): carries source EXIF onto an export minus everything
/// privacy-relevant. The scrub REBUILDS the TIFF from a per-IFD ALLOWLIST walk (capture
/// parameters, dates, camera/lens model, color/resolution structure) — free-text, author,
/// GPS, MakerNote, serials and the IFD1 thumbnail (pre-edit pixels!) are never copied.
/// Structure tags are normalized to the exported raster: Orientation becomes 1 (loaders
/// already applied it to the pixels) and PixelX/YDimension are rewritten to the output size.
/// Fail-closed: aliased/overlapping value ranges, duplicate tags, malformed sub-IFD pointers
/// or a blown budget reject the whole blob (null) — the export then proceeds bare, because
/// metadata is auxiliary and must never break a save.
/// </summary>
public static class ExportMetadata
{
    private const int MaxExifBytes = 4 * 1024 * 1024;
    private const int MaxIfdEntries = 256;
    private const int MaxValueBytes = 64 * 1024;

    private const ushort OrientationTag = 0x0112;
    private const ushort PixelXDimensionTag = 0xA002;
    private const ushort PixelYDimensionTag = 0xA003;
    private const ushort ExifIfdPointerTag = 0x8769;
    private const ushort GpsIfdPointerTag = 0x8825;
    private const ushort InteropIfdPointerTag = 0xA005;
    private const ushort ThumbnailOffsetTag = 0x0201;
    private const ushort ThumbnailLengthTag = 0x0202;

    // Allowed-type bitmasks (1 << TIFF type id).
    private const ushort A = 1 << 2;   // ASCII
    private const ushort S = 1 << 3;   // SHORT
    private const ushort L = 1 << 4;   // LONG
    private const ushort R = 1 << 5;   // RATIONAL
    private const ushort U = 1 << 7;   // UNDEFINED
    private const ushort SR = 1 << 10; // SRATIONAL

    /// <summary>IFD0: camera identity and raster structure only — no author/description text.</summary>
    private static readonly Dictionary<ushort, ushort> ZerothAllowed = new()
    {
        [0x010F] = A, // Make
        [0x0110] = A, // Model
        [OrientationTag] = S, // normalized to 1
        [0x011A] = R, // XResolution
        [0x011B] = R, // YResolution
        [0x0128] = S, // ResolutionUnit
        [0x0131] = A, // Software
        [0x0132] = A, // DateTime
        [0x0213] = S, // YCbCrPositioning
    };

    /// <summary>Exif IFD: capture parameters and timestamps — no comments, no unique ids.</summary>
    private static readonly Dictionary<ushort, ushort> ExifAllowed = new()
    {
        [0x829A] = R,  // ExposureTime
        [0x829D] = R,  // FNumber
        [0x8822] = S,  // ExposureProgram
        [0x8827] = S,  // PhotographicSensitivity (ISO)
        [0x8830] = S,  // SensitivityType
        [0x9000] = U,  // ExifVersion
        [0x9003] = A,  // DateTimeOriginal
        [0x9004] = A,  // DateTimeDigitized
        [0x9010] = A,  // OffsetTime
        [0x9011] = A,  // OffsetTimeOriginal
        [0x9012] = A,  // OffsetTimeDigitized
        [0x9101] = U,  // ComponentsConfiguration
        [0x9102] = R,  // CompressedBitsPerPixel
        [0x9201] = SR, // ShutterSpeedValue
        [0x9202] = R,  // ApertureValue
        [0x9203] = SR, // BrightnessValue
        [0x9204] = SR, // ExposureBiasValue
        [0x9205] = R,  // MaxApertureValue
        [0x9206] = R,  // SubjectDistance
        [0x9207] = S,  // MeteringMode
        [0x9208] = S,  // LightSource
        [0x9209] = S,  // Flash
        [0x920A] = R,  // FocalLength
        [0x9214] = S,  // SubjectArea
        [0x9290] = A,  // SubSecTime
        [0x9291] = A,  // SubSecTimeOriginal
        [0x9292] = A,  // SubSecTimeDigitized
        [0xA000] = U,  // FlashpixVersion
        [0xA001] = S,  // ColorSpace
        [PixelXDimensionTag] = (ushort)(S | L), // rewritten to output width
        [PixelYDimensionTag] = (ushort)(S | L), // rewritten to output height
        [0xA20E] = R,  // FocalPlaneXResolution
        [0xA20F] = R,  // FocalPlaneYResolution
        [0xA210] = S,  // FocalPlaneResolutionUnit
        [0xA217] = S,  // SensingMethod
        [0xA300] = U,  // FileSource
        [0xA301] = U,  // SceneType
        [0xA401] = S,  // CustomRendered
        [0xA402] = S,  // ExposureMode
        [0xA403] = S,  // WhiteBalance
        [0xA404] = R,  // DigitalZoomRatio
        [0xA405] = S,  // FocalLengthIn35mmFilm
        [0xA406] = S,  // SceneCaptureType
        [0xA407] = S,  // GainControl
        [0xA408] = S,  // Contrast
        [0xA409] = S,  // Saturation
        [0xA40A] = S,  // Sharpness
        [0xA40C] = S,  // SubjectDistanceRange
        [0xA432] = R,  // LensSpecification
        [0xA433] = A,  // LensMake
        [0xA434] = A,  // LensModel
    };

    private static readonly Dictionary<ushort, ushort> InteropAllowed = new()
    {
        [0x0001] = A, // InteroperabilityIndex
        [0x0002] = U, // InteroperabilityVersion
    };

    // TIFF value type sizes, index = type id 1..12; 0 marks an unknown type (entry dropped).
    private static readonly byte[] TypeSize = [0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8];

    /// <summary>Pulls the raw EXIF TIFF blob out of a JPEG/PNG/WebP container, null otherwise.
    /// Input hardening: a PNG eXIf chunk must pass its CRC; a WebP scan stays inside the
    /// declared RIFF size — trailing data is not metadata.</summary>
    public static byte[]? TryExtractExif(ReadOnlySpan<byte> container)
    {
        if (container.Length >= 2 && container[0] == 0xFF && container[1] == 0xD8)
            return ExtractFromJpeg(container);
        if (container.Length >= 8 && container.StartsWith(PngSignature))
            return ExtractFromPng(container);
        if (container.Length >= 12 && container.StartsWith("RIFF"u8)
            && container.Slice(8, 4).SequenceEqual("WEBP"u8))
            return ExtractFromWebP(container);
        return null;
    }

    /// <summary>Rebuilds the blob through the allowlist; null when nothing valid remains or the
    /// structure fails closed. Pass the exported raster size so the dimension tags describe the
    /// actual output; 0 drops them instead.</summary>
    public static byte[]? ScrubSensitive(ReadOnlySpan<byte> exif, int outputWidth = 0, int outputHeight = 0)
    {
        try
        {
            return ScrubCore(exif, outputWidth, outputHeight);
        }
        catch (Exception ex) when (ex is ScrubRejected or OverflowException)
        {
            return null;
        }
    }

    private sealed class ScrubRejected : Exception;

    private static void Reject() => throw new ScrubRejected();

    private sealed class ScrubState
    {
        public bool Little;
        /// <summary>IFD blocks — pairwise overlap means a circular/self-referencing structure.</summary>
        public readonly List<(long Start, long End)> Blocks = [];
        /// <summary>Dropped/sensitive value bytes: kept values may never read from here.</summary>
        public readonly List<(long Start, long End)> Reserved = [];
        /// <summary>External ranges the kept values copy from — must be disjoint from everything.</summary>
        public readonly List<(long Start, long End)> Kept = [];
    }

    private static byte[]? ScrubCore(ReadOnlySpan<byte> exif, int outputWidth, int outputHeight)
    {
        if (exif.Length < 8 || exif.Length > MaxExifBytes)
            return null;
        var little = exif[0] == (byte)'I' && exif[1] == (byte)'I';
        if (!little && !(exif[0] == (byte)'M' && exif[1] == (byte)'M'))
            return null;
        if (U16(exif, 2, little) != 42)
            return null;

        var state = new ScrubState { Little = little };
        var ifd0 = WalkIfd(exif, U32(exif, 4, little), IfdKind.Zeroth, state, outputWidth, outputHeight,
            out var exifPtr, out var gpsPtr, out _, out var ifd1);
        List<Entry>? exifIfd = null, interopIfd = null;
        if (exifPtr > 0)
        {
            exifIfd = WalkIfd(exif, exifPtr, IfdKind.Exif, state, outputWidth, outputHeight,
                out _, out _, out var interopPtr, out _);
            if (interopPtr > 0)
                interopIfd = WalkIfd(exif, interopPtr, IfdKind.Interop, state, outputWidth, outputHeight,
                    out _, out _, out _, out _);
        }
        // GPS and the thumbnail chain are walked for their ranges only: every byte they own is
        // poisoned ground no kept value may alias into.
        if (gpsPtr > 0)
            WalkIfd(exif, gpsPtr, IfdKind.RangeOnly, state, 0, 0, out _, out _, out _, out _);
        var next = ifd1;
        for (var hop = 0; next > 0; hop++)
        {
            if (hop >= 4)
                Reject();
            WalkIfd(exif, next, IfdKind.RangeOnly, state, 0, 0, out _, out _, out _, out next);
        }

        ValidateRanges(state);
        if (ifd0.Count == 0 && (exifIfd?.Count ?? 0) == 0)
            return null;
        var blob = Rebuild(ifd0, exifIfd, interopIfd, little);
        return blob.Length <= MaxExifBytes ? blob : null;
    }

    private enum IfdKind
    {
        Zeroth,
        Exif,
        Interop,
        /// <summary>GPS / thumbnail IFDs: nothing kept, all owned bytes reserved.</summary>
        RangeOnly,
    }

    private readonly record struct Entry(ushort Tag, ushort Type, uint Count, byte[] Value);

    private static List<Entry> WalkIfd(
        ReadOnlySpan<byte> exif, long offset, IfdKind kind, ScrubState state,
        int outputWidth, int outputHeight,
        out long exifPtr, out long gpsPtr, out long interopPtr, out long nextIfd)
    {
        exifPtr = 0;
        gpsPtr = 0;
        interopPtr = 0;
        nextIfd = 0;
        if (offset < 8 || offset + 2 > exif.Length)
            Reject();
        int count = U16(exif, (int)offset, state.Little);
        var blockEnd = offset + 2 + count * 12L + 4;
        if (count > MaxIfdEntries || blockEnd > exif.Length)
            Reject();
        state.Blocks.Add((offset, blockEnd));

        var allowed = kind switch
        {
            IfdKind.Zeroth => ZerothAllowed,
            IfdKind.Exif => ExifAllowed,
            IfdKind.Interop => InteropAllowed,
            _ => null,
        };
        var entries = new List<Entry>(allowed is null ? 0 : count);
        var seen = new HashSet<ushort>();
        long thumbOffset = 0, thumbLength = 0;
        for (var i = 0; i < count; i++)
        {
            var at = (int)(offset + 2 + i * 12);
            var tag = U16(exif, at, state.Little);
            var type = U16(exif, at + 2, state.Little);
            var valueCount = U32(exif, at + 4, state.Little);
            if (!seen.Add(tag))
                Reject(); // duplicate tags make the keep/drop decision ambiguous
            if (type == 0 || type >= TypeSize.Length)
                continue; // unsizable: cannot locate its bytes, nothing to keep or reserve
            var size = checked((long)TypeSize[type] * valueCount);
            var external = size > 4;
            long pointer = external ? U32(exif, at + 8, state.Little) : 0;
            if (external && (pointer < 8 || pointer + size > exif.Length))
                Reject();

            // Sub-IFD pointers demand LONG/count=1 discipline — anything else is hostile shape.
            if (kind == IfdKind.Zeroth && tag is ExifIfdPointerTag or GpsIfdPointerTag)
            {
                if (type != 4 || valueCount != 1)
                    Reject();
                if (tag == ExifIfdPointerTag)
                    exifPtr = U32(exif, at + 8, state.Little);
                else
                    gpsPtr = U32(exif, at + 8, state.Little);
                continue;
            }
            if (kind == IfdKind.Exif && tag == InteropIfdPointerTag)
            {
                if (type != 4 || valueCount != 1)
                    Reject();
                interopPtr = U32(exif, at + 8, state.Little);
                continue;
            }

            if (kind == IfdKind.RangeOnly)
            {
                if (external)
                    state.Reserved.Add((pointer, pointer + size));
                if (tag == ThumbnailOffsetTag && type == 4)
                    thumbOffset = U32(exif, at + 8, state.Little);
                if (tag == ThumbnailLengthTag && type is 3 or 4)
                    thumbLength = U32(exif, at + 8, state.Little);
                continue;
            }

            var keep = allowed!.TryGetValue(tag, out var allowedTypes)
                && (allowedTypes & 1 << type) != 0
                && size <= MaxValueBytes;

            // Structure normalization: the export raster is already upright and resized.
            if (keep && tag == OrientationTag)
            {
                entries.Add(new Entry(OrientationTag, 3, 1,
                    state.Little ? [1, 0] : [0, 1]));
                if (external)
                    state.Reserved.Add((pointer, pointer + size));
                continue;
            }
            if (keep && tag is PixelXDimensionTag or PixelYDimensionTag)
            {
                if (external)
                    state.Reserved.Add((pointer, pointer + size));
                if (outputWidth > 0 && outputHeight > 0)
                {
                    var dimension = (uint)(tag == PixelXDimensionTag ? outputWidth : outputHeight);
                    var value = new byte[4];
                    W32(value, 0, dimension, state.Little);
                    entries.Add(new Entry(tag, 4, 1, value));
                }
                continue;
            }

            if (!keep)
            {
                if (external)
                    state.Reserved.Add((pointer, pointer + size));
                continue;
            }
            byte[] valueBytes;
            if (external)
            {
                state.Kept.Add((pointer, pointer + size));
                valueBytes = exif.Slice((int)pointer, (int)size).ToArray();
            }
            else
            {
                valueBytes = exif.Slice(at + 8, (int)size).ToArray();
            }
            entries.Add(new Entry(tag, type, valueCount, valueBytes));
        }

        var nextAt = (int)(offset + 2 + count * 12);
        nextIfd = kind is IfdKind.Zeroth or IfdKind.RangeOnly ? U32(exif, nextAt, state.Little) : 0;
        if (thumbOffset > 0 && thumbLength > 0)
            state.Reserved.Add((thumbOffset, checked(thumbOffset + thumbLength)));
        return entries;
    }

    /// <summary>Kept ranges must be disjoint from each other, from every IFD block and from every
    /// reserved (dropped/sensitive) range; IFD blocks must not overlap each other. Any aliasing
    /// rejects the whole blob — that is what makes "dropped data cannot survive" actually true.</summary>
    private static void ValidateRanges(ScrubState state)
    {
        for (var i = 0; i < state.Blocks.Count; i++)
            for (var j = i + 1; j < state.Blocks.Count; j++)
                if (Overlaps(state.Blocks[i], state.Blocks[j]))
                    Reject();
        for (var i = 0; i < state.Kept.Count; i++)
        {
            for (var j = i + 1; j < state.Kept.Count; j++)
                if (Overlaps(state.Kept[i], state.Kept[j]))
                    Reject();
            foreach (var block in state.Blocks)
                if (Overlaps(state.Kept[i], block))
                    Reject();
            foreach (var reserved in state.Reserved)
                if (Overlaps(state.Kept[i], reserved))
                    Reject();
        }
    }

    private static bool Overlaps((long Start, long End) a, (long Start, long End) b) =>
        a.Start < b.End && b.Start < a.End;

    private static byte[] Rebuild(List<Entry> ifd0, List<Entry>? exifIfd, List<Entry>? interopIfd, bool little)
    {
        // Synthesized sub-IFD pointers keep the chain intact; empty sub-IFDs disappear entirely.
        var interop = interopIfd is { Count: > 0 } ? interopIfd : null;
        var exif = exifIfd is { Count: > 0 } || interop is not null ? exifIfd ?? [] : null;
        if (interop is not null)
            exif!.Add(new Entry(InteropIfdPointerTag, 4, 1, new byte[4]));
        if (exif is not null)
            ifd0.Add(new Entry(ExifIfdPointerTag, 4, 1, new byte[4]));
        ifd0.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        exif?.Sort((a, b) => a.Tag.CompareTo(b.Tag));
        interop?.Sort((a, b) => a.Tag.CompareTo(b.Tag));

        static int BlockSize(List<Entry> ifd) => 2 + ifd.Count * 12 + 4;
        var ifd0Offset = 8;
        var exifOffset = ifd0Offset + BlockSize(ifd0);
        var interopOffset = exif is null ? 0 : exifOffset + BlockSize(exif);
        var dataOffset = exif is null ? exifOffset
            : interop is null ? interopOffset : interopOffset + BlockSize(interop);

        // First pass sizes the external value area (even-aligned per TIFF).
        var total = dataOffset;
        foreach (var ifd in new[] { ifd0, exif, interop })
        {
            if (ifd is null)
                continue;
            foreach (var entry in ifd)
            {
                if (entry.Value.Length <= 4)
                    continue;
                total = (total + 1) & ~1;
                total += entry.Value.Length;
            }
        }

        var blob = new byte[total];
        blob[0] = blob[1] = little ? (byte)'I' : (byte)'M';
        W16(blob, 2, 42, little);
        W32(blob, 4, (uint)ifd0Offset, little);

        var data = dataOffset;
        WriteIfd(blob, ifd0Offset, ifd0, little, ref data, subIfdPointerValue: (uint)exifOffset);
        if (exif is not null)
            WriteIfd(blob, exifOffset, exif, little, ref data, subIfdPointerValue: (uint)interopOffset);
        if (interop is not null)
            WriteIfd(blob, interopOffset, interop, little, ref data, subIfdPointerValue: 0);
        return blob;
    }

    private static void WriteIfd(
        byte[] blob, int offset, List<Entry> ifd, bool little, ref int data, uint subIfdPointerValue)
    {
        W16(blob, offset, (ushort)ifd.Count, little);
        for (var i = 0; i < ifd.Count; i++)
        {
            var entry = ifd[i];
            var at = offset + 2 + i * 12;
            W16(blob, at, entry.Tag, little);
            W16(blob, at + 2, entry.Type, little);
            W32(blob, at + 4, entry.Count, little);
            if (entry.Tag is ExifIfdPointerTag or InteropIfdPointerTag)
            {
                W32(blob, at + 8, subIfdPointerValue, little);
            }
            else if (entry.Value.Length <= 4)
            {
                entry.Value.CopyTo(blob, at + 8); // remaining bytes stay zero
            }
            else
            {
                data = (data + 1) & ~1;
                W32(blob, at + 8, (uint)data, little);
                entry.Value.CopyTo(blob, data);
                data += entry.Value.Length;
            }
        }
        W32(blob, offset + 2 + ifd.Count * 12, 0, little); // no next IFD: the thumbnail never rides
    }

    private static ushort U16(ReadOnlySpan<byte> s, int at, bool little) =>
        little ? (ushort)(s[at] | s[at + 1] << 8) : (ushort)(s[at] << 8 | s[at + 1]);

    private static uint U32(ReadOnlySpan<byte> s, int at, bool little) =>
        little
            ? s[at] | (uint)s[at + 1] << 8 | (uint)s[at + 2] << 16 | (uint)s[at + 3] << 24
            : (uint)s[at] << 24 | (uint)s[at + 1] << 16 | (uint)s[at + 2] << 8 | s[at + 3];

    private static void W16(byte[] s, int at, ushort value, bool little)
    {
        if (little) { s[at] = (byte)value; s[at + 1] = (byte)(value >> 8); }
        else { s[at] = (byte)(value >> 8); s[at + 1] = (byte)value; }
    }

    private static void W32(byte[] s, int at, uint value, bool little)
    {
        if (little)
        {
            s[at] = (byte)value; s[at + 1] = (byte)(value >> 8);
            s[at + 2] = (byte)(value >> 16); s[at + 3] = (byte)(value >> 24);
        }
        else
        {
            s[at] = (byte)(value >> 24); s[at + 1] = (byte)(value >> 16);
            s[at + 2] = (byte)(value >> 8); s[at + 3] = (byte)value;
        }
    }

    /// <summary>Inserts the (scrubbed) blob into an encoder output. The containers are our own
    /// encoder's, so a structural surprise throws; an APP1 overflow skips instead — the export
    /// must not fail over auxiliary metadata. Compare the result by reference to know.</summary>
    public static byte[] Embed(byte[] encoded, ExportFormat format, ReadOnlySpan<byte> exif)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        return format switch
        {
            ExportFormat.Jpeg => EmbedInJpeg(encoded, exif),
            ExportFormat.Png => EmbedInPng(encoded, exif),
            ExportFormat.WebP => EmbedInWebP(encoded, exif),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown export format."),
        };
    }

    // ---- JPEG (APP1) ----

    private static byte[]? ExtractFromJpeg(ReadOnlySpan<byte> s)
    {
        var pos = 2;
        while (pos + 4 <= s.Length)
        {
            if (s[pos] != 0xFF)
                return null;
            var marker = s[pos + 1];
            if (marker == 0xFF) { pos++; continue; } // fill byte
            if (marker is 0xDA or 0xD9)
                return null; // entropy data / end: no EXIF ahead
            var length = s[pos + 2] << 8 | s[pos + 3];
            if (length < 2 || pos + 2 + length > s.Length)
                return null;
            if (marker == 0xE1 && length >= 8 && s.Slice(pos + 4, 6).SequenceEqual("Exif\0\0"u8))
                return s.Slice(pos + 10, length - 8).ToArray();
            pos += 2 + length;
        }
        return null;
    }

    private static byte[] EmbedInJpeg(byte[] encoded, ReadOnlySpan<byte> exif)
    {
        if (encoded.Length < 2 || encoded[0] != 0xFF || encoded[1] != 0xD8)
            throw new InvalidOperationException("JPEG metadata embed: encoder output has no SOI.");
        var segmentLength = 2 + 6 + exif.Length; // length bytes + Exif\0\0 + blob
        if (segmentLength > 0xFFFF)
            return encoded; // an APP1 cannot carry it; auxiliary metadata never fails the export
        var result = new byte[encoded.Length + 2 + segmentLength];
        result[0] = 0xFF;
        result[1] = 0xD8;
        result[2] = 0xFF;
        result[3] = 0xE1; // APP1 immediately after SOI (EXIF placement convention)
        result[4] = (byte)(segmentLength >> 8);
        result[5] = (byte)segmentLength;
        "Exif\0\0"u8.CopyTo(result.AsSpan(6));
        exif.CopyTo(result.AsSpan(12));
        encoded.AsSpan(2).CopyTo(result.AsSpan(12 + exif.Length));
        return result;
    }

    // ---- PNG (eXIf chunk) ----

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[]? ExtractFromPng(ReadOnlySpan<byte> s)
    {
        var pos = 8;
        while (pos + 12 <= s.Length)
        {
            var length = (int)U32(s, pos, little: false);
            if (length < 0 || pos + 12 + (long)length > s.Length)
                return null;
            var type = s.Slice(pos + 4, 4);
            if (type.SequenceEqual("eXIf"u8))
            {
                // Foreign input: a chunk that fails its own CRC is corruption, not metadata.
                var stored = U32(s, pos + 8 + length, little: false);
                if (Crc32(s.Slice(pos + 4, 4 + length)) != stored)
                    return null;
                return s.Slice(pos + 8, length).ToArray();
            }
            if (type.SequenceEqual("IEND"u8))
                return null;
            pos += 12 + length;
        }
        return null;
    }

    private static byte[] EmbedInPng(byte[] encoded, ReadOnlySpan<byte> exif)
    {
        // 8-byte signature + IHDR (8 header + 13 data + 4 CRC) = insertion point 33.
        if (encoded.Length < 33 || !encoded.AsSpan().StartsWith(PngSignature)
            || !encoded.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidOperationException("PNG metadata embed: encoder output has no IHDR.");
        const int insertAt = 33;
        var chunk = new byte[12 + exif.Length];
        W32(chunk, 0, (uint)exif.Length, little: false);
        "eXIf"u8.CopyTo(chunk.AsSpan(4));
        exif.CopyTo(chunk.AsSpan(8));
        W32(chunk, 8 + exif.Length, Crc32(chunk.AsSpan(4, 4 + exif.Length)), little: false);

        var result = new byte[encoded.Length + chunk.Length];
        encoded.AsSpan(0, insertAt).CopyTo(result);
        chunk.CopyTo(result.AsSpan(insertAt));
        encoded.AsSpan(insertAt).CopyTo(result.AsSpan(insertAt + chunk.Length));
        return result;
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB8_8320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }

    // ---- WebP (VP8X + EXIF chunk) ----

    private static byte[]? ExtractFromWebP(ReadOnlySpan<byte> s)
    {
        // The declared RIFF size bounds the scan: trailing bytes are not container content.
        var end = Math.Min(s.Length, 8L + U32Le(s, 4));
        var pos = 12;
        while (pos + 8 <= end)
        {
            var fourCc = s.Slice(pos, 4);
            var size = (int)U32Le(s, pos + 4);
            if (size < 0 || pos + 8 + (long)size > end)
                return null;
            if (fourCc.SequenceEqual("EXIF"u8))
            {
                var payload = s.Slice(pos + 8, size);
                // Some writers keep the JPEG-style prefix; the spec wants the bare TIFF header.
                if (payload.Length > 6 && payload.StartsWith("Exif\0\0"u8))
                    payload = payload[6..];
                return payload.ToArray();
            }
            pos += 8 + size + (size & 1);
        }
        return null;
    }

    private static byte[] EmbedInWebP(byte[] encoded, ReadOnlySpan<byte> exif)
    {
        var s = encoded.AsSpan();
        if (encoded.Length < 20 || !s.StartsWith("RIFF"u8) || !s.Slice(8, 4).SequenceEqual("WEBP"u8))
            throw new InvalidOperationException("WebP metadata embed: not a RIFF container.");
        var first = s.Slice(12, 4);

        // Already-extended still (e.g. lossy + alpha): flag EXIF and append the chunk.
        if (first.SequenceEqual("VP8X"u8))
        {
            var basePadded = encoded.Length + (encoded.Length & 1);
            var appended = new byte[basePadded + 8 + exif.Length + (exif.Length & 1)];
            encoded.CopyTo(appended, 0);
            appended[20] |= 0x08;
            "EXIF"u8.CopyTo(appended.AsSpan(basePadded));
            W32Le(appended, basePadded + 4, (uint)exif.Length);
            exif.CopyTo(appended.AsSpan(basePadded + 8));
            W32Le(appended, 4, (uint)(appended.Length - 8));
            return appended;
        }

        // Simple still: promote to VP8X, deriving canvas and alpha from the stream header.
        int width, height;
        var hasAlpha = false;
        if (first.SequenceEqual("VP8 "u8))
        {
            // Keyframe header: 3-byte frame tag, 9D 01 2A start code, then 14-bit dimensions.
            if (encoded.Length < 30 || s[23] != 0x9D || s[24] != 0x01 || s[25] != 0x2A)
                throw new InvalidOperationException("WebP metadata embed: unrecognized VP8 header.");
            width = (s[26] | s[27] << 8) & 0x3FFF;
            height = (s[28] | s[29] << 8) & 0x3FFF;
        }
        else if (first.SequenceEqual("VP8L"u8))
        {
            if (encoded.Length < 25 || s[20] != 0x2F)
                throw new InvalidOperationException("WebP metadata embed: unrecognized VP8L header.");
            var bits = U32Le(s, 21);
            width = (int)(bits & 0x3FFF) + 1;
            height = (int)(bits >> 14 & 0x3FFF) + 1;
            hasAlpha = (bits >> 28 & 1) != 0;
        }
        else
        {
            throw new InvalidOperationException("WebP metadata embed: unrecognized image chunk.");
        }

        var imageChunks = encoded.Length - 12;
        var exifPadded = exif.Length + (exif.Length & 1);
        var result = new byte[12 + 18 + imageChunks + 8 + exifPadded];
        var w = result.AsSpan();
        "RIFF"u8.CopyTo(w);
        W32Le(result, 4, (uint)(result.Length - 8));
        "WEBP"u8.CopyTo(w[8..]);
        "VP8X"u8.CopyTo(w[12..]);
        W32Le(result, 16, 10);
        result[20] = (byte)(0x08 | (hasAlpha ? 0x10 : 0)); // EXIF flag (+ alpha when the stream has it)
        W24Le(result, 24, (uint)(width - 1));
        W24Le(result, 27, (uint)(height - 1));
        encoded.AsSpan(12).CopyTo(w[30..]);
        var at = 30 + imageChunks;
        "EXIF"u8.CopyTo(w[at..]);
        W32Le(result, at + 4, (uint)exif.Length);
        exif.CopyTo(w[(at + 8)..]);
        return result;
    }

    private static uint U32Le(ReadOnlySpan<byte> s, int at) =>
        s[at] | (uint)s[at + 1] << 8 | (uint)s[at + 2] << 16 | (uint)s[at + 3] << 24;

    private static void W32Le(byte[] s, int at, uint value)
    {
        s[at] = (byte)value;
        s[at + 1] = (byte)(value >> 8);
        s[at + 2] = (byte)(value >> 16);
        s[at + 3] = (byte)(value >> 24);
    }

    private static void W24Le(byte[] s, int at, uint value)
    {
        s[at] = (byte)value;
        s[at + 1] = (byte)(value >> 8);
        s[at + 2] = (byte)(value >> 16);
    }
}
