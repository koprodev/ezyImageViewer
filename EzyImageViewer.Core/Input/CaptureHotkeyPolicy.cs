using System.Collections.ObjectModel;

namespace EzyImageViewer.Core.Input;

/// <summary>Defines the persisted and runtime-safe global capture hotkey domain.</summary>
public static class CaptureHotkeyPolicy
{
    public const uint AllowedModifierMask = 0x000F;

    private static readonly ReadOnlyCollection<int> Keys = Array.AsReadOnly(
        Enumerable.Range(0x41, 26)
            .Concat(Enumerable.Range(0x30, 10))
            .Concat(Enumerable.Range(0x70, 24))
            .ToArray());

    public static IReadOnlyList<int> SupportedVirtualKeys => Keys;

    public static bool IsSupportedChord(uint modifiers, int virtualKey) =>
        modifiers != 0
        && (modifiers & ~AllowedModifierMask) == 0
        && IsSupportedVirtualKey(virtualKey);

    public static bool IsSupportedChord(uint modifiers, uint virtualKey) =>
        virtualKey <= int.MaxValue
        && IsSupportedChord(modifiers, (int)virtualKey);

    public static bool IsSupportedVirtualKey(int virtualKey) =>
        virtualKey is >= 0x30 and <= 0x39
            or >= 0x41 and <= 0x5A
            or >= 0x70 and <= 0x87;

    public static string GetVirtualKeyDisplayName(int virtualKey)
    {
        if (!IsSupportedVirtualKey(virtualKey))
            throw new ArgumentOutOfRangeException(nameof(virtualKey));

        return virtualKey switch
        {
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A =>
                ((char)virtualKey).ToString(),
            _ => $"F{virtualKey - 0x6F}",
        };
    }
}
