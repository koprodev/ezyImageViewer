using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SkiaSharp.Views.Windows;

namespace EzyImageViewer.App.Views;

/// <summary>
/// WP4 backend gate host: runs the shared <see cref="BenchScenario"/> on SKXamlCanvas (CPU raster)
/// or SKSwapChainPanel (GL). The checkerboard/content is drawn inside the panel — the swap chain
/// does not composite over XAML background (visual-layer constraint).
/// </summary>
public sealed partial class BenchWindow : Window
{
    private readonly BenchScenario _scenario;

    public BenchWindow(string backend, string outputPath)
    {
        InitializeComponent();
        _scenario = new BenchScenario(backend, outputPath);

        // Maximize to measure the largest available physical viewport (actual size is recorded).
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
