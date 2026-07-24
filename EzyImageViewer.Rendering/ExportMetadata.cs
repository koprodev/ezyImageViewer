namespace EzyImageViewer.Rendering;

/// <summary>
/// 원본 EXIF에서 개인정보를 걷어내고 내보내기에 전달.
/// IFD 허용 목록으로 TIFF를 새로 만들며 자유 텍스트·GPS·MakerNote·일련번호·썸네일은 복사 금지.
/// 방향과 크기는 출력 래스터에 맞추고 구조가 수상하면 메타데이터 전체를 버림.
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

    // 허용 형식 비트마스크(1 << TIFF 형식 ID).
    private const ushort A = 1 << 2;   // ASCII 문자열
    private const ushort S = 1 << 3;   // SHORT 정수
    private const ushort L = 1 << 4;   // LONG 정수
    private const ushort R = 1 << 5;   // RATIONAL 유리수
    private const ushort U = 1 << 7;   // UNDEFINED 원시값
    private const ushort SR = 1 << 10; // SRATIONAL 부호 유리수

    /// <summary>IFD0: 카메라 식별과 래스터 구조만 허용. 작성자·설명은 제외.</summary>
    private static readonly Dictionary<ushort, ushort> ZerothAllowed = new()
    {
        [0x010F] = A, // 제조사(Make)
        [0x0110] = A, // 모델(Model)
        [OrientationTag] = S, // 방향(Orientation), 1로 정규화
        [0x011A] = R, // 가로 해상도(XResolution)
        [0x011B] = R, // 세로 해상도(YResolution)
        [0x0128] = S, // 해상도 단위(ResolutionUnit)
        [0x0131] = A, // 소프트웨어(Software)
        [0x0132] = A, // 날짜·시각(DateTime)
        [0x0213] = S, // YCbCr 위치(YCbCrPositioning)
    };

    /// <summary>Exif IFD: 촬영값과 시각만 허용. 설명·고유 ID는 제외.</summary>
    private static readonly Dictionary<ushort, ushort> ExifAllowed = new()
    {
        [0x829A] = R,  // 노출 시간(ExposureTime)
        [0x829D] = R,  // 조리개 수치(FNumber)
        [0x8822] = S,  // 노출 프로그램(ExposureProgram)
        [0x8827] = S,  // 감도(PhotographicSensitivity, ISO)
        [0x8830] = S,  // 감도 형식(SensitivityType)
        [0x9000] = U,  // Exif 버전(ExifVersion)
        [0x9003] = A,  // 원본 시각(DateTimeOriginal)
        [0x9004] = A,  // 디지털화 시각(DateTimeDigitized)
        [0x9010] = A,  // 시각 오프셋(OffsetTime)
        [0x9011] = A,  // 원본 시각 오프셋(OffsetTimeOriginal)
        [0x9012] = A,  // 디지털화 시각 오프셋(OffsetTimeDigitized)
        [0x9101] = U,  // 구성 요소(ComponentsConfiguration)
        [0x9102] = R,  // 픽셀당 압축 비트(CompressedBitsPerPixel)
        [0x9201] = SR, // 셔터 속도(ShutterSpeedValue)
        [0x9202] = R,  // 조리개(ApertureValue)
        [0x9203] = SR, // 밝기(BrightnessValue)
        [0x9204] = SR, // 노출 보정(ExposureBiasValue)
        [0x9205] = R,  // 최대 조리개(MaxApertureValue)
        [0x9206] = R,  // 피사체 거리(SubjectDistance)
        [0x9207] = S,  // 측광 방식(MeteringMode)
        [0x9208] = S,  // 광원(LightSource)
        [0x9209] = S,  // 플래시(Flash)
        [0x920A] = R,  // 초점 거리(FocalLength)
        [0x9214] = S,  // 피사체 영역(SubjectArea)
        [0x9290] = A,  // 소수 초(SubSecTime)
        [0x9291] = A,  // 원본 소수 초(SubSecTimeOriginal)
        [0x9292] = A,  // 디지털화 소수 초(SubSecTimeDigitized)
        [0xA000] = U,  // Flashpix 버전(FlashpixVersion)
        [0xA001] = S,  // 색 공간(ColorSpace)
        [PixelXDimensionTag] = (ushort)(S | L), // 출력 너비로 다시 작성
        [PixelYDimensionTag] = (ushort)(S | L), // 출력 높이로 다시 작성
        [0xA20E] = R,  // 초점면 가로 해상도(FocalPlaneXResolution)
        [0xA20F] = R,  // 초점면 세로 해상도(FocalPlaneYResolution)
        [0xA210] = S,  // 초점면 해상도 단위(FocalPlaneResolutionUnit)
        [0xA217] = S,  // 감지 방식(SensingMethod)
        [0xA300] = U,  // 파일 원본(FileSource)
        [0xA301] = U,  // 장면 형식(SceneType)
        [0xA401] = S,  // 사용자 렌더링(CustomRendered)
        [0xA402] = S,  // 노출 모드(ExposureMode)
        [0xA403] = S,  // 화이트 밸런스(WhiteBalance)
        [0xA404] = R,  // 디지털 줌 비율(DigitalZoomRatio)
        [0xA405] = S,  // 35mm 환산 초점 거리(FocalLengthIn35mmFilm)
        [0xA406] = S,  // 장면 촬영 형식(SceneCaptureType)
        [0xA407] = S,  // 게인 제어(GainControl)
        [0xA408] = S,  // 대비(Contrast)
        [0xA409] = S,  // 채도(Saturation)
        [0xA40A] = S,  // 선명도(Sharpness)
        [0xA40C] = S,  // 피사체 거리 범위(SubjectDistanceRange)
        [0xA432] = R,  // 렌즈 사양(LensSpecification)
        [0xA433] = A,  // 렌즈 제조사(LensMake)
        [0xA434] = A,  // 렌즈 모델(LensModel)
    };

    private static readonly Dictionary<ushort, ushort> InteropAllowed = new()
    {
        [0x0001] = A, // 상호운용 색인(InteroperabilityIndex)
        [0x0002] = U, // 상호운용 버전(InteroperabilityVersion)
    };

    // TIFF 값 형식 크기. 색인은 1~12 형식 ID, 0은 미지원이라 항목 폐기.
    private static readonly byte[] TypeSize = [0, 1, 1, 2, 4, 8, 1, 1, 2, 4, 8, 4, 8];

    /// <summary>JPEG·PNG·WebP에서 EXIF TIFF 원문 추출. PNG CRC와 WebP RIFF 범위까지 검증.</summary>
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

    /// <summary>허용 목록으로 원문 재작성. 유효 항목이 없거나 구조 검증 실패면 null.</summary>
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
        /// <summary>IFD 블록. 서로 겹치면 순환·자기 참조 구조.</summary>
        public readonly List<(long Start, long End)> Blocks = [];
        /// <summary>폐기·민감 값 영역. 보존 값이 여기서 읽으면 탈락.</summary>
        public readonly List<(long Start, long End)> Reserved = [];
        /// <summary>보존 값의 외부 영역. 다른 모든 영역과 분리돼야 함.</summary>
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
        // GPS와 썸네일 체인은 영역만 추적. 그 바이트를 보존 값이 빌려 쓰면 전체 탈락.
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
        /// <summary>GPS·썸네일 IFD: 보존 없이 소유 바이트 전부 예약.</summary>
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
                Reject(); // 중복 태그는 보존·폐기 판정을 흐림.
            if (type == 0 || type >= TypeSize.Length)
                continue; // 크기를 모르면 바이트 위치도 모르니 보존 안 함.
            var size = checked((long)TypeSize[type] * valueCount);
            var external = size > 4;
            long pointer = external ? U32(exif, at + 8, state.Little) : 0;
            if (external && (pointer < 8 || pointer + size > exif.Length))
                Reject();

            // 하위 IFD 포인터는 LONG 한 개만 허용. 나머지는 수상한 모양.
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

            // 출력 래스터는 이미 똑바로 서고 크기도 바뀌었으니 구조값 정규화.
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

    /// <summary>보존·IFD·민감 영역은 서로 겹치면 안 됨. 별칭 하나면 원문 전체 폐기.</summary>
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
        // 합성 하위 IFD 포인터로 체인 유지. 빈 하위 IFD는 깔끔하게 제거.
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

        // 첫 순회에서 외부 값 영역 크기 계산. TIFF 규약대로 짝수 정렬.
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
                entry.Value.CopyTo(blob, at + 8); // 남는 바이트는 0 유지.
            }
            else
            {
                data = (data + 1) & ~1;
                W32(blob, at + 8, (uint)data, little);
                entry.Value.CopyTo(blob, data);
                data += entry.Value.Length;
            }
        }
        W32(blob, offset + 2 + ifd.Count * 12, 0, little); // 다음 IFD 없음. 썸네일 탑승 금지.
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

    /// <summary>정리한 원문을 인코더 출력에 삽입. APP1 초과는 저장을 깨지 않고 건너뜀.</summary>
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

    // ---- JPEG APP1 영역 ---------------------------------------------------------------------

    private static byte[]? ExtractFromJpeg(ReadOnlySpan<byte> s)
    {
        var pos = 2;
        while (pos + 4 <= s.Length)
        {
            if (s[pos] != 0xFF)
                return null;
            var marker = s[pos + 1];
            if (marker == 0xFF) { pos++; continue; } // 채움 바이트.
            if (marker is 0xDA or 0xD9)
                return null; // 엔트로피 데이터·끝 뒤에는 EXIF 없음.
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
        var segmentLength = 2 + 6 + exif.Length; // 길이 + Exif\0\0 + 원문.
        if (segmentLength > 0xFFFF)
            return encoded; // APP1에 못 담아도 보조 정보 때문에 저장을 깨진 않음.
        var result = new byte[encoded.Length + 2 + segmentLength];
        result[0] = 0xFF;
        result[1] = 0xD8;
        result[2] = 0xFF;
        result[3] = 0xE1; // EXIF 관례대로 SOI 바로 뒤 APP1.
        result[4] = (byte)(segmentLength >> 8);
        result[5] = (byte)segmentLength;
        "Exif\0\0"u8.CopyTo(result.AsSpan(6));
        exif.CopyTo(result.AsSpan(12));
        encoded.AsSpan(2).CopyTo(result.AsSpan(12 + exif.Length));
        return result;
    }

    // ---- PNG eXIf 청크 ----------------------------------------------------------------------

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
                // 자기 CRC도 틀린 외부 청크는 메타데이터가 아니라 손상 데이터.
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
        // 8바이트 시그니처 + IHDR(헤더 8 + 데이터 13 + CRC 4) = 삽입 위치 33.
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

    // ---- WebP VP8X + EXIF 청크 ---------------------------------------------------------------

    private static byte[]? ExtractFromWebP(ReadOnlySpan<byte> s)
    {
        // 선언된 RIFF 크기까지만 순회. 꼬리 바이트는 컨테이너 내용 아님.
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
                // 일부 작성기는 JPEG 접두사를 남기지만 규격은 순수 TIFF 헤더 요구.
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

        // 이미 확장된 정지 이미지면 EXIF 플래그를 켜고 청크 추가.
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

        // 단순 정지 이미지는 스트림 헤더에서 캔버스·알파를 읽어 VP8X로 승격.
        int width, height;
        var hasAlpha = false;
        if (first.SequenceEqual("VP8 "u8))
        {
            // 키 프레임 헤더: 3바이트 태그, 9D 01 2A 시작 코드, 14비트 크기.
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
        result[20] = (byte)(0x08 | (hasAlpha ? 0x10 : 0)); // EXIF 플래그와 스트림 알파.
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
