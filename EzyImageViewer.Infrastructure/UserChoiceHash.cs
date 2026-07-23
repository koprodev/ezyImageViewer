// SPDX-License-Identifier: MPL-2.0
// The UserChoice hash algorithm below is a C# port of the structure in Mozilla Firefox's
// browser/components/shell/WindowsUserChoice.cpp (Mozilla Public License 2.0). As an MPL "Larger
// Work" this file stays under MPL-2.0; the rest of ezyImageViewer remains under its root MIT
// license (see THIRD-PARTY-NOTICES.md). Output is cross-checked against the DanysysTeam/PS-SFTA
// (MIT) oracle in unit tests; that project's source is not included in the product.
using System.Security.Cryptography;
using System.Text;

namespace EzyImageViewer.Infrastructure;

/// <summary>
/// UserChoice hash core for the experimental in-app default-association writer. Windows Explorer
/// only honors a UserChoice ProgId whose Hash value matches this computation.
/// </summary>
public static class UserChoiceHash
{
    // Built into Windows as part of the UserChoice hash algorithm (shell32 resource string).
    public const string UserExperience =
        "User Choice set via Windows User Experience {D18B6DD5-6124-4341-9318-804003BAFA0B}";

    /// <summary>Hash input: lowercase(ext + sid + progId + minute-truncated FILETIME hex +
    /// experience string). All components are ASCII, so invariant lowercasing is exact.</summary>
    public static string BuildInput(
        string extension, string userSid, string progId, DateTime timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        ArgumentException.ThrowIfNullOrWhiteSpace(userSid);
        ArgumentException.ThrowIfNullOrWhiteSpace(progId);
        var fileTime = ToMinuteFileTimeUtc(timestamp);
        var timestampHex = ((uint)(fileTime >> 32)).ToString("x8")
            + ((uint)fileTime).ToString("x8");
        return (extension + userSid + progId + timestampHex + UserExperience)
            .ToLowerInvariant();
    }

    public static string ComputeHash(
        string extension, string userSid, string progId, DateTime timestamp) =>
        HashInput(BuildInput(extension, userSid, progId, timestamp));

    /// <summary>Seconds and milliseconds are zeroed; UTC-offset minutes are whole, so
    /// truncating the local or UTC minute selects the same instant.</summary>
    public static long ToMinuteFileTimeUtc(DateTime timestamp)
    {
        var utc = timestamp.Kind == DateTimeKind.Utc ? timestamp : timestamp.ToUniversalTime();
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0,
            DateTimeKind.Utc).ToFileTimeUtc();
    }

    public static string HashInput(string input)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        // UTF-16LE including the terminating NUL; both references hash the terminator.
        var bytes = new byte[(input.Length + 1) * 2];
        Encoding.Unicode.GetBytes(input, 0, input.Length, bytes, 0);

        // Two DWORDs per block; a trailing incomplete block is ignored.
        var blockCount = bytes.Length / 8;
        if (blockCount == 0)
            throw new ArgumentException("The hash input is too short.", nameof(input));

        var md5 = MD5.HashData(bytes);
        Span<uint> md5Words =
        [
            BitConverter.ToUInt32(md5, 0) | 1,
            BitConverter.ToUInt32(md5, 4) | 1,
        ];

        // Constant multipliers per DWORD position within a block (Mozilla formulation).
        ReadOnlySpan<uint> c00 = [md5Words[0], 0xCF98_B111u, 0x8708_5B9Fu, 0x12CE_B96Du, 0x257E_1D83u];
        ReadOnlySpan<uint> c01 = [md5Words[1], 0xA274_16F5u, 0xD383_96FFu, 0x7C93_2B89u, 0xBFA4_9F69u];
        ReadOnlySpan<uint> c10 = [md5Words[0], 0xEF05_69FBu, 0x689B_6B9Fu, 0x79F8_A395u, 0xC3EF_EA97u];
        ReadOnlySpan<uint> c11 = [md5Words[1], 0xC317_13DBu, 0xDDCD_1F0Fu, 0x59C3_AF2Du, 0x35BD_1EC9u];

        uint h0 = 0, h1 = 0, h0Acc = 0, h1Acc = 0;
        for (var block = 0; block < blockCount; block++)
        {
            for (var word = 0; word < 2; word++)
            {
                var c0 = word == 0 ? c00 : c01;
                var c1 = word == 0 ? c10 : c11;
                var input32 = BitConverter.ToUInt32(bytes, (block * 2 + word) * 4);

                unchecked
                {
                    h0 += input32;
                    h0 *= c0[0];
                    h0 = WordSwap(h0) * c0[1];
                    h0 = WordSwap(h0) * c0[2];
                    h0 = WordSwap(h0) * c0[3];
                    h0 = WordSwap(h0) * c0[4];
                    h0Acc += h0;

                    h1 += input32;
                    h1 = WordSwap(h1) * c1[1] + h1 * c1[0];
                    h1 = (h1 >> 16) * c1[2] + h1 * c1[3];
                    h1 = WordSwap(h1) * c1[4] + h1;
                    h1Acc += h1;
                }
            }
        }

        var result = new byte[8];
        BitConverter.TryWriteBytes(result.AsSpan(0, 4), h0 ^ h1);
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), h0Acc ^ h1Acc);
        return Convert.ToBase64String(result);
    }

    private static uint WordSwap(uint value) => (value >> 16) | (value << 16);
}
