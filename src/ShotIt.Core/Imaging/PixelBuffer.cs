using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShotIt.Core.Geometry;

namespace ShotIt.Core.Imaging;

/// <summary>An 8-bit RGBA colour with straight (non-premultiplied) alpha.</summary>
[JsonConverter(typeof(ColorRgbaJsonConverter))]
public readonly record struct ColorRgba(byte R, byte G, byte B, byte A = 255)
{
    public static readonly ColorRgba Black = new(0, 0, 0);
    public static readonly ColorRgba White = new(255, 255, 255);
    public static readonly ColorRgba Red = new(0xE8, 0x1C, 0x2E);
    public static readonly ColorRgba Yellow = new(0xFF, 0xE6, 0x00);
    public static readonly ColorRgba Transparent = new(0, 0, 0, 0);

    public ColorRgba WithAlpha(byte a) => new(R, G, B, a);

    public string ToHex() => A == 255
        ? string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}")
        : string.Create(CultureInfo.InvariantCulture, $"#{R:X2}{G:X2}{B:X2}{A:X2}");

    /// <summary>Relative luminance in 0..1; used to pick a readable foreground.</summary>
    public double Luminance => (0.2126 * R + 0.7152 * G + 0.0722 * B) / 255.0;

    public static bool TryParse(string? text, out ColorRgba color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.AsSpan().Trim();
        if (s.Length > 0 && s[0] == '#') s = s[1..];
        if (s.Length is not (6 or 8)) return false;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return false;
        color = s.Length == 6
            ? new ColorRgba((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new ColorRgba((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static ColorRgba Parse(string text) =>
        TryParse(text, out var c) ? c : throw new FormatException($"'{text}' is not a valid #RRGGBB or #RRGGBBAA colour.");

    public override string ToString() => ToHex();
}

public sealed class ColorRgbaJsonConverter : JsonConverter<ColorRgba>
{
    public override ColorRgba Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        ColorRgba.TryParse(reader.GetString(), out var c) ? c : ColorRgba.Black;

    public override void Write(Utf8JsonWriter writer, ColorRgba value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToHex());
}

/// <summary>
/// A top-down 32-bit BGRA image with straight alpha. Instances handed to the editor are treated
/// as immutable so undo history can share them.
/// </summary>
public sealed class PixelBuffer
{
    public PixelBuffer(int width, int height)
        : this(width, height, new byte[checked(width * height * 4)])
    {
    }

    public PixelBuffer(int width, int height, byte[] data)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < checked(width * height * 4))
            throw new ArgumentException("Pixel data is smaller than width * height * 4.", nameof(data));
        Width = width;
        Height = height;
        Data = data;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public byte[] Data { get; }
    public PixelSize Size => new(Width, Height);
    public PixelRect Bounds => new(0, 0, Width, Height);

    public Span<byte> Row(int y) => Data.AsSpan(y * Stride, Stride);

    public ColorRgba GetPixel(int x, int y)
    {
        int i = y * Stride + x * 4;
        return new ColorRgba(Data[i + 2], Data[i + 1], Data[i], Data[i + 3]);
    }

    public void SetPixel(int x, int y, ColorRgba c)
    {
        int i = y * Stride + x * 4;
        Data[i] = c.B; Data[i + 1] = c.G; Data[i + 2] = c.R; Data[i + 3] = c.A;
    }

    public void Fill(ColorRgba c)
    {
        var row = Row(0);
        for (int x = 0; x < Width; x++)
        {
            row[x * 4] = c.B; row[x * 4 + 1] = c.G; row[x * 4 + 2] = c.R; row[x * 4 + 3] = c.A;
        }
        for (int y = 1; y < Height; y++) row.CopyTo(Row(y));
    }

    public PixelBuffer Clone() => new(Width, Height, (byte[])Data.Clone());

    /// <summary>Copies the part of <paramref name="rect"/> that lies inside the image. Throws when nothing overlaps.</summary>
    public PixelBuffer Crop(PixelRect rect)
    {
        var r = rect.Intersect(Bounds);
        if (r.IsEmpty) throw new ArgumentException("Crop rectangle does not intersect the image.", nameof(rect));
        var result = new PixelBuffer(r.Width, r.Height);
        for (int y = 0; y < r.Height; y++)
            Data.AsSpan((r.Y + y) * Stride + r.X * 4, r.Width * 4).CopyTo(result.Row(y));
        return result;
    }

    /// <summary>Copies <paramref name="source"/> into this image at the given position, clipping as needed.</summary>
    public void Blit(PixelBuffer source, int destX, int destY)
    {
        ArgumentNullException.ThrowIfNull(source);
        var target = new PixelRect(destX, destY, source.Width, source.Height).Intersect(Bounds);
        if (target.IsEmpty) return;
        int sx = target.X - destX, sy = target.Y - destY;
        for (int y = 0; y < target.Height; y++)
        {
            source.Data.AsSpan((sy + y) * source.Stride + sx * 4, target.Width * 4)
                .CopyTo(Data.AsSpan((target.Y + y) * Stride + target.X * 4, target.Width * 4));
        }
    }

    /// <summary>Forces every pixel fully opaque. Screen captures from GDI carry undefined alpha.</summary>
    public void MakeOpaque()
    {
        var d = Data;
        for (int i = 3; i < Width * Height * 4; i += 4) d[i] = 255;
    }
}
