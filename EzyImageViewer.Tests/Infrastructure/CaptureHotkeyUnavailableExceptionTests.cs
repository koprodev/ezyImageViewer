using EzyImageViewer.Infrastructure;
using Xunit;

namespace EzyImageViewer.Tests.Infrastructure;

public sealed class CaptureHotkeyUnavailableExceptionTests
{
    [Fact]
    public void Exception_PreservesTheRequestedChordForLocalizedPresentation()
    {
        var hotkey = new CaptureHotkey
        {
            Modifiers = HotkeyModifiers.Alt | HotkeyModifiers.Shift,
            VirtualKey = 0x87,
        };

        var exception = new CaptureHotkeyUnavailableException(hotkey);

        Assert.Same(hotkey, exception.RequestedHotkey);
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Throws<ArgumentNullException>(
            () => new CaptureHotkeyUnavailableException(null!));
    }
}
