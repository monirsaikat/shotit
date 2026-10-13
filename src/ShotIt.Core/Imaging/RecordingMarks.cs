namespace ShotIt.Core.Imaging;

/// <summary>Marks drawn straight onto the frames of a screen recording: click rings and caption labels.</summary>
public static class RecordingMarks
{
    /// <summary>Draws a soft-edged ring centred on a point. Parts outside the image are clipped.</summary>
    /// <param name="opacity">0 (invisible) to 1 (solid).</param>
    public static void DrawRing(PixelBuffer target, int centerX, int centerY, double radius, double thickness, ColorRgba color, double opacity)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (opacity <= 0 || radius <= 0 || thickness <= 0) return;
        double outer = radius + thickness / 2, inner = radius - thickness / 2;
        int reach = (int)Math.Ceiling(outer) + 1;
        int x0 = Math.Max(0, centerX - reach), x1 = Math.Min(target.Width - 1, centerX + reach);
        int y0 = Math.Max(0, centerY - reach), y1 = Math.Min(target.Height - 1, centerY + reach);
        byte[] data = target.Data;
        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                double distance = Math.Sqrt((double)(x - centerX) * (x - centerX) + (double)(y - centerY) * (y - centerY));
                // One pixel of fall-off on each edge keeps the ring smooth.
                double coverage = Math.Clamp(Math.Min(outer - distance, distance - inner) + 0.5, 0, 1) * opacity;
                if (coverage <= 0) continue;
                int i = y * target.Stride + x * 4;
                data[i] = (byte)(data[i] + (color.B - data[i]) * coverage);
                data[i + 1] = (byte)(data[i + 1] + (color.G - data[i + 1]) * coverage);
                data[i + 2] = (byte)(data[i + 2] + (color.R - data[i + 2]) * coverage);
            }
        }
    }

    /// <summary>Blends a picture with straight alpha over the target at the given position, clipping as needed.</summary>
    public static void Overlay(PixelBuffer target, PixelBuffer source, int destX, int destY)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        for (int y = Math.Max(0, -destY); y < source.Height && destY + y < target.Height; y++)
        {
            var from = source.Row(y);
            var to = target.Row(destY + y);
            for (int x = Math.Max(0, -destX); x < source.Width && destX + x < target.Width; x++)
            {
                int a = from[x * 4 + 3];
                if (a == 0) continue;
                int i = (destX + x) * 4;
                to[i] = (byte)((from[x * 4] * a + to[i] * (255 - a)) / 255);
                to[i + 1] = (byte)((from[x * 4 + 1] * a + to[i + 1] * (255 - a)) / 255);
                to[i + 2] = (byte)((from[x * 4 + 2] * a + to[i + 2] * (255 - a)) / 255);
            }
        }
    }
}

/// <summary>
/// Turns the keys that are held down into the caption a recording shows, such as "Ctrl + Shift + S".
/// Only shortcuts and navigation keys are ever named. Letters, digits and symbols typed on their own
/// are left out, so a recording never spells out a password.
/// </summary>
public static class KeyCaption
{
    private const int Shift = 0x10, Control = 0x11, Alt = 0x12, RightAlt = 0xA5, LeftWin = 0x5B, RightWin = 0x5C;

    private static readonly (int Key, string Name)[] Named =
    [
        (0x0D, "Enter"), (0x09, "Tab"), (0x08, "Backspace"), (0x2E, "Delete"), (0x2D, "Insert"),
        (0x24, "Home"), (0x23, "End"), (0x21, "Page Up"), (0x22, "Page Down"),
        (0x25, "←"), (0x26, "↑"), (0x27, "→"), (0x28, "↓"), (0x2C, "Print Screen"),
    ];

    /// <param name="isDown">Answers whether the key with a given Windows virtual-key code is held down.</param>
    public static string? Describe(Func<int, bool> isDown)
    {
        ArgumentNullException.ThrowIfNull(isDown);
        bool control = isDown(Control), alt = isDown(Alt), shift = isDown(Shift), win = isDown(LeftWin) || isDown(RightWin);
        // AltGr reports as Ctrl with the right Alt key, and it types characters rather than giving commands.
        bool typing = !win && (!(control || alt) || (control && isDown(RightAlt)));

        string? key = null;
        foreach (var (code, name) in Named)
            if (isDown(code)) key = name;
        for (int f = 1; f <= 24 && key is null; f++)
            if (isDown(0x6F + f)) key = "F" + f.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (key is null && !typing)
        {
            for (int code = 'A'; code <= 'Z' && key is null; code++)
                if (isDown(code)) key = ((char)code).ToString();
            for (int code = '0'; code <= '9' && key is null; code++)
                if (isDown(code)) key = ((char)code).ToString();
            if (key is null && isDown(0x20)) key = "Space";
            if (key is null && isDown(0x1B)) key = "Esc";
        }

        if (key is null) return null;
        var parts = new List<string>(5);
        if (control) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        if (win) parts.Add("Win");
        parts.Add(key);
        return string.Join(" + ", parts);
    }
}
