using EzyImageViewer.Core.Input;
using Xunit;

namespace EzyImageViewer.Tests.Input;

public sealed class CaptureHotkeyPolicyTests
{
    [Fact]
    public void SupportedVirtualKeys_AreTheExactOrderedProductDomain()
    {
        var expected = Enumerable.Range(0x41, 26)
            .Concat(Enumerable.Range(0x30, 10))
            .Concat(Enumerable.Range(0x70, 24))
            .ToArray();

        Assert.Equal(60, CaptureHotkeyPolicy.SupportedVirtualKeys.Count);
        Assert.Equal(expected, CaptureHotkeyPolicy.SupportedVirtualKeys);
        Assert.Equal(
            CaptureHotkeyPolicy.SupportedVirtualKeys.Count,
            CaptureHotkeyPolicy.SupportedVirtualKeys.Distinct().Count());
        Assert.All(
            CaptureHotkeyPolicy.SupportedVirtualKeys,
            key => Assert.True(CaptureHotkeyPolicy.IsSupportedVirtualKey(key)));
    }

    [Theory]
    [InlineData(0x30, "0")]
    [InlineData(0x39, "9")]
    [InlineData(0x41, "A")]
    [InlineData(0x5A, "Z")]
    [InlineData(0x70, "F1")]
    [InlineData(0x87, "F24")]
    public void BoundaryKeys_AreSupportedAndHaveStableDisplayNames(
        int virtualKey,
        string displayName)
    {
        Assert.True(CaptureHotkeyPolicy.IsSupportedVirtualKey(virtualKey));
        Assert.Equal(
            displayName,
            CaptureHotkeyPolicy.GetVirtualKeyDisplayName(virtualKey));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(0x01)]
    [InlineData(0x20)]
    [InlineData(0x2F)]
    [InlineData(0x3A)]
    [InlineData(0x40)]
    [InlineData(0x5B)]
    [InlineData(0x6F)]
    [InlineData(0x88)]
    [InlineData(0xFF)]
    [InlineData(0x100)]
    public void OutOfPolicyKeys_AreRejected(int virtualKey)
    {
        Assert.False(CaptureHotkeyPolicy.IsSupportedVirtualKey(virtualKey));
        Assert.False(CaptureHotkeyPolicy.IsSupportedChord(0x0002, virtualKey));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => CaptureHotkeyPolicy.GetVirtualKeyDisplayName(virtualKey));
    }

    [Theory]
    [InlineData(0x0000, false)]
    [InlineData(0x0001, true)]
    [InlineData(0x0002, true)]
    [InlineData(0x0004, true)]
    [InlineData(0x0008, true)]
    [InlineData(0x000F, true)]
    [InlineData(0x0010, false)]
    [InlineData(0x8000, false)]
    public void ModifierMask_RequiresOneOrMoreKnownModifiers(uint modifiers, bool expected)
    {
        Assert.Equal(
            expected,
            CaptureHotkeyPolicy.IsSupportedChord(modifiers, (uint)'E'));
    }
}
