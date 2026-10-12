using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShotIt.Core.Annotations;
using ShotIt.Core.Export;
using ShotIt.Core.Imaging;
using ShotIt.Core.Settings;

namespace ShotIt.Rendering;

public static class WpfInterop
{
    private static readonly ConditionalWeakTable<PixelBuffer, BitmapSource> BitmapCache = [];

    public static Color ToWpf(this ColorRgba c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    public static ColorRgba ToRgba(this Color c) => new(c.R, c.G, c.B, c.A);

    public static SolidColorBrush ToBrush(this ColorRgba c)
    {
        var brush = new SolidColorBrush(c.ToWpf());
        brush.Freeze();
        return brush;
    }

    public static Point ToWpf(this ShotIt.Core.Geometry.Vec2 v) => new(v.X, v.Y);

    public static Rect ToWpf(this ShotIt.Core.Geometry.RectD r) => new(r.X, r.Y, Math.Max(0, r.Width), Math.Max(0, r.Height));

    public static ShotIt.Core.Geometry.Vec2 ToVec(this Point p) => new(p.X, p.Y);

    /// <summary>Creates a frozen bitmap from the buffer. One unit equals one pixel when <paramref name="dpi"/> is 96.</summary>
    public static BitmapSource ToBitmapSource(this PixelBuffer buffer, double dpi = 96)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var bitmap = BitmapSource.Create(buffer.Width, buffer.Height, dpi, dpi, PixelFormats.Bgra32, null, buffer.Data, buffer.Stride);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// A 96-DPI bitmap for the buffer, cached for as long as the buffer is alive. Document base images are
    /// immutable, so the same bitmap is reused for every repaint and every undo step that shares the image.
    /// </summary>
    public static BitmapSource ToCachedBitmap(this PixelBuffer buffer) => BitmapCache.GetValue(buffer, b => b.ToBitmapSource());

    public static PixelBuffer ToPixelBuffer(this BitmapSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        BitmapSource bgra = source.Format == PixelFormats.Bgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var buffer = new PixelBuffer(bgra.PixelWidth, bgra.PixelHeight);
        bgra.CopyPixels(buffer.Data, buffer.Stride, 0);
        return buffer;
    }
}

/// <summary>Measures text exactly as the renderer will draw it. Installed as <see cref="TextMetrics.Measurer"/> at startup.</summary>
public sealed class WpfTextMeasurer : ITextMeasurer
{
    public static void Install() => TextMetrics.Measurer = new WpfTextMeasurer();

    public (double Width, double Height) Measure(string text, string fontFamily, double fontSize, bool bold, bool italic)
    {
        var formatted = TextLayout.Create(string.IsNullOrEmpty(text) ? " " : text, fontFamily, fontSize, bold, italic, Brushes.Black);
        return (formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }
}

public static class TextLayout
{
    public static FormattedText Create(string text, string fontFamily, double fontSize, bool bold, bool italic, Brush brush)
    {
        var typeface = new Typeface(
            new FontFamily(string.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI" : fontFamily + ", Segoe UI, Segoe UI Emoji, Segoe UI Symbol"),
            italic ? FontStyles.Italic : FontStyles.Normal,
            bold ? FontWeights.SemiBold : FontWeights.Normal,
            FontStretches.Normal);
        return new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, Math.Max(1, fontSize), brush, 1.0);
    }
}

/// <summary>Encoding and decoding through the Windows Imaging Component.</summary>
public static class ImageCodec
{
    public static string Extension(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => ".jpg",
        ImageFormat.Bmp => ".bmp",
        ImageFormat.Pdf => ".pdf",
        _ => ".png",
    };

    public static string ContentType(ImageFormat format) => format switch
    {
        ImageFormat.Jpeg => "image/jpeg",
        ImageFormat.Bmp => "image/bmp",
        ImageFormat.Pdf => "application/pdf",
        _ => "image/png",
    };

    public static ImageFormat FormatFromPath(string path) => Path.GetExtension(path).ToUpperInvariant() switch
    {
        ".JPG" or ".JPEG" => ImageFormat.Jpeg,
        ".BMP" => ImageFormat.Bmp,
        ".PDF" => ImageFormat.Pdf,
        _ => ImageFormat.Png,
    };

    /// <summary>Encodes the image. The output carries no metadata: only pixels are written.</summary>
    public static byte[] Encode(PixelBuffer image, ImageFormat format, int jpegQuality = 90)
    {
        ArgumentNullException.ThrowIfNull(image);
        switch (format)
        {
            case ImageFormat.Jpeg:
                return EncodeJpeg(image, jpegQuality);
            case ImageFormat.Pdf:
                return PdfWriter.FromJpeg(EncodeJpeg(image, Math.Max(jpegQuality, 92)), image.Width, image.Height);
            case ImageFormat.Bmp:
                return Run(new BmpBitmapEncoder(), Flatten(image, ColorRgba.White).ToBitmapSource());
            default:
                return Run(new PngBitmapEncoder(), image.ToBitmapSource());
        }
    }

    private static byte[] EncodeJpeg(PixelBuffer image, int quality)
    {
        // JPEG has no alpha; put transparent pixels on white rather than letting them turn black.
        var encoder = new JpegBitmapEncoder { QualityLevel = Math.Clamp(quality, 1, 100) };
        var opaque = new FormatConvertedBitmap(Flatten(image, ColorRgba.White).ToBitmapSource(), PixelFormats.Bgr24, null, 0);
        return Run(encoder, opaque);
    }

    private static byte[] Run(BitmapEncoder encoder, BitmapSource source)
    {
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Composites the image over a solid background, producing a fully opaque copy.</summary>
    public static PixelBuffer Flatten(PixelBuffer image, ColorRgba background)
    {
        ArgumentNullException.ThrowIfNull(image);
        var result = image.Clone();
        byte[] d = result.Data;
        for (int i = 0; i < image.Width * image.Height * 4; i += 4)
        {
            int a = d[i + 3];
            if (a == 255) continue;
            d[i] = (byte)((d[i] * a + background.B * (255 - a)) / 255);
            d[i + 1] = (byte)((d[i + 1] * a + background.G * (255 - a)) / 255);
            d[i + 2] = (byte)((d[i + 2] * a + background.R * (255 - a)) / 255);
            d[i + 3] = 255;
        }

        return result;
    }

    /// <summary>Decodes any format WIC understands (PNG, JPEG, BMP, GIF, TIFF, WebP with the system codec, ICO).</summary>
    /// <exception cref="NotSupportedException">The data is not a readable image.</exception>
    public static PixelBuffer Decode(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) throw new NotSupportedException("The file contains no image.");
            return decoder.Frames[0].ToPixelBuffer();
        }
        catch (Exception ex) when (ex is FileFormatException or System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException or OverflowException or IOException)
        {
            throw new NotSupportedException("The file is not an image ShotIt can read.", ex);
        }
    }

    public static PixelBuffer DecodeFile(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>A small JPEG preview whose longest side is at most <paramref name="maxSide"/> pixels.</summary>
    public static byte[] Thumbnail(PixelBuffer image, int maxSide = 256)
    {
        ArgumentNullException.ThrowIfNull(image);
        double scale = Math.Min(1.0, (double)maxSide / Math.Max(image.Width, image.Height));
        var small = scale < 1
            ? ImageEffects.Resize(image, Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)))
            : image;
        return EncodeJpeg(small, 82);
    }
}
