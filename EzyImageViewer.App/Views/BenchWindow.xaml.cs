using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SkiaSharp.Views.Windows;

namespace EzyImageViewer.App.Views;

/// <summary>
/// WP4 백엔드 게이트 호스트.
/// SKXamlCanvas(CPU 래스터) 또는 SKSwapChainPanel(GL)에서 공용 <see cref="BenchScenario"/> 실행.
/// 스왑 체인은 XAML 배경 위에 합성하지 못하므로 체크무늬와 내용도 패널 안에서 그림.
/// </summary>
public sealed partial class BenchWindow : Window
{
    private readonly BenchScenario _scenario;

    public BenchWindow(string backend, string outputPath)
    {
        InitializeComponent();
        _scenario = new BenchScenario(backend, outputPath);

        // 가능한 가장 큰 물리 뷰포트를 재도록 최대화. 실제 크기도 기록.
        (AppWindow.Presenter as OverlappedPresenter)?.Maximize();

        if (backend == "swapchain")
        {
            var panel = new SKSwapChainPanel();
            panel.PaintSurface += (_, e) =>
            {
                var size = e.Surface.Canvas.DeviceClipBounds;
                if (_scenario.PaintFrame(e.Surface.Canvas, size.Width, size.Height, RasterScale()))
                    DispatcherQueue.TryEnqueue(panel.Invalidate);
                else
                    FinishIfDone();
            };
            Host.Children.Add(panel);
        }
        else
        {
            var canvas = new SKXamlCanvas();
            canvas.PaintSurface += (_, e) =>
            {
                if (_scenario.PaintFrame(e.Surface.Canvas, e.Info.Width, e.Info.Height, RasterScale()))
                    DispatcherQueue.TryEnqueue(canvas.Invalidate);
                else
                    FinishIfDone();
            };
            Host.Children.Add(canvas);
        }
    }

    private double RasterScale() => Content?.XamlRoot?.RasterizationScale ?? 1.0;

    private void FinishIfDone()
    {
        if (_scenario.IsDone)
            DispatcherQueue.TryEnqueue(() => Application.Current.Exit());
    }
}
