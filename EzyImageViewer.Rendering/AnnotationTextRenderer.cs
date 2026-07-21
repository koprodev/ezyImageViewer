using System.Globalization;
using System.Text;
using EzyImageViewer.Core.Documents.Layers;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace EzyImageViewer.Rendering;

internal static class AnnotationTextRenderer
{
    public static void Draw(SKCanvas canvas, TextAnnotation annotation, SKColor color)
    {
        var style = annotation.IsBold
            ? annotation.IsItalic ? SKFontStyle.BoldItalic : SKFontStyle.Bold
            : annotation.IsItalic ? SKFontStyle.Italic : SKFontStyle.Normal;
        using var primary = SKFontManager.Default.MatchFamily(annotation.FontFamily, style)
            ?? SKTypeface.Default;
        using var primaryFont = new SKFont(primary, annotation.FontSize);
        var metrics = primaryFont.Metrics;
        var lineHeight = MathF.Max(annotation.FontSize, metrics.Descent - metrics.Ascent + metrics.Leading);
        var baseline = annotation.Bounds.Y - metrics.Ascent;
        using var paint = new SKPaint { IsAntialias = true, Color = color };

        canvas.Save();
        canvas.ClipRect(SKRect.Create(
            annotation.Bounds.X, annotation.Bounds.Y,
            annotation.Bounds.Width, annotation.Bounds.Height));
        foreach (var line in annotation.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (baseline > annotation.Bounds.Bottom + metrics.Descent)
                break;
            using var runs = BuildRuns(line, annotation.FontFamily, style, primary, primaryFont);
            var width = Measure(runs.Items, annotation.FontSize);
            var x = annotation.Alignment switch
            {
                AnnotationTextAlignment.Center =>
                    annotation.Bounds.X + ((annotation.Bounds.Width - width) / 2f),
                AnnotationTextAlignment.Right => annotation.Bounds.Right - width,
                _ => annotation.Bounds.X,
            };
            foreach (var run in runs.Items)
            {
                using var font = new SKFont(run.Typeface, annotation.FontSize);
                using var shaper = new SKShaper(run.Typeface);
                canvas.DrawShapedText(
                    shaper, run.Text, x, baseline, SKTextAlign.Left, font, paint);
                x += shaper.Shape(run.Text, font).Width;
            }
            baseline += lineHeight;
        }
        canvas.Restore();
    }

    private static float Measure(IReadOnlyList<TextRun> runs, float size)
    {
        var width = 0f;
        foreach (var run in runs)
        {
            using var font = new SKFont(run.Typeface, size);
            using var shaper = new SKShaper(run.Typeface);
            width += shaper.Shape(run.Text, font).Width;
        }
        return width;
    }

    private static TextRunCollection BuildRuns(
        string text, string family, SKFontStyle style, SKTypeface primary, SKFont primaryFont)
    {
        var collection = new TextRunCollection();
        if (text.Length == 0)
            return collection;

        var enumerator = StringInfo.GetTextElementEnumerator(text);
        StringBuilder? current = null;
        SKTypeface? currentTypeface = null;
        var currentOwned = false;
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            var typeface = primary;
            var owned = false;
            if (!primaryFont.ContainsGlyphs(element))
            {
                var rune = Rune.GetRuneAt(element, 0);
                typeface = SKFontManager.Default.MatchCharacter(
                    family, style, Array.Empty<string>(), rune.Value) ?? primary;
                owned = !ReferenceEquals(typeface, primary);
            }

            if (currentTypeface is not null && SameTypeface(currentTypeface, typeface))
            {
                current!.Append(element);
                if (owned)
                    typeface.Dispose();
                continue;
            }

            if (currentTypeface is not null)
                collection.Add(new TextRun(current!.ToString(), currentTypeface, currentOwned));
            current = new StringBuilder(element);
            currentTypeface = typeface;
            currentOwned = owned;
        }
        if (currentTypeface is not null)
            collection.Add(new TextRun(current!.ToString(), currentTypeface, currentOwned));
        return collection;
    }

    private static bool SameTypeface(SKTypeface left, SKTypeface right) =>
        left.FamilyName == right.FamilyName && left.FontStyle == right.FontStyle;

    private sealed record TextRun(string Text, SKTypeface Typeface, bool OwnsTypeface);

    private sealed class TextRunCollection : IDisposable
    {
        public List<TextRun> Items { get; } = [];
        public void Add(TextRun run) => Items.Add(run);

        public void Dispose()
        {
            foreach (var run in Items)
            {
                if (run.OwnsTypeface)
                    run.Typeface.Dispose();
            }
        }
    }
}
