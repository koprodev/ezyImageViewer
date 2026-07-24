using EzyImageViewer.Core.Imaging;

namespace EzyImageViewer.Rendering;

/// <summary>창 크기와 그 크기를 만든 캔버스 배율. 모두 물리 픽셀.</summary>
public readonly record struct InitialWindowLayout(PixelSize WindowSize, float ContentScale);

/// <summary>
/// 첫 이미지를 최대 100%와 사방 여백으로 담되 모니터 작업 영역을 넘지 않게 계산.
/// 순수 물리 픽셀 계산이며 DPI 변환과 실제 창 호출은 호출자 몫.
/// </summary>
public static class InitialWindowGeometry
{
    /// <summary>자동 크기 창이 차지할 수 있는 모니터 작업 영역 최대 비율.</summary>
    public const double WorkAreaFraction = 0.9;

    /// <param name="content">이미지 자체 픽셀 크기.</param>
    /// <param name="chrome">창에서 캔버스를 뺀 제목·테두리·상태바 크기.</param>
    /// <param name="margin">이미지 한쪽 캔버스 여백.</param>
    /// <param name="workArea">창이 머물 모니터 작업 영역.</param>
    /// <param name="minimumWindow">도구 막대·상태바를 쓸 수 있는 최소 창 크기.</param>
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
        // 확대 금지. 64px 아이콘은 최소 창에서 100%로 열고 뻥튀기 안 함.
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

    /// <summary>작업 영역 중앙 배치. 왼쪽 위 밖에서 시작 금지.</summary>
    public static (int X, int Y) Center(
        PixelSize window, PixelSize workArea, int workAreaX, int workAreaY) =>
        (workAreaX + Math.Max(0, (workArea.Width - window.Width) / 2),
            workAreaY + Math.Max(0, (workArea.Height - window.Height) / 2));
}
