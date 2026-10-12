using System.IO;
using System.Runtime.ExceptionServices;
using ShotIt.Core.Annotations;
using ShotIt.Core.Editing;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using ShotIt.Core.Settings;
using ShotIt.Rendering;

namespace ShotIt.Rendering.Tests;

/// <summary>WPF rendering needs a single-threaded apartment; xunit runs tests on MTA pool threads.</summary>
internal static class Sta
{
    public static void Run(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                WpfTextMeasurer.Install();
                action();
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}

public class RenderingTests
{
    private const int W = 320, H = 240;

    private static PixelBuffer White()
    {
        var b = new PixelBuffer(W, H);
        b.Fill(ColorRgba.White);
        return b;
    }

    private static DocState Doc(params Annotation[] items) => new(White(), [.. items]);

    private static PixelRect ChangedBounds(PixelBuffer before, PixelBuffer after)
    {
        int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
        for (int y = 0; y < before.Height; y++)
        {
            for (int x = 0; x < before.Width; x++)
            {
                if (before.GetPixel(x, y) == after.GetPixel(x, y)) continue;
                l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y);
            }
        }

        return r < 0 ? PixelRect.Empty : PixelRect.FromLTRB(l, t, r + 1, b + 1);
    }

    public static IEnumerable<object[]> LocalAnnotations() => new Annotation[]
    {
        new ArrowAnnotation { Start = new(60, 60), End = new(200, 150), StrokeWidth = 5 },
        new ArrowAnnotation { Start = new(60, 60), End = new(200, 150), Control = new Vec2(100, 150), DoubleHeaded = true },
        new LineAnnotation { Points = [new(60, 60), new(200, 150)], Dashed = true },
        new ShapeAnnotation { Rect = new(60, 60, 140, 90), Kind = ShapeKind.Rectangle },
        new ShapeAnnotation { Rect = new(60, 60, 140, 90), Kind = ShapeKind.RoundedRectangle, Fill = ColorRgba.Red.WithAlpha(70) },
        new ShapeAnnotation { Rect = new(60, 60, 140, 90), Kind = ShapeKind.Ellipse },
        new FreehandAnnotation { Points = [new(60, 60), new(90, 120), new(150, 80), new(200, 150)] },
        new HighlighterAnnotation { Points = [new(70, 100), new(190, 100)] },
        new AreaHighlightAnnotation { Rect = new(60, 60, 140, 90) },
        new AreaHighlightAnnotation { Rect = new(60, 60, 140, 90), Ellipse = true },
        new TextAnnotation { Position = new(60, 60), Text = "Hello\nWorld", FontSize = 22, Background = ColorRgba.White.WithAlpha(215) },
        new CalloutAnnotation { Box = new(60, 60, 120, 50), Tail = new(80, 150), Text = "Look here" },
        new StepAnnotation { Center = new(120, 110), Number = 12, Radius = 20 },
        new RedactAnnotation { Rect = new(60, 60, 140, 90) },
        new StickerAnnotation { Rect = new(60, 60, 80, 80), Glyph = "check" },
        new StickerAnnotation { Rect = new(60, 60, 80, 80), Glyph = "warning" },
        new StickerAnnotation { Rect = new(60, 60, 80, 80), Glyph = "Z" },
        new MeasureAnnotation { Start = new(60, 120), End = new(200, 120) },
    }.Select(a => new object[] { a });

    [Theory]
    [MemberData(nameof(LocalAnnotations))]
    public void Annotation_draws_something_and_only_near_its_bounds(Annotation a) => Sta.Run(() =>
    {
        var doc = Doc(a);
        var flat = DocumentRenderer.Flatten(doc);
        Assert.Equal(doc.Size, flat.Size);
        var changed = ChangedBounds(doc.Base, flat);
        Assert.False(changed.IsEmpty, $"{a.GetType().Name} drew nothing");

        // Nothing may be drawn far away from the annotation (callout tails and measure labels need slack).
        var allowed = a.Bounds.Inflate(a is CalloutAnnotation ? 110 : 40).ToPixelRectOuter();
        Assert.True(allowed.Contains(changed), $"{a.GetType().Name} changed {changed}, allowed {allowed}");
    });

    [Fact]
    public void Document_without_annotations_flattens_to_the_identical_buffer() => Sta.Run(() =>
    {
        var doc = Doc();
        Assert.Same(doc.Base, DocumentRenderer.Flatten(doc));
        Assert.Same(doc.Base, DocumentRenderer.RenderFinal(doc));
    });

    [Fact]
    public void Redact_replaces_pixels_exactly_and_leaves_no_trace_of_the_original() => Sta.Run(() =>
    {
        var image = White();
        for (int x = 100; x < 140; x++) image.SetPixel(x, 100, new ColorRgba(1, 2, 3)); // the "secret"
        var doc = new DocState(image, [new RedactAnnotation { Rect = new(90, 90, 60, 20) }]);
        var flat = DocumentRenderer.Flatten(doc);
        for (int y = 90; y < 110; y++)
            for (int x = 90; x < 150; x++)
                Assert.Equal(ColorRgba.Black, flat.GetPixel(x, y));
        Assert.Equal(ColorRgba.White, flat.GetPixel(89, 100));
        Assert.Equal(ColorRgba.White, flat.GetPixel(150, 100));
    });

    [Fact]
    public void Highlighter_tints_paper_but_keeps_dark_text_dark() => Sta.Run(() =>
    {
        var image = White();
        for (int x = 80; x < 180; x++)
            for (int y = 98; y < 103; y++)
                image.SetPixel(x, y, ColorRgba.Black); // a line of "text"
        var doc = new DocState(image, [new HighlighterAnnotation { Points = [new(60, 100), new(200, 100)], StrokeWidth = 24 }]);
        var flat = DocumentRenderer.Flatten(doc);
        Assert.Equal(ColorRgba.Black, flat.GetPixel(120, 100));
        var paper = flat.GetPixel(120, 92);
        Assert.True(paper.R > 240 && paper.G > 200 && paper.B < 40, $"paper is {paper}");
        Assert.Equal(ColorRgba.White, flat.GetPixel(120, 60));
    });

    [Fact]
    public void Blur_and_pixelate_change_only_their_rectangle() => Sta.Run(() =>
    {
        var image = new PixelBuffer(W, H);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
                image.SetPixel(x, y, (x / 4 + y / 4) % 2 == 0 ? ColorRgba.Black : ColorRgba.White);

        foreach (Annotation a in new Annotation[] { new BlurAnnotation { Rect = new(80, 60, 100, 80), Radius = 10 }, new PixelateAnnotation { Rect = new(80, 60, 100, 80), BlockSize = 10 } })
        {
            var flat = DocumentRenderer.Flatten(new DocState(image, [a]));
            var changed = ChangedBounds(image, flat);
            Assert.False(changed.IsEmpty);
            Assert.True(new PixelRect(80, 60, 100, 80).Contains(changed), $"{a.GetType().Name} changed {changed}");
            // The checkerboard must no longer be readable inside.
            var inside = flat.GetPixel(130, 100);
            Assert.True(inside.R is > 40 and < 215, $"{a.GetType().Name} left a sharp pixel {inside}");
        }
    });

    [Fact]
    public void Effects_partly_or_fully_outside_the_image_do_not_throw() => Sta.Run(() =>
    {
        var doc = Doc(
            new BlurAnnotation { Rect = new(-50, -50, 100, 100) },
            new PixelateAnnotation { Rect = new(300, 220, 200, 200) },
            new BlurAnnotation { Rect = new(1000, 1000, 50, 50) },
            new AreaHighlightAnnotation { Rect = new(-500, -500, 10, 10) },
            new HighlighterAnnotation { Points = [new(-100, -100), new(-50, -50)] },
            new HighlighterAnnotation { Points = [] },
            new MagnifierAnnotation { Source = new(-20, -20, 40, 40), Dest = new(100, 100, 80, 80) },
            new ArrowAnnotation { Start = new(5, 5), End = new(5, 5) },
            new CalloutAnnotation { Box = new(10, 10, 0, 0), Tail = new(0, 0) },
            new ImageAnnotation { Rect = new(10, 10, 20, 20), EncodedImage = [1, 2, 3, 4] },
            new FreehandAnnotation { Points = [new(50, 50)] });
        var flat = DocumentRenderer.Flatten(doc);
        Assert.Equal(doc.Size, flat.Size);
    });

    [Fact]
    public void Spotlight_dims_outside_and_leaves_inside_untouched() => Sta.Run(() =>
    {
        var doc = Doc(new SpotlightAnnotation { Rect = new(100, 80, 100, 60), DimOpacity = 0.5 });
        var flat = DocumentRenderer.Flatten(doc);
        Assert.Equal(ColorRgba.White, flat.GetPixel(150, 110));
        var outside = flat.GetPixel(10, 10);
        Assert.InRange(outside.R, 120, 135);
    });

    [Fact]
    public void Magnifier_shows_source_pixels_enlarged() => Sta.Run(() =>
    {
        var image = White();
        for (int y = 20; y < 30; y++)
            for (int x = 20; x < 30; x++)
                image.SetPixel(x, y, new ColorRgba(0, 0, 255));
        var doc = new DocState(image, [new MagnifierAnnotation { Source = new(20, 20, 10, 10), Dest = new(150, 100, 80, 80), BorderWidth = 2 }]);
        var flat = DocumentRenderer.Flatten(doc);
        Assert.Equal(new ColorRgba(0, 0, 255), flat.GetPixel(190, 140));
    });

    [Fact]
    public void Arrow_tip_reaches_its_end_point_and_uses_its_colour() => Sta.Run(() =>
    {
        var doc = Doc(new ArrowAnnotation { Start = new(50, 120), End = new(250, 120), StrokeWidth = 6, Color = new ColorRgba(0, 0, 255) });
        var flat = DocumentRenderer.Flatten(doc);
        Assert.Equal(new ColorRgba(0, 0, 255), flat.GetPixel(150, 120));
        Assert.True(flat.GetPixel(246, 120).B > 200 && flat.GetPixel(246, 120).R < 120);
        Assert.Equal(ColorRgba.White, flat.GetPixel(254, 120));
    });

    [Fact]
    public void Renderer_cache_follows_annotation_changes() => Sta.Run(() =>
    {
        var renderer = new AnnotationRenderer();
        var blur = new RedactAnnotation { Rect = new(10, 10, 20, 20) };
        var highlight = new AreaHighlightAnnotation { Rect = new(100, 100, 40, 40) };
        PixelBuffer Draw(DocState s)
        {
            var visual = new System.Windows.Media.DrawingVisual();
            using (var dc = visual.RenderOpen()) renderer.Render(dc, s);
            var target = new System.Windows.Media.Imaging.RenderTargetBitmap(W, H, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
            target.Render(visual);
            return target.ToPixelBuffer();
        }

        var first = Draw(Doc(blur, highlight));
        Assert.Equal(ColorRgba.Yellow, first.GetPixel(120, 120));
        var moved = Draw(Doc(blur, highlight.MoveBy(new Vec2(100, 0))));
        Assert.Equal(ColorRgba.White, moved.GetPixel(120, 120));
        Assert.Equal(ColorRgba.Yellow, moved.GetPixel(220, 120));
        var removed = Draw(Doc(blur));
        Assert.Equal(ColorRgba.White, removed.GetPixel(220, 120));
    });

    [Fact]
    public void Canvas_padding_background_and_shadow_grow_the_output() => Sta.Run(() =>
    {
        var doc = Doc() with { Canvas = new CanvasStyle { Padding = 40, Background = BackgroundKind.Solid, Color1 = new ColorRgba(0, 0, 255), CornerRadius = 12, Shadow = true } };
        var final = DocumentRenderer.RenderFinal(doc);
        Assert.Equal(new PixelSize(W + 80, H + 80), final.Size);
        Assert.Equal(new ColorRgba(0, 0, 255), final.GetPixel(2, 2));
        Assert.Equal(ColorRgba.White, final.GetPixel(40 + W / 2, 40 + H / 2));
        // Rounded corner: the very corner pixel of the image area shows the backdrop, not the image.
        Assert.NotEqual(ColorRgba.White, final.GetPixel(40, 40));
    });

    [Fact]
    public void Watermark_is_drawn_in_the_requested_corner() => Sta.Run(() =>
    {
        var doc = Doc() with { Watermark = new Watermark { Text = "ShotIt", Color = ColorRgba.Black, Opacity = 1, Position = WatermarkPosition.BottomRight } };
        var final = DocumentRenderer.RenderFinal(doc);
        var changed = ChangedBounds(doc.Base, final);
        Assert.False(changed.IsEmpty);
        Assert.True(changed.Left > W / 2 && changed.Top > H / 2, $"watermark at {changed}");
    });

    [Theory]
    [InlineData(ImageFormat.Png, true)]
    [InlineData(ImageFormat.Bmp, true)]
    [InlineData(ImageFormat.Jpeg, false)]
    public void Codec_round_trip(ImageFormat format, bool lossless) => Sta.Run(() =>
    {
        var image = new PixelBuffer(64, 48);
        for (int y = 0; y < 48; y++)
            for (int x = 0; x < 64; x++)
                image.SetPixel(x, y, new ColorRgba((byte)(x * 4), (byte)(y * 5), 128));
        var decoded = ImageCodec.Decode(ImageCodec.Encode(image, format));
        Assert.Equal(image.Size, decoded.Size);
        if (lossless) Assert.Equal(image.Data, decoded.Data);
        else Assert.InRange(decoded.GetPixel(32, 24).R, 118, 138);
    });

    [Fact]
    public void Png_keeps_transparency_and_jpeg_puts_it_on_white() => Sta.Run(() =>
    {
        var image = new PixelBuffer(8, 8);
        Assert.Equal(0, ImageCodec.Decode(ImageCodec.Encode(image, ImageFormat.Png)).GetPixel(3, 3).A);
        var jpeg = ImageCodec.Decode(ImageCodec.Encode(image, ImageFormat.Jpeg)).GetPixel(3, 3);
        Assert.True(jpeg.R > 245 && jpeg.G > 245 && jpeg.B > 245);
    });

    [Fact]
    public void Pdf_export_is_a_pdf() => Sta.Run(() =>
    {
        byte[] pdf = ImageCodec.Encode(White(), ImageFormat.Pdf);
        Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
    });

    [Fact]
    public void Decode_rejects_garbage_with_a_clear_exception()
    {
        Assert.Throws<NotSupportedException>(() => ImageCodec.Decode([1, 2, 3, 4, 5]));
        Assert.Throws<NotSupportedException>(() => ImageCodec.Decode([]));
    }

    [Fact]
    public void Thumbnail_is_bounded_and_keeps_aspect() => Sta.Run(() =>
    {
        var big = new PixelBuffer(2000, 500);
        big.Fill(ColorRgba.Red);
        var thumb = ImageCodec.Decode(ImageCodec.Thumbnail(big, 256));
        Assert.Equal(new PixelSize(256, 64), thumb.Size);
        var tiny = ImageCodec.Decode(ImageCodec.Thumbnail(new PixelBuffer(10, 5), 256));
        Assert.Equal(new PixelSize(10, 5), tiny.Size);
    });

    [Fact]
    public void Text_measurer_matches_rendered_extent() => Sta.Run(() =>
    {
        var text = new TextAnnotation { Position = new(40, 40), Text = "Measure me", FontSize = 24, Color = ColorRgba.Black };
        var flat = DocumentRenderer.Flatten(Doc(text));
        var changed = ChangedBounds(White(), flat);
        Assert.True(text.Bounds.ToPixelRectOuter().Inflate(2).Contains(changed), $"ink {changed} outside measured {text.Bounds}");
        Assert.True(changed.Width > text.Bounds.Width * 0.7, "measured width is far larger than the ink");
    });

    /// <summary>Writes a contact sheet so a human can eyeball every tool. Not an assertion-heavy test.</summary>
    [Fact]
    public void Contact_sheet_renders() => Sta.Run(() =>
    {
        var image = new PixelBuffer(900, 620);
        image.Fill(new ColorRgba(0xF4, 0xF5, 0xF7));
        var items = new List<Annotation>();
        for (int i = 0; i < 12; i++)
            items.Add(new TextAnnotation { Position = new(30, 20 + i * 22), Text = "The quick brown fox jumps over the lazy dog 0123456789", FontSize = 13, Color = new ColorRgba(0x22, 0x22, 0x22) });
        items.AddRange(
        [
            new HighlighterAnnotation { Points = [new(36, 58), new(300, 58)] },
            new AreaHighlightAnnotation { Rect = new(30, 110, 250, 44), Color = new ColorRgba(0x7C, 0xFF, 0x6B) },
            new BlurAnnotation { Rect = new(30, 180, 180, 44) },
            new PixelateAnnotation { Rect = new(220, 180, 160, 44), BlockSize = 8 },
            new RedactAnnotation { Rect = new(30, 244, 140, 20) },
            new ArrowAnnotation { Start = new(470, 60), End = new(620, 130), StrokeWidth = 5 },
            new ArrowAnnotation { Start = new(470, 150), End = new(640, 200), Control = new Vec2(520, 220), DoubleHeaded = true, Color = new ColorRgba(0x1E, 0x88, 0xE5) },
            new LineAnnotation { Points = [new(470, 250), new(640, 250)], Dashed = true, StrokeWidth = 3 },
            new ShapeAnnotation { Rect = new(680, 40, 180, 90), Kind = ShapeKind.Rectangle },
            new ShapeAnnotation { Rect = new(680, 150, 180, 90), Kind = ShapeKind.Ellipse, Color = new ColorRgba(0x2E, 0xB8, 0x5C), Fill = new ColorRgba(0x2E, 0xB8, 0x5C, 60) },
            new FreehandAnnotation { Points = [.. Enumerable.Range(0, 60).Select(i => new Vec2(40 + i * 5, 330 + Math.Sin(i / 4.0) * 22))], Color = new ColorRgba(0x7E, 0x57, 0xC2) },
            new TextAnnotation { Position = new(400, 300), Text = "Text with pill", FontSize = 22, Bold = true, Background = ColorRgba.White.WithAlpha(215) },
            new TextAnnotation { Position = new(400, 350), Text = "Outlined", FontSize = 26, Bold = true, Color = ColorRgba.White, Outline = ColorRgba.Black },
            new CalloutAnnotation { Box = new(640, 290, 200, 64), Tail = new(600, 420), Text = "Callout with a tail" },
            new StepAnnotation { Center = new(60, 430), Number = 1 },
            new StepAnnotation { Center = new(110, 430), Number = 2, Color = new ColorRgba(0x1E, 0x88, 0xE5) },
            new StepAnnotation { Center = new(160, 430), Number = 13, Color = ColorRgba.Yellow, TextColor = ColorRgba.Black },
            new MagnifierAnnotation { Source = new(30, 16, 110, 30), Dest = new(220, 400, 330, 90) },
            new MeasureAnnotation { Start = new(600, 480), End = new(860, 480) },
        ]);
        int sx = 40;
        foreach (string name in Stickers.Names)
        {
            items.Add(new StickerAnnotation { Rect = new(sx, 520, 64, 64), Glyph = name });
            sx += 80;
        }

        var doc = new DocState(image, [.. items])
        {
            Canvas = new CanvasStyle { Padding = 48, Background = BackgroundKind.Gradient, CornerRadius = 14, Shadow = true },
            Watermark = new Watermark { Text = "ShotIt", Opacity = 0.7, Color = new ColorRgba(0x55, 0x55, 0x55) },
        };
        var final = DocumentRenderer.RenderFinal(doc);
        string path = Path.Combine(Path.GetTempPath(), "shotit-contact-sheet.png");
        File.WriteAllBytes(path, ImageCodec.Encode(final, ImageFormat.Png));
        Assert.True(new FileInfo(path).Length > 10_000);
    });
}
