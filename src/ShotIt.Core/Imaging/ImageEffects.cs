using ShotIt.Core.Geometry;

namespace ShotIt.Core.Imaging;

/// <summary>Pure pixel operations. Every method returns a new buffer and leaves its input untouched.</summary>
public static class ImageEffects
{
    /// <summary>Three box-blur passes, which closely approximate a Gaussian blur.</summary>
    public static PixelBuffer Blur(PixelBuffer source, int radius)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (radius <= 0) return source.Clone();
        radius = Math.Min(radius, Math.Max(1, Math.Max(source.Width, source.Height)));
        var a = source.Clone();
        var b = new PixelBuffer(source.Width, source.Height);
        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlurHorizontal(a, b, radius);
            BoxBlurVertical(b, a, radius);
        }
        return a;
    }

    private static void BoxBlurHorizontal(PixelBuffer src, PixelBuffer dst, int radius)
    {
        int w = src.Width, h = src.Height, window = radius * 2 + 1;
        for (int y = 0; y < h; y++)
        {
            var s = src.Row(y);
            var d = dst.Row(y);
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int i = -radius; i <= radius; i++) sum += s[Math.Clamp(i, 0, w - 1) * 4 + c];
                for (int x = 0; x < w; x++)
                {
                    d[x * 4 + c] = (byte)(sum / window);
                    int add = Math.Min(x + radius + 1, w - 1), remove = Math.Max(x - radius, 0);
                    sum += s[add * 4 + c] - s[remove * 4 + c];
                }
            }
        }
    }

    private static void BoxBlurVertical(PixelBuffer src, PixelBuffer dst, int radius)
    {
        int w = src.Width, h = src.Height, stride = src.Stride, window = radius * 2 + 1;
        byte[] s = src.Data, d = dst.Data;
        for (int x = 0; x < w; x++)
        {
            for (int c = 0; c < 4; c++)
            {
                int o = x * 4 + c, sum = 0;
                for (int i = -radius; i <= radius; i++) sum += s[Math.Clamp(i, 0, h - 1) * stride + o];
                for (int y = 0; y < h; y++)
                {
                    d[y * stride + o] = (byte)(sum / window);
                    int add = Math.Min(y + radius + 1, h - 1), remove = Math.Max(y - radius, 0);
                    sum += s[add * stride + o] - s[remove * stride + o];
                }
            }
        }
    }

    /// <summary>Replaces each block of pixels with its average colour.</summary>
    public static PixelBuffer Pixelate(PixelBuffer source, int blockSize)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (blockSize <= 1) return source.Clone();
        var result = new PixelBuffer(source.Width, source.Height);
        byte[] s = source.Data, d = result.Data;
        int stride = source.Stride;
        for (int by = 0; by < source.Height; by += blockSize)
        {
            int bh = Math.Min(blockSize, source.Height - by);
            for (int bx = 0; bx < source.Width; bx += blockSize)
            {
                int bw = Math.Min(blockSize, source.Width - bx);
                long sb = 0, sg = 0, sr = 0, sa = 0;
                for (int y = 0; y < bh; y++)
                {
                    int i = (by + y) * stride + bx * 4;
                    for (int x = 0; x < bw; x++, i += 4)
                    {
                        sb += s[i]; sg += s[i + 1]; sr += s[i + 2]; sa += s[i + 3];
                    }
                }
                int n = bw * bh;
                byte b = (byte)(sb / n), g = (byte)(sg / n), r = (byte)(sr / n), a = (byte)(sa / n);
                for (int y = 0; y < bh; y++)
                {
                    int i = (by + y) * stride + bx * 4;
                    for (int x = 0; x < bw; x++, i += 4)
                    {
                        d[i] = b; d[i + 1] = g; d[i + 2] = r; d[i + 3] = a;
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Multiply-blends a tint over the image like a real highlighter pen: dark text stays dark.
    /// <paramref name="coverage"/> holds one byte per pixel (0 = untouched, 255 = full tint).
    /// Transparent source pixels are blended against white so highlights stay visible on them.
    /// </summary>
    public static PixelBuffer MultiplyTint(PixelBuffer source, ReadOnlySpan<byte> coverage, ColorRgba tint)
    {
        ArgumentNullException.ThrowIfNull(source);
        int count = source.Width * source.Height;
        if (coverage.Length < count) throw new ArgumentException("Coverage mask is smaller than the image.", nameof(coverage));
        var result = source.Clone();
        byte[] d = result.Data;
        for (int p = 0, i = 0; p < count; p++, i += 4)
        {
            int cov = coverage[p] * tint.A / 255;
            if (cov == 0) continue;
            int alpha = d[i + 3];
            // Composite the source pixel over white first so transparent areas highlight correctly.
            int b = (d[i] * alpha + 255 * (255 - alpha)) / 255;
            int g = (d[i + 1] * alpha + 255 * (255 - alpha)) / 255;
            int r = (d[i + 2] * alpha + 255 * (255 - alpha)) / 255;
            int mb = b * tint.B / 255, mg = g * tint.G / 255, mr = r * tint.R / 255;
            d[i] = (byte)((mb * cov + d[i] * (255 - cov)) / 255);
            d[i + 1] = (byte)((mg * cov + d[i + 1] * (255 - cov)) / 255);
            d[i + 2] = (byte)((mr * cov + d[i + 2] * (255 - cov)) / 255);
            d[i + 3] = (byte)Math.Max(alpha, cov);
        }
        return result;
    }

    public static PixelBuffer Grayscale(PixelBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = source.Clone();
        byte[] d = result.Data;
        for (int i = 0; i < source.Width * source.Height * 4; i += 4)
        {
            byte l = (byte)((d[i] * 114 + d[i + 1] * 587 + d[i + 2] * 299) / 1000);
            d[i] = l; d[i + 1] = l; d[i + 2] = l;
        }
        return result;
    }

    public static PixelBuffer Invert(PixelBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = source.Clone();
        byte[] d = result.Data;
        for (int i = 0; i < source.Width * source.Height * 4; i += 4)
        {
            d[i] = (byte)(255 - d[i]); d[i + 1] = (byte)(255 - d[i + 1]); d[i + 2] = (byte)(255 - d[i + 2]);
        }
        return result;
    }

    /// <summary>Brightness in -100..100 (additive), contrast in -100..100.</summary>
    public static PixelBuffer BrightnessContrast(PixelBuffer source, int brightness, int contrast)
    {
        ArgumentNullException.ThrowIfNull(source);
        brightness = Math.Clamp(brightness, -100, 100);
        contrast = Math.Clamp(contrast, -100, 100);
        double factor = 259.0 * (contrast * 2.55 + 255) / (255.0 * (259 - contrast * 2.55));
        Span<byte> lut = stackalloc byte[256];
        for (int v = 0; v < 256; v++)
            lut[v] = (byte)Math.Clamp(Math.Round(factor * (v - 128) + 128 + brightness * 2.55), 0, 255);
        var result = source.Clone();
        byte[] d = result.Data;
        for (int i = 0; i < source.Width * source.Height * 4; i += 4)
        {
            d[i] = lut[d[i]]; d[i + 1] = lut[d[i + 1]]; d[i + 2] = lut[d[i + 2]];
        }
        return result;
    }

    public static PixelBuffer FlipHorizontal(PixelBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new PixelBuffer(source.Width, source.Height);
        int w = source.Width;
        for (int y = 0; y < source.Height; y++)
        {
            var s = source.Row(y);
            var d = result.Row(y);
            for (int x = 0; x < w; x++) s.Slice(x * 4, 4).CopyTo(d.Slice((w - 1 - x) * 4, 4));
        }
        return result;
    }

    public static PixelBuffer FlipVertical(PixelBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var result = new PixelBuffer(source.Width, source.Height);
        for (int y = 0; y < source.Height; y++) source.Row(y).CopyTo(result.Row(source.Height - 1 - y));
        return result;
    }

    /// <summary>Rotates by 90 degrees clockwise; width and height swap.</summary>
    public static PixelBuffer Rotate90(PixelBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);
        int w = source.Width, h = source.Height;
        var result = new PixelBuffer(h, w);
        byte[] s = source.Data, d = result.Data;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int si = (y * w + x) * 4;
                int di = (x * h + (h - 1 - y)) * 4;
                d[di] = s[si]; d[di + 1] = s[si + 1]; d[di + 2] = s[si + 2]; d[di + 3] = s[si + 3];
            }
        }
        return result;
    }

    /// <summary>Makes every pixel outside the polygon transparent (freeform capture).</summary>
    public static PixelBuffer MaskPolygon(PixelBuffer source, IReadOnlyList<Vec2> polygon)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(polygon);
        var result = source.Clone();
        if (polygon.Count < 3) return result;
        byte[] d = result.Data;
        var crossings = new List<double>();
        for (int y = 0; y < source.Height; y++)
        {
            double py = y + 0.5;
            crossings.Clear();
            for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
            {
                var a = polygon[i];
                var b = polygon[j];
                if ((a.Y > py) != (b.Y > py))
                    crossings.Add((b.X - a.X) * (py - a.Y) / (b.Y - a.Y) + a.X);
            }
            crossings.Sort();
            int row = y * source.Stride, k = 0;
            bool inside = false;
            for (int x = 0; x < source.Width; x++)
            {
                double px = x + 0.5;
                while (k < crossings.Count && crossings[k] <= px) { inside = !inside; k++; }
                if (!inside)
                {
                    int i = row + x * 4;
                    d[i] = 0; d[i + 1] = 0; d[i + 2] = 0; d[i + 3] = 0;
                }
            }
        }
        return result;
    }

    /// <summary>High quality downscale / bilinear upscale to an exact size.</summary>
    public static PixelBuffer Resize(PixelBuffer source, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (width == source.Width && height == source.Height) return source.Clone();
        var result = new PixelBuffer(width, height);
        byte[] s = source.Data, d = result.Data;
        double sx = (double)source.Width / width, sy = (double)source.Height / height;
        bool shrink = sx > 1 || sy > 1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int di = (y * width + x) * 4;
                if (shrink)
                {
                    // Area average: every source pixel the destination pixel covers contributes.
                    int x0 = (int)(x * sx), x1 = Math.Max(x0 + 1, Math.Min(source.Width, (int)Math.Ceiling((x + 1) * sx)));
                    int y0 = (int)(y * sy), y1 = Math.Max(y0 + 1, Math.Min(source.Height, (int)Math.Ceiling((y + 1) * sy)));
                    long b = 0, g = 0, r = 0, a = 0;
                    for (int yy = y0; yy < y1; yy++)
                    {
                        int si = (yy * source.Width + x0) * 4;
                        for (int xx = x0; xx < x1; xx++, si += 4)
                        {
                            b += s[si]; g += s[si + 1]; r += s[si + 2]; a += s[si + 3];
                        }
                    }
                    int n = (x1 - x0) * (y1 - y0);
                    d[di] = (byte)(b / n); d[di + 1] = (byte)(g / n); d[di + 2] = (byte)(r / n); d[di + 3] = (byte)(a / n);
                }
                else
                {
                    double fx = Math.Max(0, (x + 0.5) * sx - 0.5), fy = Math.Max(0, (y + 0.5) * sy - 0.5);
                    int x0 = (int)fx, y0 = (int)fy;
                    int x1 = Math.Min(x0 + 1, source.Width - 1), y1 = Math.Min(y0 + 1, source.Height - 1);
                    double tx = fx - x0, ty = fy - y0;
                    for (int c = 0; c < 4; c++)
                    {
                        double top = s[(y0 * source.Width + x0) * 4 + c] * (1 - tx) + s[(y0 * source.Width + x1) * 4 + c] * tx;
                        double bottom = s[(y1 * source.Width + x0) * 4 + c] * (1 - tx) + s[(y1 * source.Width + x1) * 4 + c] * tx;
                        d[di + c] = (byte)Math.Round(top * (1 - ty) + bottom * ty);
                    }
                }
            }
        }
        return result;
    }
}
