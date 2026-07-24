using EzyImageViewer.App.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SkiaSharp;

namespace EzyImageViewer.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, eventArgs) =>
            Program.RecordUnhandledStartupFailure(eventArgs.Exception);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        StartupTimeline.Mark("onLaunched");
        var commandLine = Environment.GetCommandLineArgs();
        string? Arg(string prefix) => commandLine
            .FirstOrDefault(a => a.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];

        var benchOut = Arg("--bench-zoompan=") ?? Arg("--spike-zoompan=");
        if (benchOut is not null)
        {
            _window = new BenchWindow(Arg("--bench-backend=") ?? "xaml", benchOut);
            _window.Activate();
            return;
        }

        if (Arg("--bench-startup=") is { } startupOut)
        {
            AppServices.ConfigureStartupBenchmark(
                startupOut,
                Program.ProcessStartTimestamp);
            AppServices.InitializeUi(DispatcherQueue.GetForCurrentThread());
            return;
        }

        if (Program.IsRecoverySmoke)
        {
            AppServices.InitializeUi(DispatcherQueue.GetForCurrentThread());
            AppServices.Windows!.EnsurePrimary();
            return;
        }

        if (commandLine.Any(a => a.StartsWith("--bench-open24mp", StringComparison.Ordinal)))
        {
            var viewer = new ViewerWindow();
            viewer.ConfigureFirstPaintBench(Generate24MpJpeg(), Arg("--bench-open24mp="));
            _window = viewer;
            _window.Activate();
            return;
        }

        if (Arg("--smoke-open=") is { } smokePath)
        {
            var smokeOut = Arg("--smoke-out=")
                ?? Path.Combine(Path.GetTempPath(), "ezy-smoke.json");
            try
            {
                var viewer = new ViewerWindow();
                viewer.ConfigureSmoke(smokePath, smokeOut, Arg("--smoke-project="),
                    commandLine.Contains("--smoke-capture"));
                _window = viewer;
                _window.Activate();
            }
            catch (Exception ex)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(smokeOut)!);
                File.WriteAllText(smokeOut, System.Text.Json.JsonSerializer.Serialize(
                    new { state = "SmokeStartupError", error = ex.ToString() }));
                Exit();
            }
            return;
        }

        if (Arg("--smoke-hold=") is { } holdPath)
        {
            var viewer = new ViewerWindow();
            viewer.ConfigureEditHold(holdPath);
            _window = viewer;
            _window.Activate();
            return;
        }

            // 일반 경로: UI가 생기기 전에 Program.Main이 최초 요청을 올려 둠.
        AppServices.InitializeUi(DispatcherQueue.GetForCurrentThread());
    }

    /// <summary>NFR-PERF-002 최초 표시 측정용 합성 6000×4000(24MP) JPEG.</summary>
    private static string Generate24MpJpeg()
    {
        var path = Path.Combine(Path.GetTempPath(), "ezy-bench-24mp.jpg");
        using var bitmap = BenchScenario.CreateTestImage(6000, 4000);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        using var file = File.Create(path);
        data.SaveTo(file);
        return path;
    }
}
