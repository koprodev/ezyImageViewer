using System.Diagnostics;
using System.Text.Json;
using SkiaSharp;

namespace EzyImageViewer.App.Views;

/// <summary>
/// Shared scripted zoom/pan benchmark used by both render backends (WP4/NFR-PERF-005).
/// Metrics: frameIntervalMs = time between paint callbacks (queue latency included);
/// paintMs = raster/GL work only. Exactly <see cref="MeasuredFrames"/> intervals are recorded.
/// </summary>
public sealed class BenchScenario(string backend, string outputPath)
{
    public const int WarmupFrames = 30;
    public const int MeasuredFrames = 300;

    private readonly SKBitmap _testImage = CreateTestImage(4000, 3000);
    private readonly List<double> _intervalMs = new(WarmupFrames + MeasuredFrames);
    private readonly List<double> _paintMs = new(WarmupFrames + MeasuredFrames);
    private readonly Stopwatch _frameClock = new();
    private readonly Stopwatch _paintClock = new();

    private float _scale = 1f;
    private SKPoint _offset;
    private int _frame;

    public bool IsDone { get; private set; }

    public static SKBitmap CreateTestImage(int width, int height)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        const int cell = 64;
        using var dark = new SKPaint { Color = new SKColor(0x60, 0x60, 0x68) };
        using var light = new SKPaint { Color = new SKColor(0xA0, 0xA0, 0xA8) };
        for (var y = 0; y < height; y += cell)
        for (var x = 0; x < width; x += cell)
            canvas.DrawRect(x, y, cell, cell, ((x + y) / cell) % 2 == 0 ? dark : light);

        var rng = new Random(42);
        for (var i = 0; i < 400; i++)
        {
            using var paint = new SKPaint
            {
                Color = new SKColor((byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256), 0xB0),
                IsAntialias = true,
            };
            canvas.DrawCircle(rng.Next(width), rng.Next(height), rng.Next(20, 160), paint);
        }
        return bitmap;
    }

    /// <summary>Draws one scripted frame; returns false once measurement is complete.</summary>
    public bool PaintFrame(SKCanvas canvas, int viewportWidth, int viewportHeight, double rasterizationScale)
    {
        if (IsDone)
            return false;

        if (_frameClock.IsRunning)
            _intervalMs.Add(_frameClock.Elapsed.TotalMilliseconds);
        _frameClock.Restart();

        var t = _frame / 60.0;
        _scale = (float)(0.4 + 2.0 * (1 + Math.Sin(t * Math.PI / 2)) / 2);
        _offset = new SKPoint(
            (float)(-800 + 400 * Math.Sin(t)),
            (float)(-500 + 300 * Math.Cos(t)));
        _frame++;

        _paintClock.Restart();
        canvas.Clear(new SKColor(0x28, 0x28, 0x2C));
        canvas.Save();
        canvas.Translate(_offset.X, _offset.Y);
        canvas.Scale(_scale);
        canvas.DrawBitmap(_testImage, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        canvas.Restore();
        _paintClock.Stop();
        _paintMs.Add(_paintClock.Elapsed.TotalMilliseconds);

        if (_intervalMs.Count >= WarmupFrames + MeasuredFrames)
        {
            IsDone = true;
            WriteResult(viewportWidth, viewportHeight, rasterizationScale);
            return false;
        }
        return true;
    }

    private void WriteResult(int viewportWidth, int viewportHeight, double rasterizationScale)
    {
        var intervals = _intervalMs.Skip(WarmupFrames).ToArray();
        var paints = _paintMs.TakeLast(intervals.Length).ToArray();
        Array.Sort(intervals);
        var sortedPaints = (double[])paints.Clone();
        Array.Sort(sortedPaints);

        var result = new
        {
            metric = "frameIntervalMs = time between paint callbacks; paintMs = raster/GL work only",
            backend,
            frames = intervals.Length,
            imageSize = $"{_testImage.Width}x{_testImage.Height}",
            viewportPx = $"{viewportWidth}x{viewportHeight}",
            meets4K = viewportWidth >= 3840 && viewportHeight >= 2160,
            rasterizationScale,
#if DEBUG
            buildConfig = "Debug",
#else
            buildConfig = "Release",
#endif
            os = Environment.OSVersion.VersionString,
            processorCount = Environment.ProcessorCount,
            frameIntervalAvgMs = intervals.Average(),
            frameIntervalP95Ms = intervals[(int)(intervals.Length * 0.95)],
            frameIntervalP99Ms = intervals[(int)(intervals.Length * 0.99)],
            frameIntervalMaxMs = intervals[^1],
            overBudgetRatio = intervals.Count(v => v > 16.67) / (double)intervals.Length,
            paintAvgMs = paints.Length > 0 ? paints.Average() : 0,
            paintP95Ms = sortedPaints.Length > 0 ? sortedPaints[(int)(sortedPaints.Length * 0.95)] : 0,
            paintMaxMs = sortedPaints.Length > 0 ? sortedPaints[^1] : 0,
            effectiveFps = 1000.0 / intervals.Average(),
            timestampUtc = DateTimeOffset.UtcNow,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
