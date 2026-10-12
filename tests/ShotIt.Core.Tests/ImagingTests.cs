using ShotIt.Core.Export;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;

namespace ShotIt.Core.Tests;

public class ImagingTests
{
    private static PixelBuffer Gradient(int w, int h)
    {
        var b = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                b.SetPixel(x, y, new ColorRgba((byte)(x * 255 / Math.Max(1, w - 1)), (byte)(y * 255 / Math.Max(1, h - 1)), (byte)((x + y) % 256)));
        return b;
    }

    [Theory]
    [InlineData("#FF0000", 255, 0, 0, 255)]
    [InlineData("00ff7f", 0, 255, 127, 255)]
    [InlineData("#11223344", 0x11, 0x22, 0x33, 0x44)]
    public void Color_parses_hex(string text, byte r, byte g, byte b, byte a)
    {
        Assert.True(ColorRgba.TryParse(text, out var c));
        Assert.Equal(new ColorRgba(r, g, b, a), c);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("#GGGGGG")]
    [InlineData(null)]
    public void Color_rejects_garbage(string? text) => Assert.False(ColorRgba.TryParse(text, out _));

    [Fact]
    public void Color_hex_round_trips()
    {
        var c = new ColorRgba(1, 2, 3, 4);
        Assert.Equal(c, ColorRgba.Parse(c.ToHex()));
        Assert.Equal("#010203", new ColorRgba(1, 2, 3).ToHex());
    }

    [Fact]
    public void Crop_copies_the_right_pixels()
    {
        var src = Gradient(20, 10);
        var crop = src.Crop(new PixelRect(5, 2, 4, 3));
        Assert.Equal(4, crop.Width);
        Assert.Equal(3, crop.Height);
        for (int y = 0; y < 3; y++)
            for (int x = 0; x < 4; x++)
                Assert.Equal(src.GetPixel(5 + x, 2 + y), crop.GetPixel(x, y));
    }

    [Fact]
    public void Crop_clips_to_image_and_rejects_no_overlap()
    {
        var src = Gradient(20, 10);
        var crop = src.Crop(new PixelRect(-5, -5, 10, 10));
        Assert.Equal(new PixelSize(5, 5), crop.Size);
        Assert.Throws<ArgumentException>(() => src.Crop(new PixelRect(100, 100, 5, 5)));
    }

    [Fact]
    public void Blit_clips_at_all_edges()
    {
        var dst = new PixelBuffer(4, 4);
        var src = new PixelBuffer(4, 4);
        src.Fill(ColorRgba.White);
        dst.Blit(src, -2, -2);
        Assert.Equal(ColorRgba.White, dst.GetPixel(1, 1));
        Assert.Equal(ColorRgba.Transparent, dst.GetPixel(2, 2));
        dst.Blit(src, 3, 3);
        Assert.Equal(ColorRgba.White, dst.GetPixel(3, 3));
        dst.Blit(src, 50, 50); // entirely outside: must not throw
    }

    [Fact]
    public void Blur_of_uniform_image_is_unchanged()
    {
        var src = new PixelBuffer(32, 16);
        src.Fill(new ColorRgba(10, 200, 30));
        var blurred = ImageEffects.Blur(src, 5);
        Assert.Equal(src.Data, blurred.Data);
    }

    [Fact]
    public void Blur_spreads_a_single_pixel_and_keeps_input_untouched()
    {
        var src = new PixelBuffer(21, 21);
        src.Fill(ColorRgba.Black);
        src.SetPixel(10, 10, ColorRgba.White);
        byte[] before = (byte[])src.Data.Clone();
        var blurred = ImageEffects.Blur(src, 3);
        Assert.Equal(before, src.Data);
        Assert.True(blurred.GetPixel(10, 10).R < 255);
        Assert.True(blurred.GetPixel(12, 10).R > 0);
        Assert.Equal(blurred.GetPixel(8, 10), blurred.GetPixel(12, 10)); // symmetric
    }

    [Fact]
    public void Blur_radius_larger_than_image_does_not_throw()
    {
        var blurred = ImageEffects.Blur(Gradient(3, 2), 500);
        Assert.Equal(new PixelSize(3, 2), blurred.Size);
    }

    [Fact]
    public void Pixelate_makes_blocks_uniform_including_partial_edge_blocks()
    {
        var p = ImageEffects.Pixelate(Gradient(10, 7), 4);
        Assert.Equal(p.GetPixel(0, 0), p.GetPixel(3, 3));
        Assert.Equal(p.GetPixel(8, 4), p.GetPixel(9, 6)); // 2x3 leftover block
        Assert.NotEqual(p.GetPixel(0, 0), p.GetPixel(4, 0));
    }

    [Fact]
    public void MultiplyTint_keeps_black_text_black_and_tints_white_paper()
    {
        var src = new PixelBuffer(2, 1);
        src.SetPixel(0, 0, ColorRgba.Black);
        src.SetPixel(1, 0, ColorRgba.White);
        var tinted = ImageEffects.MultiplyTint(src, [255, 255], ColorRgba.Yellow);
        Assert.Equal(ColorRgba.Black, tinted.GetPixel(0, 0));
        Assert.Equal(ColorRgba.Yellow, tinted.GetPixel(1, 0));
    }

    [Fact]
    public void MultiplyTint_respects_coverage_and_shows_on_transparent_pixels()
    {
        var src = new PixelBuffer(2, 1);
        src.SetPixel(0, 0, ColorRgba.White);
        src.SetPixel(1, 0, ColorRgba.Transparent);
        var tinted = ImageEffects.MultiplyTint(src, [0, 255], ColorRgba.Yellow);
        Assert.Equal(ColorRgba.White, tinted.GetPixel(0, 0));
        Assert.Equal(ColorRgba.Yellow, tinted.GetPixel(1, 0)); // P36: highlight visible on transparency
    }

    [Fact]
    public void Rotate90_four_times_is_identity()
    {
        var src = Gradient(7, 4);
        var r = ImageEffects.Rotate90(ImageEffects.Rotate90(ImageEffects.Rotate90(ImageEffects.Rotate90(src))));
        Assert.Equal(src.Data, r.Data);
    }

    [Fact]
    public void Rotate90_moves_top_left_to_top_right()
    {
        var src = Gradient(7, 4);
        var r = ImageEffects.Rotate90(src);
        Assert.Equal(new PixelSize(4, 7), r.Size);
        Assert.Equal(src.GetPixel(0, 0), r.GetPixel(3, 0));
        Assert.Equal(src.GetPixel(6, 0), r.GetPixel(3, 6));
    }

    [Fact]
    public void Flips_are_involutions()
    {
        var src = Gradient(9, 5);
        Assert.Equal(src.Data, ImageEffects.FlipHorizontal(ImageEffects.FlipHorizontal(src)).Data);
        Assert.Equal(src.Data, ImageEffects.FlipVertical(ImageEffects.FlipVertical(src)).Data);
        Assert.Equal(src.GetPixel(0, 0), ImageEffects.FlipHorizontal(src).GetPixel(8, 0));
        Assert.Equal(src.GetPixel(0, 0), ImageEffects.FlipVertical(src).GetPixel(0, 4));
    }

    [Fact]
    public void Invert_twice_is_identity_and_grayscale_equalizes_channels()
    {
        var src = Gradient(9, 5);
        Assert.Equal(src.Data, ImageEffects.Invert(ImageEffects.Invert(src)).Data);
        var g = ImageEffects.Grayscale(src).GetPixel(4, 2);
        Assert.Equal(g.R, g.G);
        Assert.Equal(g.G, g.B);
    }

    [Fact]
    public void BrightnessContrast_zero_is_identity()
    {
        var src = Gradient(9, 5);
        Assert.Equal(src.Data, ImageEffects.BrightnessContrast(src, 0, 0).Data);
        Assert.True(ImageEffects.BrightnessContrast(src, 50, 0).GetPixel(4, 2).R >= src.GetPixel(4, 2).R);
    }

    [Fact]
    public void MaskPolygon_clears_outside_only()
    {
        var src = new PixelBuffer(10, 10);
        src.Fill(ColorRgba.White);
        var masked = ImageEffects.MaskPolygon(src, [new Vec2(0, 0), new Vec2(10, 0), new Vec2(0, 10)]);
        Assert.Equal(ColorRgba.White, masked.GetPixel(1, 1));
        Assert.Equal(ColorRgba.Transparent, masked.GetPixel(9, 9));
    }

    [Fact]
    public void Resize_preserves_uniform_colour_both_directions()
    {
        var src = new PixelBuffer(8, 8);
        src.Fill(new ColorRgba(40, 80, 120));
        foreach (var size in new[] { (3, 5), (16, 16), (8, 8), (1, 1), (17, 3) })
        {
            var r = ImageEffects.Resize(src, size.Item1, size.Item2);
            Assert.Equal(new PixelSize(size.Item1, size.Item2), r.Size);
            Assert.Equal(new ColorRgba(40, 80, 120), r.GetPixel(0, 0));
            Assert.Equal(new ColorRgba(40, 80, 120), r.GetPixel(size.Item1 - 1, size.Item2 - 1));
        }
    }

    [Fact]
    public void MakeOpaque_sets_alpha()
    {
        var b = new PixelBuffer(3, 3);
        b.MakeOpaque();
        Assert.Equal(255, b.GetPixel(2, 2).A);
    }

    [Fact]
    public void Pdf_has_valid_structure_and_xref_offsets()
    {
        byte[] jpeg = [0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9];
        byte[] pdf = PdfWriter.FromJpeg(jpeg, 200, 100);
        string text = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-1.4", text, StringComparison.Ordinal);
        Assert.EndsWith("%%EOF\n", text, StringComparison.Ordinal);
        Assert.Contains("/MediaBox [0 0 150 75]", text, StringComparison.Ordinal);

        int startxref = int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal("xref", text.Substring(startxref, 4));
        // Every xref entry must point at "N 0 obj".
        var lines = text[startxref..].Split('\n');
        for (int i = 1; i <= 5; i++)
        {
            int offset = int.Parse(lines[2 + i][..10], System.Globalization.CultureInfo.InvariantCulture);
            Assert.StartsWith($"{i} 0 obj", text[offset..], StringComparison.Ordinal);
        }
    }
}
