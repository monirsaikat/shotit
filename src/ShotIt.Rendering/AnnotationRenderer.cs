using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShotIt.Core.Annotations;
using ShotIt.Core.Editing;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;

namespace ShotIt.Rendering;

/// <summary>
/// Draws a document's annotations with WPF. One drawing unit equals one image pixel; callers apply
/// their own view transform. The same code path renders the live editor and the exported file,
/// so what the user sees is exactly what gets saved.
/// </summary>
public sealed class AnnotationRenderer
{
    private sealed record EffectCache(Annotation Key, PixelBuffer Base, BitmapSource Bitmap, Rect Target);

    private readonly Dictionary<Guid, EffectCache> _effects = [];
    private static readonly ConditionalWeakTable<byte[], BitmapSource> DecodedImages = [];

    /// <summary>Draws the base image (optional) and every annotation, plus the annotation being drawn.</summary>
    /// <param name="dc">Target drawing context, in document pixel units.</param>
    /// <param name="state">The document.</param>
    /// <param name="draft">Annotation in progress, drawn on top. May be null.</param>
    /// <param name="area">The part of the document that counts as "the image" for spotlights; null = everything.</param>
    /// <param name="drawBase">False when the host already shows the base image (the capture overlay).</param>
    public void Render(DrawingContext dc, DocState state, Annotation? draft = null, RectD? area = null, bool drawBase = true)
    {
        ArgumentNullException.ThrowIfNull(dc);
        ArgumentNullException.ThrowIfNull(state);

        var full = new Rect(0, 0, state.Base.Width, state.Base.Height);
        if (drawBase) dc.DrawImage(state.Base.ToCachedBitmap(), full);

        IEnumerable<Annotation> all = draft is null ? state.Items : state.Items.Append(draft);
        var items = all as IReadOnlyCollection<Annotation> ?? [.. all];
        PruneCache(items);

        // Pass 1: effects that rewrite image pixels. They sit directly on the image, below all markup.
        foreach (var a in items)
        {
            switch (a)
            {
                case BlurAnnotation or PixelateAnnotation or HighlighterAnnotation or AreaHighlightAnnotation:
                    DrawEffect(dc, a, state.Base);
                    break;
            }
        }

        // Pass 2: one dim layer with a hole for every spotlight.
        DrawSpotlights(dc, items, area?.ToWpf() ?? full);

        // Pass 3: vector markup in document order.
        foreach (var a in items) DrawVector(dc, a, state.Base);
    }

    private void PruneCache(IReadOnlyCollection<Annotation> items)
    {
        if (_effects.Count == 0) return;
        var alive = items.Select(i => i.Id).ToHashSet();
        foreach (var id in _effects.Keys.Where(k => !alive.Contains(k)).ToList()) _effects.Remove(id);
    }

    // ------------------------------------------------------------------ pixel effects

    private void DrawEffect(DrawingContext dc, Annotation a, PixelBuffer image)
    {
        if (!_effects.TryGetValue(a.Id, out var cached) || !cached.Key.Equals(a) || !ReferenceEquals(cached.Base, image))
        {
            var built = BuildEffect(a, image);
            if (built is null)
            {
                _effects.Remove(a.Id);
                return;
            }

            cached = new EffectCache(a, image, built.Value.Bitmap, built.Value.Target);
            _effects[a.Id] = cached;
        }

        dc.DrawImage(cached.Bitmap, cached.Target);
    }

    private static (BitmapSource Bitmap, Rect Target)? BuildEffect(Annotation a, PixelBuffer image)
    {
        switch (a)
        {
            case BlurAnnotation blur:
            {
                var px = blur.Rect.ToPixelRectOuter().Intersect(image.Bounds);
                if (px.IsEmpty) return null;
                var crop = image.Crop(px);
                int radius = Math.Max(1, (int)Math.Round(blur.Radius));
                // Blurring a reduced copy is visually identical and keeps dragging smooth on large regions (P37).
                int factor = Math.Clamp(radius / 4, 1, 8);
                PixelBuffer result = factor > 1
                    ? ImageEffects.Blur(ImageEffects.Resize(crop, Math.Max(1, (crop.Width + factor - 1) / factor), Math.Max(1, (crop.Height + factor - 1) / factor)), Math.Max(1, radius / factor))
                    : ImageEffects.Blur(crop, radius);
                return (result.ToBitmapSource(), new Rect(px.X, px.Y, px.Width, px.Height));
            }

            case PixelateAnnotation pixelate:
            {
                var px = pixelate.Rect.ToPixelRectOuter().Intersect(image.Bounds);
                if (px.IsEmpty) return null;
                var result = ImageEffects.Pixelate(image.Crop(px), Math.Max(2, pixelate.BlockSize));
                return (result.ToBitmapSource(), new Rect(px.X, px.Y, px.Width, px.Height));
            }

            case HighlighterAnnotation marker:
            {
                if (marker.Points.Length == 0) return null;
                var px = marker.Bounds.Inflate(marker.StrokeWidth / 2 + 2).ToPixelRectOuter().Intersect(image.Bounds);
                if (px.IsEmpty) return null;
                var pen = new Pen(Brushes.White, marker.StrokeWidth) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                byte[] coverage = RenderCoverage(px, dc => DrawPath(dc, marker.Points, pen, smooth: true));
                var result = ImageEffects.MultiplyTint(image.Crop(px), coverage, marker.Color);
                return (result.ToBitmapSource(), new Rect(px.X, px.Y, px.Width, px.Height));
            }

            case AreaHighlightAnnotation area:
            {
                var px = area.Rect.ToPixelRectOuter().Intersect(image.Bounds);
                if (px.IsEmpty) return null;
                byte[] coverage;
                if (area.Ellipse)
                {
                    coverage = RenderCoverage(px, dc => dc.DrawEllipse(Brushes.White, null, area.Rect.Center.ToWpf(), area.Rect.Width / 2, area.Rect.Height / 2));
                }
                else
                {
                    coverage = new byte[px.Width * px.Height];
                    Array.Fill(coverage, (byte)255);
                }

                var result = ImageEffects.MultiplyTint(image.Crop(px), coverage, area.Color);
                return (result.ToBitmapSource(), new Rect(px.X, px.Y, px.Width, px.Height));
            }

            default:
                return null;
        }
    }

    /// <summary>Rasterises a shape into an 8-bit coverage mask for the given pixel rectangle.</summary>
    private static byte[] RenderCoverage(PixelRect px, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(-px.X, -px.Y));
            draw(dc);
            dc.Pop();
        }

        var target = new RenderTargetBitmap(px.Width, px.Height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        byte[] pixels = new byte[px.Width * px.Height * 4];
        target.CopyPixels(pixels, px.Width * 4, 0);
        byte[] coverage = new byte[px.Width * px.Height];
        for (int i = 0; i < coverage.Length; i++) coverage[i] = pixels[i * 4 + 3];
        return coverage;
    }

    private static void DrawSpotlights(DrawingContext dc, IReadOnlyCollection<Annotation> items, Rect area)
    {
        GeometryGroup? holes = null;
        double dim = 0;
        foreach (var s in items.OfType<SpotlightAnnotation>())
        {
            holes ??= new GeometryGroup { FillRule = FillRule.Nonzero };
            holes.Children.Add(s.Ellipse ? new EllipseGeometry(s.Rect.ToWpf()) : new RectangleGeometry(s.Rect.ToWpf()));
            dim = Math.Max(dim, s.DimOpacity);
        }

        if (holes is null) return;
        var shade = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(area), holes);
        var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(dim * 255, 0, 255), 0, 0, 0));
        dc.DrawGeometry(brush, null, shade);
    }

    // ------------------------------------------------------------------ vector markup

    private static Pen MakePen(ColorRgba color, double width, bool dashed = false, PenLineCap cap = PenLineCap.Round, PenLineJoin join = PenLineJoin.Round)
    {
        var pen = new Pen(color.ToBrush(), Math.Max(0.5, width)) { StartLineCap = cap, EndLineCap = cap, LineJoin = join };
        if (dashed)
        {
            pen.DashStyle = new DashStyle([3, 2.5], 0);
            pen.DashCap = PenLineCap.Flat;
        }

        return pen;
    }

    private static void DrawPath(DrawingContext dc, IReadOnlyList<Vec2> points, Pen pen, bool smooth)
    {
        if (points.Count == 0) return;
        if (points.Count == 1)
        {
            dc.DrawEllipse(pen.Brush, null, points[0].ToWpf(), pen.Thickness / 2, pen.Thickness / 2);
            return;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0].ToWpf(), false, false);
            if (smooth && points.Count > 2)
            {
                // Quadratic segments through the midpoints turn jittery mouse input into a smooth stroke.
                for (int i = 1; i < points.Count - 1; i++)
                    ctx.QuadraticBezierTo(points[i].ToWpf(), Vec2.Lerp(points[i], points[i + 1], 0.5).ToWpf(), true, true);
                ctx.LineTo(points[^1].ToWpf(), true, true);
            }
            else
            {
                for (int i = 1; i < points.Count; i++) ctx.LineTo(points[i].ToWpf(), true, true);
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private void DrawVector(DrawingContext dc, Annotation a, PixelBuffer image)
    {
        switch (a)
        {
            case RedactAnnotation redact:
                // Drawn in document order so it also hides markup that was added before it.
                dc.DrawRectangle(redact.Color.ToBrush(), null, redact.Rect.ToWpf());
                break;

            case ArrowAnnotation arrow:
                DrawArrow(dc, arrow);
                break;

            case LineAnnotation line:
                DrawPath(dc, line.Points, MakePen(line.Color, line.StrokeWidth, line.Dashed), smooth: false);
                break;

            case FreehandAnnotation pen:
                DrawPath(dc, pen.Points, MakePen(pen.Color, pen.StrokeWidth), smooth: true);
                break;

            case ShapeAnnotation shape:
            {
                var pen = MakePen(shape.Color, shape.StrokeWidth, shape.Dashed, PenLineCap.Flat, PenLineJoin.Miter);
                Brush? fill = shape.Fill?.ToBrush();
                var rect = shape.Rect.ToWpf();
                switch (shape.Kind)
                {
                    case ShapeKind.Ellipse:
                        dc.DrawEllipse(fill, pen, shape.Rect.Center.ToWpf(), rect.Width / 2, rect.Height / 2);
                        break;
                    case ShapeKind.RoundedRectangle:
                        double radius = Math.Min(shape.CornerRadius, Math.Min(rect.Width, rect.Height) / 2);
                        dc.DrawRoundedRectangle(fill, pen, rect, radius, radius);
                        break;
                    default:
                        dc.DrawRectangle(fill, pen, rect);
                        break;
                }

                break;
            }

            case TextAnnotation text:
                DrawText(dc, text);
                break;

            case CalloutAnnotation callout:
                DrawCallout(dc, callout);
                break;

            case StepAnnotation step:
                DrawStep(dc, step);
                break;

            case MagnifierAnnotation magnifier:
                DrawMagnifier(dc, magnifier, image);
                break;

            case StickerAnnotation sticker:
                Stickers.Draw(dc, sticker.Glyph, sticker.Rect.ToWpf());
                break;

            case ImageAnnotation picture:
                if (Decode(picture.EncodedImage) is { } bitmap) dc.DrawImage(bitmap, picture.Rect.ToWpf());
                break;

            case MeasureAnnotation measure:
                DrawMeasure(dc, measure);
                break;
        }
    }

    private static BitmapSource? Decode(byte[] data)
    {
        if (data.Length == 0) return null;
        if (DecodedImages.TryGetValue(data, out var cached)) return cached;
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            frame.Freeze();
            DecodedImages.AddOrUpdate(data, frame);
            return frame;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or ArgumentException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    private static void DrawArrow(DrawingContext dc, ArrowAnnotation arrow)
    {
        double width = Math.Max(1, arrow.StrokeWidth);
        double headLength = Math.Max(12, width * 4.2), headWidth = Math.Max(9, width * 3.2);
        double length = arrow.Start.DistanceTo(arrow.End);
        if (length < 0.5) return;
        headLength = Math.Min(headLength, length * 0.6);
        headWidth = Math.Min(headWidth, headLength * 1.1);

        Vec2 control = arrow.BezierControl;
        bool curved = arrow.Control is not null;
        Vec2 endDirection = (arrow.End - (curved ? control : arrow.Start)).Normalized();
        Vec2 startDirection = (arrow.Start - (curved ? control : arrow.End)).Normalized();
        if (endDirection.LengthSquared < 0.5) endDirection = (arrow.End - arrow.Start).Normalized();
        if (startDirection.LengthSquared < 0.5) startDirection = (arrow.Start - arrow.End).Normalized();

        // Stop the shaft inside the head so its square end never pokes out of the tip.
        Vec2 shaftEnd = arrow.End - endDirection * (headLength * 0.75);
        Vec2 shaftStart = arrow.DoubleHeaded ? arrow.Start - startDirection * (headLength * 0.75) : arrow.Start;

        var pen = MakePen(arrow.Color, width, arrow.Dashed, PenLineCap.Flat);
        if (!arrow.DoubleHeaded) pen.StartLineCap = PenLineCap.Round;
        var shaft = new StreamGeometry();
        using (var ctx = shaft.Open())
        {
            ctx.BeginFigure(shaftStart.ToWpf(), false, false);
            if (curved) ctx.QuadraticBezierTo(control.ToWpf(), shaftEnd.ToWpf(), true, true);
            else ctx.LineTo(shaftEnd.ToWpf(), true, true);
        }

        shaft.Freeze();
        dc.DrawGeometry(null, pen, shaft);

        var brush = arrow.Color.ToBrush();
        DrawHead(dc, brush, arrow.End, endDirection, headLength, headWidth);
        if (arrow.DoubleHeaded) DrawHead(dc, brush, arrow.Start, startDirection, headLength, headWidth);
    }

    private static void DrawHead(DrawingContext dc, Brush brush, Vec2 tip, Vec2 direction, double length, double width)
    {
        Vec2 back = tip - direction * length;
        Vec2 side = direction.Perpendicular() * (width / 2);
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(tip.ToWpf(), true, true);
            ctx.LineTo((back + side).ToWpf(), true, false);
            // A slight notch makes the head read as an arrow rather than a plain triangle.
            ctx.LineTo((back + direction * (length * 0.18)).ToWpf(), true, false);
            ctx.LineTo((back - side).ToWpf(), true, false);
        }

        head.Freeze();
        dc.DrawGeometry(brush, null, head);
    }

    private static void DrawText(DrawingContext dc, TextAnnotation text)
    {
        var bounds = text.Bounds.ToWpf();
        if (text.Background is { } background)
            dc.DrawRoundedRectangle(background.ToBrush(), null, bounds, 5, 5);

        var formatted = TextLayout.Create(text.Text, text.FontFamily, text.FontSize, text.Bold, text.Italic, text.Color.ToBrush());
        var origin = new Point(text.Position.X + TextAnnotation.Padding, text.Position.Y + TextAnnotation.Padding);
        if (text.Outline is { } outline)
        {
            var pen = new Pen(outline.ToBrush(), Math.Max(2, text.FontSize / 8)) { LineJoin = PenLineJoin.Round };
            dc.DrawGeometry(null, pen, formatted.BuildGeometry(origin));
        }

        dc.DrawText(formatted, origin);
    }

    private static void DrawCallout(DrawingContext dc, CalloutAnnotation callout)
    {
        var box = callout.Box.ToWpf();
        if (box.Width < 1 || box.Height < 1) return;
        double radius = Math.Min(callout.CornerRadius, Math.Min(box.Width, box.Height) / 2);
        Geometry shape = new RectangleGeometry(box, radius, radius);

        if (!callout.Box.Contains(callout.Tail))
        {
            Vec2 center = callout.Box.Center;
            Vec2 direction = (callout.Tail - center).Normalized();
            Vec2 side = direction.Perpendicular() * Math.Max(6, Math.Min(box.Width, box.Height) * 0.18);
            var tail = new StreamGeometry();
            using (var ctx = tail.Open())
            {
                ctx.BeginFigure((center + side).ToWpf(), true, true);
                ctx.LineTo(callout.Tail.ToWpf(), true, true);
                ctx.LineTo((center - side).ToWpf(), true, true);
            }

            shape = new CombinedGeometry(GeometryCombineMode.Union, shape, tail);
        }

        var pen = callout.StrokeWidth > 0 ? MakePen(callout.Stroke, callout.StrokeWidth, false, PenLineCap.Round, PenLineJoin.Round) : null;
        dc.DrawGeometry(callout.Fill.ToBrush(), pen, shape);

        if (callout.Text.Length == 0) return;
        const double pad = 8;
        var formatted = TextLayout.Create(callout.Text, callout.FontFamily, callout.FontSize, true, false, callout.TextColor.ToBrush());
        formatted.MaxTextWidth = Math.Max(1, box.Width - 2 * pad);
        formatted.MaxTextHeight = Math.Max(1, box.Height - pad);
        formatted.TextAlignment = TextAlignment.Center;
        formatted.Trimming = TextTrimming.CharacterEllipsis;
        dc.DrawText(formatted, new Point(box.X + pad, box.Y + Math.Max(pad / 2, (box.Height - formatted.Height) / 2)));
    }

    private static void DrawStep(DrawingContext dc, StepAnnotation step)
    {
        var center = step.Center.ToWpf();
        var ring = new Pen(step.TextColor.ToBrush(), Math.Max(1.5, step.Radius / 9));
        dc.DrawEllipse(step.Color.ToBrush(), ring, center, step.Radius, step.Radius);
        string label = step.Number.ToString(CultureInfo.InvariantCulture);
        double size = step.Radius * (label.Length > 2 ? 0.8 : label.Length > 1 ? 1.0 : 1.2);
        var formatted = TextLayout.Create(label, "Segoe UI", size, true, false, step.TextColor.ToBrush());
        dc.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    private static void DrawMagnifier(DrawingContext dc, MagnifierAnnotation m, PixelBuffer image)
    {
        var source = m.Source.ToWpf();
        var dest = m.Dest.ToWpf();
        if (source.Width < 1 || source.Height < 1 || dest.Width < 1 || dest.Height < 1) return;
        var pen = MakePen(m.Border, m.BorderWidth, false, PenLineCap.Flat, PenLineJoin.Miter);
        var thin = MakePen(m.Border, Math.Max(1, m.BorderWidth / 2), false, PenLineCap.Flat, PenLineJoin.Miter);

        // Connector lines between the facing corners.
        if (dest.Left >= source.Right || dest.Right <= source.Left)
        {
            bool right = dest.Left >= source.Right;
            double sx = right ? source.Right : source.Left, dx = right ? dest.Left : dest.Right;
            dc.DrawLine(thin, new Point(sx, source.Top), new Point(dx, dest.Top));
            dc.DrawLine(thin, new Point(sx, source.Bottom), new Point(dx, dest.Bottom));
        }
        else if (dest.Top >= source.Bottom || dest.Bottom <= source.Top)
        {
            bool below = dest.Top >= source.Bottom;
            double sy = below ? source.Bottom : source.Top, dy = below ? dest.Top : dest.Bottom;
            dc.DrawLine(thin, new Point(source.Left, sy), new Point(dest.Left, dy));
            dc.DrawLine(thin, new Point(source.Right, sy), new Point(dest.Right, dy));
        }

        dc.DrawRectangle(null, thin, source);

        // Nearest-neighbour scaling keeps magnified pixels crisp instead of smearing them.
        var zoom = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(zoom, BitmapScalingMode.NearestNeighbor);
        using (var z = zoom.Open())
        {
            var brush = new ImageBrush(image.ToCachedBitmap())
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = Rect.Intersect(source, new Rect(0, 0, image.Width, image.Height)) is { IsEmpty: false } visible ? visible : source,
                Stretch = Stretch.Fill,
            };
            if (m.Round) z.DrawEllipse(brush, null, m.Dest.Center.ToWpf(), dest.Width / 2, dest.Height / 2);
            else z.DrawRectangle(brush, null, dest);
        }

        dc.DrawDrawing(zoom);
        if (m.Round) dc.DrawEllipse(null, pen, m.Dest.Center.ToWpf(), dest.Width / 2, dest.Height / 2);
        else dc.DrawRectangle(null, pen, dest);
    }

    private static void DrawMeasure(DrawingContext dc, MeasureAnnotation m)
    {
        var pen = MakePen(m.Color, 2, false, PenLineCap.Flat);
        dc.DrawLine(pen, m.Start.ToWpf(), m.End.ToWpf());
        Vec2 direction = (m.End - m.Start).Normalized();
        if (direction.LengthSquared < 0.5) return;
        Vec2 tick = direction.Perpendicular() * 6;
        dc.DrawLine(pen, (m.Start + tick).ToWpf(), (m.Start - tick).ToWpf());
        dc.DrawLine(pen, (m.End + tick).ToWpf(), (m.End - tick).ToWpf());

        string label = string.Create(CultureInfo.InvariantCulture, $"{m.Length:0} px   {Math.Abs(m.End.X - m.Start.X):0} × {Math.Abs(m.End.Y - m.Start.Y):0}   {m.AngleDegrees:0.#}°");
        var formatted = TextLayout.Create(label, "Segoe UI", 12, true, false, Brushes.White);
        Vec2 mid = Vec2.Lerp(m.Start, m.End, 0.5);
        var box = new Rect(mid.X - formatted.Width / 2 - 6, mid.Y - formatted.Height - 14, formatted.Width + 12, formatted.Height + 6);
        dc.DrawRoundedRectangle(m.Color.ToBrush(), null, box, 4, 4);
        dc.DrawText(formatted, new Point(box.X + 6, box.Y + 3));
    }
}

/// <summary>Built-in vector stickers. Drawn as shapes because WPF renders emoji in monochrome.</summary>
public static class Stickers
{
    public static IReadOnlyList<string> Names { get; } = ["check", "cross", "warning", "info", "question", "star", "heart", "pointer"];

    public static void Draw(DrawingContext dc, string glyph, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(dc);
        if (rect.Width < 1 || rect.Height < 1) return;
        double s = Math.Min(rect.Width, rect.Height);
        var origin = new Point(rect.X + (rect.Width - s) / 2, rect.Y + (rect.Height - s) / 2);
        Point P(double x, double y) => new(origin.X + x * s, origin.Y + y * s);
        var center = P(0.5, 0.5);
        var white = new Pen(Brushes.White, s * 0.11) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };

        switch (glyph)
        {
            case "check":
                dc.DrawEllipse(Brush(0x2E, 0xB8, 0x5C), null, center, s / 2, s / 2);
                dc.DrawGeometry(null, white, Polyline(false, P(0.27, 0.52), P(0.44, 0.69), P(0.74, 0.34)));
                break;
            case "cross":
                dc.DrawEllipse(Brush(0xE8, 0x1C, 0x2E), null, center, s / 2, s / 2);
                dc.DrawLine(white, P(0.32, 0.32), P(0.68, 0.68));
                dc.DrawLine(white, P(0.68, 0.32), P(0.32, 0.68));
                break;
            case "warning":
            {
                var yellow = Brush(0xFF, 0xC1, 0x07);
                var edge = new Pen(yellow, s * 0.1) { LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(yellow, edge, Polyline(true, P(0.5, 0.12), P(0.92, 0.86), P(0.08, 0.86)));
                var dark = new Pen(Brush(0x20, 0x20, 0x20), s * 0.1) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(dark, P(0.5, 0.36), P(0.5, 0.6));
                dc.DrawEllipse(dark.Brush, null, P(0.5, 0.75), s * 0.055, s * 0.055);
                break;
            }
            case "info":
                dc.DrawEllipse(Brush(0x1E, 0x88, 0xE5), null, center, s / 2, s / 2);
                dc.DrawLine(white, P(0.5, 0.45), P(0.5, 0.74));
                dc.DrawEllipse(Brushes.White, null, P(0.5, 0.28), s * 0.06, s * 0.06);
                break;
            case "question":
            {
                dc.DrawEllipse(Brush(0x7E, 0x57, 0xC2), null, center, s / 2, s / 2);
                var text = TextLayout.Create("?", "Segoe UI", s * 0.62, true, false, Brushes.White);
                dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
                break;
            }
            case "star":
            {
                var points = new Point[10];
                for (int i = 0; i < 10; i++)
                {
                    double angle = -Math.PI / 2 + i * Math.PI / 5, radius = i % 2 == 0 ? 0.5 : 0.21;
                    points[i] = P(0.5 + Math.Cos(angle) * radius, 0.53 + Math.Sin(angle) * radius);
                }

                dc.DrawGeometry(Brush(0xFF, 0xB3, 0x00), null, Polyline(true, points));
                break;
            }
            case "heart":
            {
                var heart = new StreamGeometry();
                using (var ctx = heart.Open())
                {
                    ctx.BeginFigure(P(0.5, 0.9), true, true);
                    ctx.BezierTo(P(0.0, 0.55), P(0.08, 0.1), P(0.5, 0.3), true, true);
                    ctx.BezierTo(P(0.92, 0.1), P(1.0, 0.55), P(0.5, 0.9), true, true);
                }

                dc.DrawGeometry(Brush(0xE8, 0x1C, 0x2E), null, heart);
                break;
            }
            case "pointer":
            {
                var outline = new Pen(Brushes.White, s * 0.06) { LineJoin = PenLineJoin.Round };
                dc.DrawGeometry(Brush(0x20, 0x20, 0x20), outline, Polyline(true,
                    P(0.2, 0.08), P(0.2, 0.82), P(0.39, 0.64), P(0.52, 0.93), P(0.64, 0.88), P(0.51, 0.59), P(0.77, 0.59)));
                break;
            }
            default:
            {
                var text = TextLayout.Create(string.IsNullOrEmpty(glyph) ? "?" : glyph, "Segoe UI Emoji", s * 0.7, false, false, Brush(0x20, 0x20, 0x20));
                dc.DrawText(text, new Point(center.X - text.Width / 2, center.Y - text.Height / 2));
                break;
            }
        }
    }

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static StreamGeometry Polyline(bool closed, params Point[] points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], closed, closed);
            for (int i = 1; i < points.Length; i++) ctx.LineTo(points[i], true, true);
        }

        geometry.Freeze();
        return geometry;
    }
}

/// <summary>Turns a document into final pixels: annotations burnt in, optional frame and watermark applied.</summary>
public static class DocumentRenderer
{
    /// <summary>The image with all annotations applied, at the document's own size.</summary>
    public static PixelBuffer Flatten(DocState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Items.IsEmpty) return state.Base;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) new AnnotationRenderer().Render(dc, state);
        return Rasterize(visual, state.Base.Width, state.Base.Height);
    }

    /// <summary>What gets saved, copied or uploaded: flattened image plus canvas styling and watermark.</summary>
    public static PixelBuffer RenderFinal(DocState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var flat = Flatten(state);
        var canvas = state.Canvas;
        bool hasWatermark = state.Watermark is { Text.Length: > 0 };
        if (canvas.IsNone && !hasWatermark) return flat;

        double pad = Math.Max(canvas.Padding, canvas.Shadow ? 28 : 0);
        int width = flat.Width + (int)Math.Round(pad * 2), height = flat.Height + (int)Math.Round(pad * 2);
        var imageRect = new Rect(Math.Round(pad), Math.Round(pad), flat.Width, flat.Height);
        double radius = Math.Min(canvas.CornerRadius, Math.Min(flat.Width, flat.Height) / 2.0);

        var root = new ContainerVisual();

        if (canvas.Background != BackgroundKind.None)
        {
            var backdrop = new DrawingVisual();
            Brush fill = canvas.Background == BackgroundKind.Gradient
                ? new LinearGradientBrush(canvas.Color1.ToWpf(), canvas.Color2.ToWpf(), new Point(0, 0), new Point(1, 1))
                : canvas.Color1.ToBrush();
            using (var dc = backdrop.RenderOpen()) dc.DrawRectangle(fill, null, new Rect(0, 0, width, height));
            root.Children.Add(backdrop);
        }

        if (canvas.Shadow)
        {
            var shadow = new DrawingVisual { Effect = new System.Windows.Media.Effects.BlurEffect { Radius = Math.Max(10, pad * 0.55), RenderingBias = System.Windows.Media.Effects.RenderingBias.Quality } };
            var rect = imageRect;
            rect.Offset(0, Math.Max(3, pad * 0.18));
            using (var dc = shadow.RenderOpen())
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), null, rect, radius, radius);
            root.Children.Add(shadow);
        }

        var picture = new DrawingVisual();
        using (var dc = picture.RenderOpen())
        {
            if (radius > 0) dc.PushClip(new RectangleGeometry(imageRect, radius, radius));
            dc.DrawImage(flat.ToBitmapSource(), imageRect);
            if (radius > 0) dc.Pop();

            if (hasWatermark)
            {
                var mark = state.Watermark!;
                var color = mark.Color.WithAlpha((byte)Math.Clamp(mark.Opacity * 255, 0, 255));
                var text = TextLayout.Create(mark.Text, "Segoe UI", mark.FontSize, true, false, color.ToBrush());
                const double margin = 12;
                double x = mark.Position switch
                {
                    WatermarkPosition.TopLeft or WatermarkPosition.BottomLeft => imageRect.Left + margin,
                    WatermarkPosition.Center => imageRect.Left + (imageRect.Width - text.Width) / 2,
                    _ => imageRect.Right - text.Width - margin,
                };
                double y = mark.Position switch
                {
                    WatermarkPosition.TopLeft or WatermarkPosition.TopRight => imageRect.Top + margin,
                    WatermarkPosition.Center => imageRect.Top + (imageRect.Height - text.Height) / 2,
                    _ => imageRect.Bottom - text.Height - margin,
                };
                var shade = new SolidColorBrush(Color.FromArgb((byte)(color.A * 0.6), 0, 0, 0));
                dc.DrawText(TextLayout.Create(mark.Text, "Segoe UI", mark.FontSize, true, false, shade), new Point(x + 1, y + 1));
                dc.DrawText(text, new Point(x, y));
            }
        }

        root.Children.Add(picture);
        return Rasterize(root, width, height);
    }

    private static PixelBuffer Rasterize(Visual visual, int width, int height)
    {
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return target.ToPixelBuffer();
    }
}
