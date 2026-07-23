using EzyImageViewer.Core.Documents.Layers;
using EzyImageViewer.Core.Imaging;
using SkiaSharp;

namespace EzyImageViewer.Rendering;

/// <summary>Composites immutable annotation objects over the background at paint time.</summary>
public static class AnnotationRendering
{
    private static readonly SKPathEffect SelectionDash = SKPathEffect.CreateDash([4f, 4f], 0f);

    public static SKMatrix NativeToContent(PixelSize native, int frameWidth, int frameHeight)
    {
        if (native.IsEmpty || frameWidth <= 0 || frameHeight <= 0)
            return SKMatrix.Identity;
        return SKMatrix.CreateScale(frameWidth / (float)native.Width, frameHeight / (float)native.Height);
    }

    public static void DrawAnnotations(
        SKCanvas canvas,
        DocumentState state,
        SKMatrix nativeToView,
        Guid selectedId = default,
        RasterAssetImageCache? assetCache = null,
        SKImage? backgroundFrame = null,
        SKMatrix? frameToNative = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(state);

        var scale = AverageScale(nativeToView);
        var background = backgroundFrame is null
            ? default(BackgroundSource?)
            : new BackgroundSource(backgroundFrame, frameToNative ?? SKMatrix.Identity);
        canvas.Save();
        canvas.SetMatrix(nativeToView);
        // Layer order is the coarse paint order; a hidden layer hides all its objects (UR-007).
        foreach (var layer in state.Layers)
        {
            if (!layer.IsVisible)
                continue;
            foreach (var annotation in layer.Annotations)
            {
                if (!annotation.IsVisible)
                    continue;
                canvas.Save();
                if (annotation.RotationDegrees != 0f)
                {
                    canvas.RotateDegrees(
                        annotation.RotationDegrees, annotation.Bounds.CenterX, annotation.Bounds.CenterY);
                }
                DrawAnnotation(canvas, annotation, scale, assetCache, background);
                canvas.Restore();
            }
        }
        canvas.Restore();

        if (selectedId != default && state.IsEffectivelyVisible(selectedId)
            && state.Find(selectedId) is { } selected)
            DrawSelection(canvas, selected, nativeToView);
    }

    /// <summary>Allocation-free domain boundary for the live pointer draft.</summary>
    public static void DrawInkDraft(
        SKCanvas canvas,
        IReadOnlyList<AnnotationPoint> points,
        SKMatrix nativeToView,
        uint strokeArgb,
        float strokeWidth,
        float opacity)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count == 0)
            return;
        canvas.Save();
        canvas.SetMatrix(nativeToView);
        DrawInkPoints(canvas, points, strokeArgb, strokeWidth, opacity, AverageScale(nativeToView));
        canvas.Restore();
    }

    /// <summary>The background frame plus its frame→native map, for effects that sample it.</summary>
    private readonly record struct BackgroundSource(SKImage Frame, SKMatrix FrameToNative);

    private static void DrawAnnotation(
        SKCanvas canvas, Annotation annotation, float scale, RasterAssetImageCache? assetCache,
        BackgroundSource? background)
    {
        switch (annotation)
        {
            case InkAnnotation ink:
                DrawInk(canvas, ink, scale);
                break;
            case LineAnnotation line:
                DrawLine(canvas, line, scale);
                break;
            case RectangleAnnotation shape:
                DrawShape(canvas, shape, scale);
                break;
            case TextAnnotation text:
                DrawText(canvas, text);
                break;
            case SpeechBubbleAnnotation bubble:
                DrawSpeechBubble(canvas, bubble, scale);
                break;
            case NumberMarkerAnnotation marker:
                DrawNumberMarker(canvas, marker);
                break;
            case ImageAnnotation image:
                DrawImage(canvas, image, assetCache);
                break;
            case ProtectionAnnotation protection:
                DrawProtection(canvas, protection, background);
                break;
        }
    }

    /// <summary>Longest edge of a protection effect's offscreen; larger regions compute at reduced
    /// resolution (still obscuring — the effect destroys detail by design).</summary>
    private const int MaxEffectDim = 2_048;

    /// <summary>Draws a privacy region (FR-ANNO-008~010). Mosaic and blur sample the background
    /// frame only — never annotations beneath — in an offscreen at native resolution, so the view
    /// at any zoom and the flattened export produce the same pixels. Output is fully opaque and
    /// covers everything below its bounds. Without a background source only the mask can draw.</summary>
    private static void DrawProtection(
        SKCanvas canvas, ProtectionAnnotation protection, BackgroundSource? background)
    {
        var bounds = protection.Bounds;
        if (bounds.Width < 1f || bounds.Height < 1f)
            return;
        var destination = SKRect.Create(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        if (protection.Kind == ProtectionKind.Mask)
        {
            using var mask = new SKPaint
            {
                IsAntialias = false,
                Style = SKPaintStyle.Fill,
                Color = new SKColor(protection.MaskArgb).WithAlpha(0xFF),
            };
            canvas.DrawRect(destination, mask);
            return;
        }
        if (background is not { } source)
            return;

        if (protection.Kind == ProtectionKind.Mosaic)
        {
            using var mosaic = RenderMosaic(protection, source);
            if (mosaic is not null)
                canvas.DrawImage(mosaic, destination, new SKSamplingOptions(SKFilterMode.Nearest));
            return;
        }
        using var blurred = RenderBlur(protection, source);
        if (blurred is not null)
            canvas.DrawImage(blurred, destination, new SKSamplingOptions(SKFilterMode.Linear));
    }

    /// <summary>True block grid: cells are BlockSize native pixels anchored at the region origin,
    /// the trailing partial cells are clipped at the bounds (§9.3 edge default), and each cell is
    /// the exact box average of the pixels it covers.</summary>
    private static SKImage? RenderMosaic(ProtectionAnnotation protection, BackgroundSource source)
    {
        var bounds = protection.Bounds;
        var effectScale = MathF.Min(1f, MaxEffectDim / MathF.Max(bounds.Width, bounds.Height));
        var width = Math.Max(1, (int)MathF.Round(bounds.Width * effectScale));
        var height = Math.Max(1, (int)MathF.Round(bounds.Height * effectScale));
        var blockPx = MathF.Max(1f, protection.BlockSize * effectScale);
        using var surface = SKSurface.Create(new SKImageInfo(
            width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (surface is null)
            return null;
        var boundsToEffect = SKMatrix.CreateScale(effectScale, effectScale)
            .PreConcat(SKMatrix.CreateTranslation(-bounds.X, -bounds.Y));
        surface.Canvas.Clear(SKColors.Black);
        surface.Canvas.SetMatrix(boundsToEffect.PreConcat(source.FrameToNative));
        using (var paint = new SKPaint { IsAntialias = false })
        {
            surface.Canvas.DrawImage(
                source.Frame, 0f, 0f, new SKSamplingOptions(SKFilterMode.Linear), paint);
        }
        using var region = surface.Snapshot();
        using var pixels = SKBitmap.FromImage(region);
        if (pixels is null)
            return null;

        using var result = SKSurface.Create(new SKImageInfo(
            width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (result is null)
            return null;
        var span = pixels.GetPixelSpan();
        var rowBytes = pixels.RowBytes;
        using var fill = new SKPaint { IsAntialias = false, Style = SKPaintStyle.Fill };
        var cellsX = (int)MathF.Ceiling(width / blockPx);
        var cellsY = (int)MathF.Ceiling(height / blockPx);
        for (var cy = 0; cy < cellsY; cy++)
        {
            var y0 = (int)MathF.Round(cy * blockPx);
            var y1 = Math.Min(height, Math.Max(y0 + 1, (int)MathF.Round((cy + 1) * blockPx)));
            if (y0 >= height)
                break;
            for (var cx = 0; cx < cellsX; cx++)
            {
                var x0 = (int)MathF.Round(cx * blockPx);
                var x1 = Math.Min(width, Math.Max(x0 + 1, (int)MathF.Round((cx + 1) * blockPx)));
                if (x0 >= width)
                    break;
                long r = 0, g = 0, b = 0;
                for (var y = y0; y < y1; y++)
                {
                    var row = y * rowBytes;
                    for (var x = x0; x < x1; x++)
                    {
                        var offset = row + (x * 4); // BGRA, opaque frame: premul == straight
                        b += span[offset];
                        g += span[offset + 1];
                        r += span[offset + 2];
                    }
                }
                var count = (long)(x1 - x0) * (y1 - y0);
                fill.Color = new SKColor(
                    (byte)(r / count), (byte)(g / count), (byte)(b / count));
                result.Canvas.DrawRect(SKRect.Create(x0, y0, x1 - x0, y1 - y0), fill);
            }
        }
        return result.Snapshot();
    }

    /// <summary>Gaussian blur at native resolution (capped by <see cref="MaxEffectDim"/>), padded by
    /// the full 3σ so region edges blur into their real neighbors instead of transparency. The
    /// validator's sigma ceiling (80) bounds the padding at 240px.</summary>
    private static SKImage? RenderBlur(ProtectionAnnotation protection, BackgroundSource source)
    {
        var bounds = protection.Bounds;
        var effectScale = MathF.Min(1f, MaxEffectDim / MathF.Max(bounds.Width, bounds.Height));
        var width = Math.Max(1, (int)MathF.Round(bounds.Width * effectScale));
        var height = Math.Max(1, (int)MathF.Round(bounds.Height * effectScale));
        var sigma = MathF.Max(0.1f, protection.BlurSigma * effectScale);
        var pad = (int)MathF.Ceiling(sigma * 3f);
        using var surface = SKSurface.Create(new SKImageInfo(
            width + (pad * 2), height + (pad * 2), SKColorType.Bgra8888, SKAlphaType.Premul));
        if (surface is null)
            return null;
        var boundsToEffect = SKMatrix.CreateTranslation(pad, pad)
            .PreConcat(SKMatrix.CreateScale(effectScale, effectScale))
            .PreConcat(SKMatrix.CreateTranslation(-bounds.X, -bounds.Y));
        surface.Canvas.Clear(SKColors.Black);
        surface.Canvas.SetMatrix(boundsToEffect.PreConcat(source.FrameToNative));
        using (var blur = SKImageFilter.CreateBlur(sigma, sigma))
        using (var paint = new SKPaint { IsAntialias = false, ImageFilter = blur })
        {
            surface.Canvas.DrawImage(
                source.Frame, 0f, 0f, new SKSamplingOptions(SKFilterMode.Linear), paint);
        }
        using var padded = surface.Snapshot();
        return padded.Subset(SKRectI.Create(pad, pad, width, height));
    }

    private static void DrawInk(SKCanvas canvas, InkAnnotation ink, float scale)
    {
        DrawInkPoints(
            canvas, ink.Points, ink.StrokeArgb, ink.StrokeWidth, ink.Opacity, scale);
    }

    private static void DrawInkPoints(
        SKCanvas canvas,
        IReadOnlyList<AnnotationPoint> points,
        uint strokeArgb,
        float strokeWidth,
        float opacity,
        float scale)
    {
        using var paint = StrokePaint(
            strokeArgb, strokeWidth, opacity, scale, SKStrokeCap.Round);
        if (points.Count == 1)
        {
            paint.Style = SKPaintStyle.Fill;
            canvas.DrawCircle(points[0].X, points[0].Y, paint.StrokeWidth / 2f, paint);
            return;
        }
        using var builder = new SKPathBuilder();
        builder.MoveTo(points[0].X, points[0].Y);
        for (var i = 1; i < points.Count; i++)
            builder.LineTo(points[i].X, points[i].Y);
        using var path = builder.Detach();
        canvas.DrawPath(path, paint);
    }

    private static void DrawLine(SKCanvas canvas, LineAnnotation line, float scale)
    {
        using var paint = StrokePaint(line.StrokeArgb, line.StrokeWidth, line.Opacity, scale);
        canvas.DrawLine(line.Start.X, line.Start.Y, line.End.X, line.End.Y, paint);
        DrawArrowhead(canvas, line.Start, line.End, line.StartArrowhead, line.StrokeWidth, paint);
        DrawArrowhead(canvas, line.End, line.Start, line.EndArrowhead, line.StrokeWidth, paint);
    }

    private static void DrawArrowhead(
        SKCanvas canvas,
        AnnotationPoint tip,
        AnnotationPoint other,
        ArrowheadKind kind,
        float logicalStrokeWidth,
        SKPaint paint)
    {
        if (kind == ArrowheadKind.None)
            return;
        var dx = tip.X - other.X;
        var dy = tip.Y - other.Y;
        var length = MathF.Sqrt((dx * dx) + (dy * dy));
        if (length <= 1e-6f)
            return;
        var ux = dx / length;
        var uy = dy / length;
        var size = MathF.Max(8f, logicalStrokeWidth * 4f);
        var halfWidth = size * 0.45f;
        var baseX = tip.X - (ux * size);
        var baseY = tip.Y - (uy * size);
        var left = new SKPoint(baseX - (uy * halfWidth), baseY + (ux * halfWidth));
        var right = new SKPoint(baseX + (uy * halfWidth), baseY - (ux * halfWidth));

        using var builder = new SKPathBuilder();
        builder.MoveTo(tip.X, tip.Y);
        builder.LineTo(left);
        if (kind == ArrowheadKind.Open)
        {
            builder.MoveTo(tip.X, tip.Y);
            builder.LineTo(right);
        }
        else
        {
            builder.LineTo(right);
            builder.Close();
        }
        using var path = builder.Detach();
        var style = paint.Style;
        paint.Style = kind == ArrowheadKind.Triangle ? SKPaintStyle.Fill : SKPaintStyle.Stroke;
        canvas.DrawPath(path, paint);
        paint.Style = style;
    }

    private static void DrawShape(SKCanvas canvas, RectangleAnnotation shape, float scale)
    {
        var rect = SKRect.Create(
            shape.Bounds.X, shape.Bounds.Y, shape.Bounds.Width, shape.Bounds.Height);
        if (shape.FillArgb is { } fillArgb)
        {
            using var fill = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = EffectiveColor(fillArgb, shape.Opacity),
            };
            DrawShapePath(canvas, shape, rect, fill);
        }
        using var stroke = StrokePaint(
            shape.StrokeArgb, shape.StrokeWidth, shape.Opacity, scale);
        DrawShapePath(canvas, shape, rect, stroke);
    }

    private static void DrawShapePath(
        SKCanvas canvas, RectangleAnnotation shape, SKRect rect, SKPaint paint)
    {
        switch (shape.Shape)
        {
            case ShapeKind.RoundedRectangle:
                var radius = MathF.Min(shape.CornerRadius, MathF.Min(rect.Width, rect.Height) / 2f);
                canvas.DrawRoundRect(rect, radius, radius, paint);
                break;
            case ShapeKind.Ellipse:
                canvas.DrawOval(rect, paint);
                break;
            default:
                canvas.DrawRect(rect, paint);
                break;
        }
    }

    private static void DrawText(SKCanvas canvas, TextAnnotation text)
    {
        if (text.BackgroundArgb is { } backgroundArgb)
        {
            using var background = new SKPaint
            {
                Style = SKPaintStyle.Fill,
                Color = EffectiveColor(backgroundArgb, text.Opacity),
            };
            canvas.DrawRect(SKRect.Create(
                text.Bounds.X, text.Bounds.Y, text.Bounds.Width, text.Bounds.Height), background);
        }
        AnnotationTextRenderer.Draw(canvas, text, EffectiveColor(text.ForegroundArgb, text.Opacity));
    }

    /// <summary>FR-ANNO-007: body and tail are boolean-unioned into one path so the outline never
    /// strokes the seam where the tail base crosses into the body (SpeechBubbleGeometry overlaps
    /// the base inward for a stable union).</summary>
    private static void DrawSpeechBubble(SKCanvas canvas, SpeechBubbleAnnotation bubble, float scale)
    {
        var bounds = bubble.Bounds;
        if (bounds.Width < 1f || bounds.Height < 1f)
            return;
        var rect = SKRect.Create(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        var radius = MathF.Max(0f, MathF.Min(
            bubble.CornerRadius, MathF.Min(rect.Width, rect.Height) / 2f));

        using var bodyBuilder = new SKPathBuilder();
        bodyBuilder.AddRoundRect(rect, radius, radius, SKPathDirection.Clockwise);
        using var bodyPath = bodyBuilder.Detach();
        SKPath? unioned = null;
        try
        {
            if (SpeechBubbleGeometry.TryGetTail(bubble, out var baseA, out var baseB, out var tip))
            {
                using var tailBuilder = new SKPathBuilder();
                tailBuilder.MoveTo(baseA.X, baseA.Y);
                tailBuilder.LineTo(tip.X, tip.Y);
                tailBuilder.LineTo(baseB.X, baseB.Y);
                tailBuilder.Close();
                using var tailPath = tailBuilder.Detach();
                unioned = bodyPath.Op(tailPath, SKPathOp.Union);
            }
            var outline = unioned ?? bodyPath;

            using (var fill = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Fill,
                Color = EffectiveColor(bubble.FillArgb, bubble.Opacity),
            })
            {
                canvas.DrawPath(outline, fill);
            }
            using (var stroke = StrokePaint(
                bubble.StrokeArgb, bubble.StrokeWidth, bubble.Opacity, scale))
            {
                canvas.DrawPath(outline, stroke);
            }
        }
        finally
        {
            unioned?.Dispose();
        }

        var padding = 6f + (bubble.StrokeWidth / 2f);
        var textBounds = new RectF(
            bounds.X + padding, bounds.Y + padding,
            MathF.Max(1f, bounds.Width - (padding * 2f)),
            MathF.Max(1f, bounds.Height - (padding * 2f)));
        // The shared text layout path keeps bubble text and plain text metrically identical.
        var layout = new TextAnnotation
        {
            Id = bubble.Id,
            Bounds = textBounds,
            Text = bubble.Text,
            FontFamily = bubble.FontFamily,
            FontSize = bubble.FontSize,
            IsBold = bubble.IsBold,
            IsItalic = bubble.IsItalic,
            ForegroundArgb = bubble.ForegroundArgb,
            Alignment = bubble.Alignment,
            Opacity = bubble.Opacity,
        };
        AnnotationTextRenderer.Draw(
            canvas, layout, EffectiveColor(bubble.ForegroundArgb, bubble.Opacity));
    }

    private static void DrawNumberMarker(SKCanvas canvas, NumberMarkerAnnotation marker)
    {
        var radius = MathF.Min(marker.Bounds.Width, marker.Bounds.Height) / 2f;
        var centerX = marker.Bounds.CenterX;
        var centerY = marker.Bounds.CenterY;
        using var fill = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = EffectiveColor(marker.FillArgb, marker.Opacity),
        };
        canvas.DrawCircle(centerX, centerY, radius, fill);

        using var typeface = SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold)
            ?? SKTypeface.Default;
        using var font = new SKFont(typeface, marker.FontSize);
        using var textPaint = new SKPaint
        {
            IsAntialias = true,
            Color = EffectiveColor(marker.ForegroundArgb, marker.Opacity),
        };
        var value = marker.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var metrics = font.Metrics;
        var baseline = centerY - ((metrics.Ascent + metrics.Descent) / 2f);
        canvas.DrawText(value, centerX, baseline, SKTextAlign.Center, font, textPaint);
    }

    private static void DrawImage(
        SKCanvas canvas, ImageAnnotation annotation, RasterAssetImageCache? assetCache)
    {
        if (assetCache?.Find(annotation.AssetId) is not { } image)
            return;
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Color = SKColors.White.WithAlpha(
                (byte)Math.Clamp(MathF.Round(annotation.Opacity * 255f), 0f, 255f)),
        };
        canvas.DrawImage(
            image,
            SKRect.Create(
                annotation.Bounds.X, annotation.Bounds.Y,
                annotation.Bounds.Width, annotation.Bounds.Height),
            new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), paint);
    }

    private static SKPaint StrokePaint(
        uint argb,
        float nativeWidth,
        float opacity,
        float scale,
        SKStrokeCap cap = SKStrokeCap.Square) => new()
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeCap = cap,
            StrokeJoin = SKStrokeJoin.Round,
            StrokeWidth = nativeWidth * scale < 1f ? 1f / scale : nativeWidth,
            Color = EffectiveColor(argb, opacity),
        };

    private static SKColor EffectiveColor(uint argb, float opacity)
    {
        var color = new SKColor(argb);
        var alpha = (byte)Math.Clamp(MathF.Round(color.Alpha * opacity), 0f, 255f);
        return color.WithAlpha(alpha);
    }

    public static void DrawSelection(SKCanvas canvas, Annotation annotation, SKMatrix nativeToView)
    {
        using var marquee = new SKPaint
        {
            IsAntialias = false,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            Color = new SKColor(0xFF, 0xFF, 0xFF, 0xC0),
            PathEffect = SelectionDash,
        };
        using var quad = ToViewQuad(
            annotation.Bounds, annotation.RotationDegrees, nativeToView);
        canvas.DrawPath(quad, marquee);

        var scale = AverageScale(nativeToView);
        var rotationOffset = 24f / scale;
        // Protection regions never rotate (ADR-0015): no rotate affordance is offered at all.
        var canRotate = annotation is not ProtectionAnnotation;
        if (canRotate)
        {
            var topNative = SelectionGeometry.HandlePoint(
                annotation, SelectionHandle.North, rotationOffset);
            var rotateNative = SelectionGeometry.HandlePoint(
                annotation, SelectionHandle.Rotate, rotationOffset);
            var top = nativeToView.MapPoint(topNative.X, topNative.Y);
            var rotate = nativeToView.MapPoint(rotateNative.X, rotateNative.Y);
            using var connector = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1f,
                Color = new SKColor(0xFF, 0xFF, 0xFF, 0xC0),
            };
            canvas.DrawLine(top, rotate, connector);
        }
        using var handlePaint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            Color = SKColors.White,
        };
        using var handleStroke = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1f,
            Color = new SKColor(0x00, 0x78, 0xD4),
        };
        foreach (var handle in Enum.GetValues<SelectionHandle>())
        {
            if (handle == SelectionHandle.None
                || (handle == SelectionHandle.Rotate && !canRotate)
                || !SelectionGeometry.HandleApplies(annotation, handle))
                continue;
            var native = SelectionGeometry.HandlePoint(annotation, handle, rotationOffset);
            var view = nativeToView.MapPoint(native.X, native.Y);
            canvas.DrawCircle(view, 4f, handlePaint);
            canvas.DrawCircle(view, 4f, handleStroke);
        }
    }

    private static SKPath ToViewQuad(RectF bounds, float rotationDegrees, SKMatrix matrix)
    {
        var corners = new[]
        {
            new AnnotationPoint(bounds.X, bounds.Y),
            new AnnotationPoint(bounds.Right, bounds.Y),
            new AnnotationPoint(bounds.Right, bounds.Bottom),
            new AnnotationPoint(bounds.X, bounds.Bottom),
        };
        using var builder = new SKPathBuilder();
        for (var i = 0; i < corners.Length; i++)
        {
            var point = Rotate(corners[i], bounds, rotationDegrees);
            var mapped = matrix.MapPoint(point.X, point.Y);
            if (i == 0)
                builder.MoveTo(mapped);
            else
                builder.LineTo(mapped);
        }
        builder.Close();
        return builder.Detach();
    }

    private static AnnotationPoint Rotate(AnnotationPoint point, RectF bounds, float degrees)
    {
        if (degrees == 0f)
            return point;
        var radians = degrees * (MathF.PI / 180f);
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        var x = point.X - bounds.CenterX;
        var y = point.Y - bounds.CenterY;
        return new AnnotationPoint(
            bounds.CenterX + (x * cos) - (y * sin),
            bounds.CenterY + (x * sin) + (y * cos));
    }

    private static float AverageScale(SKMatrix matrix)
    {
        var x = MathF.Sqrt((matrix.ScaleX * matrix.ScaleX) + (matrix.SkewY * matrix.SkewY));
        var y = MathF.Sqrt((matrix.ScaleY * matrix.ScaleY) + (matrix.SkewX * matrix.SkewX));
        var scale = MathF.Sqrt(x * y);
        return scale > 0f ? scale : 1f;
    }
}
