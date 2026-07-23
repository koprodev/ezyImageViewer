using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Rendering;

/// <summary>Window size and the canvas scale that produced it, both in physical pixels.</summary>
public readonly record struct InitialWindowLayout(PixelSize WindowSize, float ContentScale);

/// <summary>
/// Sizes a viewer window around the first image it shows: the canvas gets the picture at up to
/// 100% plus a margin on every side, and the window never outgrows its monitor's work area.
/// Pure physical-pixel math — the caller owns DPI conversion and the actual window call.
/// The scale is returned because Fit is edge-to-edge by definition and cannot express a margin;
/// the view opens at this scale instead (see <see cref="ViewTransform.OpenAtScale"/>).
/// </summary>
public static class InitialWindowGeometry
{
    /// <summary>Largest share of the monitor work area an auto-sized window may claim.</summary>
    public const double WorkAreaFraction = 0.9;

    /// <param name="content">Image size in its own pixels.</param>
    /// <param name="chrome">Window size minus canvas size: title bar, borders, status bar.</param>
    /// <param name="margin">Canvas margin per side, around the image.</param>
    /// <param name="workArea">Monitor work area the window must stay inside.</param>
    /// <param name="minimumWindow">Floor that keeps the tool rail and status bar usable; the work
    /// area still wins when the monitor is smaller than the floor.</param>
    public static InitialWindowLayout Measure(
        PixelSize content,
        PixelSize chrome,
        PixelSize margin,
        PixelSize workArea,
        PixelSize minimumWindow)
    {
        var maxWidth = Math.Max(1, (int)Math.Round(Math.Max(1, workArea.Width) * WorkAreaFraction));
        var maxHeight = Math.Max(1, (int)Math.Round(Math.Max(1, workArea.Height) * WorkAreaFraction));
        var chromeWidth = Math.Max(0, chrome.Width);
        var chromeHeight = Math.Max(0, chrome.Height);
        var marginWidth = Math.Max(0, margin.Width);
        var marginHeight = Math.Max(0, margin.Height);
        var contentWidth = Math.Max(1, content.Width);
        var contentHeight = Math.Max(1, content.Height);

        var availableWidth = Math.Max(1, maxWidth - chromeWidth - (2 * marginWidth));
        var availableHeight = Math.Max(1, maxHeight - chromeHeight - (2 * marginHeight));
        // Never upscale: a 64px icon opens at 100% in a floor-sized window, not blown up.
        var scale = Math.Min(
            1d,
            Math.Min(availableWidth / (double)contentWidth, availableHeight / (double)contentHeight));

        var width = (int)Math.Round(contentWidth * scale) + (2 * marginWidth) + chromeWidth;
        var height = (int)Math.Round(contentHeight * scale) + (2 * marginHeight) + chromeHeight;
        return new InitialWindowLayout(
            new PixelSize(
                Math.Clamp(width, Math.Min(Math.Max(1, minimumWindow.Width), maxWidth), maxWidth),
                Math.Clamp(height, Math.Min(Math.Max(1, minimumWindow.Height), maxHeight), maxHeight)),
            (float)scale);
    }

    /// <summary>Centers the window in the work area, never starting outside its top-left corner.</summary>
    public static (int X, int Y) Center(
        PixelSize window, PixelSize workArea, int workAreaX, int workAreaY) =>
        (workAreaX + Math.Max(0, (workArea.Width - window.Width) / 2),
            workAreaY + Math.Max(0, (workArea.Height - window.Height) / 2));
}
