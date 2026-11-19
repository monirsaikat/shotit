using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Vortice.WIC;
using Rect = System.Windows.Rect;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using WicPixelFormat = Vortice.WIC.PixelFormat;

namespace ShotIt.Rendering;

/// <summary>
/// Draws emoji in colour. WPF only draws emoji as black outlines, so each emoji is rendered once by
/// DirectWrite (which understands the colour layers of Segoe UI Emoji) and cached as a bitmap.
/// </summary>
public static class EmojiRenderer
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<(string Text, int Size), BitmapSource?> Cache = [];
    private static ID2D1Factory? _d2d;
    private static IDWriteFactory? _dwrite;
    private static IWICImagingFactory? _wic;
    private static bool _failed;

    /// <summary>Text that is an emoji (or a run of them) rather than one of the built-in sticker names.</summary>
    public static bool IsEmoji(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 32) return false;
        foreach (var rune in text.EnumerateRunes())
        {
            int v = rune.Value;
            bool emoji = v >= 0x1F000 || (v >= 0x2190 && v <= 0x2BFF) || (v >= 0x2300 && v <= 0x23FF) || v is 0x00A9 or 0x00AE or 0x203C or 0x2049 or 0x2122 or 0x2139 or 0x3030 or 0x303D or 0x3297 or 0x3299;
            bool joiner = v is 0x200D or 0xFE0F or 0xFE0E or 0x20E3 || (v >= 0xE0020 && v <= 0xE007F);
            bool keycap = v is '#' or '*' || (v >= '0' && v <= '9');
            if (!emoji && !joiner && !keycap) return false;
        }

        return true;
    }

    /// <summary>
    /// The emoji as a square colour bitmap at least <paramref name="minimumSize"/> pixels wide, or null when
    /// DirectWrite is unavailable. Sizes are rounded up to a few fixed steps so the cache stays small.
    /// </summary>
    public static BitmapSource? Get(string text, double minimumSize)
    {
        ArgumentNullException.ThrowIfNull(text);
        int size = minimumSize <= 32 ? 32 : minimumSize <= 64 ? 64 : minimumSize <= 128 ? 128 : minimumSize <= 256 ? 256 : 512;
        lock (Gate)
        {
            if (_failed) return null;
            if (Cache.TryGetValue((text, size), out var cached)) return cached;
            BitmapSource? bitmap;
            try
            {
                bitmap = Render(text, size);
            }
            catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or DllNotFoundException or EntryPointNotFoundException or System.Runtime.InteropServices.COMException)
            {
                // No Direct2D (some server and remote sessions): stickers fall back to WPF text.
                _failed = true;
                return null;
            }

            if (Cache.Count > 400) Cache.Clear();
            Cache[(text, size)] = bitmap;
            return bitmap;
        }
    }

    /// <summary>
    /// Draws the emoji filling <paramref name="rect"/> (as a centred square). The bitmap is twice the size
    /// so the emoji stays sharp when the editor is zoomed in or the screen is scaled.
    /// </summary>
    public static bool Draw(DrawingContext dc, string text, Rect rect)
    {
        ArgumentNullException.ThrowIfNull(dc);
        double s = Math.Min(rect.Width, rect.Height);
        if (s < 1) return true;
        var bitmap = Get(text, s * 2);
        if (bitmap is null) return false;
        dc.DrawImage(bitmap, new Rect(rect.X + (rect.Width - s) / 2, rect.Y + (rect.Height - s) / 2, s, s));
        return true;
    }

    private static BitmapSource Render(string text, int size)
    {
        _d2d ??= D2D1.D2D1CreateFactory<ID2D1Factory>(Vortice.Direct2D1.FactoryType.MultiThreaded);
        _dwrite ??= DWrite.DWriteCreateFactory<IDWriteFactory>(Vortice.DirectWrite.FactoryType.Shared);
        _wic ??= new IWICImagingFactory();

        using var target = _wic.CreateBitmap((uint)size, (uint)size, WicPixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
        var properties = new RenderTargetProperties(new D2DPixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));
        using (var rt = _d2d.CreateWicBitmapRenderTarget(target, properties))
        using (var format = _dwrite.CreateTextFormat("Segoe UI Emoji", null, Vortice.DirectWrite.FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, Vortice.DirectWrite.FontStretch.Normal, size * 0.74f, CultureInfo.CurrentUICulture.Name.Length > 0 ? CultureInfo.CurrentUICulture.Name : "en-us"))
        using (var brush = rt.CreateSolidColorBrush(new Color4(0.13f, 0.13f, 0.13f, 1f)))
        {
            format.TextAlignment = Vortice.DirectWrite.TextAlignment.Center;
            format.ParagraphAlignment = ParagraphAlignment.Center;
            format.WordWrapping = WordWrapping.NoWrap;
            rt.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
            rt.BeginDraw();
            rt.Clear(new Color4(0, 0, 0, 0));
            // A wide box lets a sequence that the font cannot join still be drawn without wrapping.
            rt.DrawText(text, format, new Vortice.Mathematics.Rect(-size, 0, size * 3, size), brush, DrawTextOptions.EnableColorFont | DrawTextOptions.NoSnap);
            rt.EndDraw();
        }

        int stride = size * 4;
        byte[] pixels = new byte[stride * size];
        target.CopyPixels((uint)stride, pixels);
        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bitmap.Freeze();
        return bitmap;
    }
}
