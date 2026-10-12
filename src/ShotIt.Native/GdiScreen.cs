using System.ComponentModel;
using System.Runtime.InteropServices;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.Storage.Xps;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ShotIt.Native;

/// <summary>A memory DC with a top-down 32-bit DIB selected into it. Owns and releases every GDI handle.</summary>
internal sealed unsafe class DibSurface : IDisposable
{
    private HDC _dc;
    private HBITMAP _bitmap;
    private HGDIOBJ _previous;

    public DibSurface(int width, int height)
    {
        Width = width;
        Height = height;
        HDC screen = PInvoke.GetDC(default);
        try
        {
            _dc = PInvoke.CreateCompatibleDC(screen);
            if (_dc.IsNull) throw new Win32Exception(Marshal.GetLastPInvokeError(), "CreateCompatibleDC failed.");

            BITMAPINFO info = default;
            info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
            info.bmiHeader.biWidth = width;
            info.bmiHeader.biHeight = -height; // negative = top-down rows
            info.bmiHeader.biPlanes = 1;
            info.bmiHeader.biBitCount = 32;
            info.bmiHeader.biCompression = 0; // BI_RGB

            _bitmap = PInvoke.CreateDIBSection(screen, &info, DIB_USAGE.DIB_RGB_COLORS, out void* bits, default, 0);
            if (_bitmap.IsNull || bits is null)
            {
                int error = Marshal.GetLastPInvokeError();
                PInvoke.DeleteDC(_dc);
                _dc = default;
                throw new Win32Exception(error, $"CreateDIBSection failed for {width}x{height}.");
            }

            Bits = bits;
            _previous = PInvoke.SelectObject(_dc, _bitmap);
        }
        finally
        {
            if (!screen.IsNull) _ = PInvoke.ReleaseDC(default, screen);
        }
    }

    public int Width { get; }
    public int Height { get; }
    public HDC Dc => _dc;
    public void* Bits { get; private set; }

    public void Fill(uint bgra) => new Span<uint>(Bits, Width * Height).Fill(bgra);

    public PixelBuffer ToPixelBuffer()
    {
        var buffer = new PixelBuffer(Width, Height);
        new ReadOnlySpan<byte>(Bits, Width * Height * 4).CopyTo(buffer.Data);
        return buffer;
    }

    public void Dispose()
    {
        if (!_dc.IsNull)
        {
            if (!_previous.IsNull) PInvoke.SelectObject(_dc, _previous);
            PInvoke.DeleteDC(_dc);
            _dc = default;
        }

        if (!_bitmap.IsNull)
        {
            PInvoke.DeleteObject(_bitmap);
            _bitmap = default;
        }

        Bits = null;
    }
}

/// <summary>Screen and window capture through GDI. Works in every session type, including RDP and VMs.</summary>
public static unsafe class GdiScreen
{
    /// <summary>Maximum pixels in one capture (about 1 GiB of BGRA). Guards against absurd virtual desktops.</summary>
    private const long MaxPixels = 268_435_456;

    /// <summary>Copies a rectangle of the virtual screen. Coordinates may be negative.</summary>
    /// <exception cref="Win32Exception">The screen could not be read (secure desktop, no session).</exception>
    public static PixelBuffer Capture(PixelRect rect)
    {
        if (rect.IsEmpty) throw new ArgumentException("Capture rectangle is empty.", nameof(rect));
        if ((long)rect.Width * rect.Height > MaxPixels) throw new ArgumentException("Capture rectangle is too large.", nameof(rect));

        using var surface = new DibSurface(rect.Width, rect.Height);
        HDC screen = PInvoke.GetDC(default);
        if (screen.IsNull) throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetDC failed for the screen.");
        try
        {
            if (!PInvoke.BitBlt(surface.Dc, 0, 0, rect.Width, rect.Height, screen, rect.X, rect.Y, ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "BitBlt from the screen failed.");
        }
        finally
        {
            _ = PInvoke.ReleaseDC(default, screen);
        }

        var buffer = surface.ToPixelBuffer();
        buffer.MakeOpaque(); // GDI leaves the alpha channel undefined
        return buffer;
    }

    /// <summary>
    /// Asks a window to render itself, which also captures parts hidden behind other windows.
    /// Returns null when the window refuses or renders nothing (the caller falls back to a screen crop).
    /// </summary>
    public static PixelBuffer? PrintWindow(nint hwnd)
    {
        var window = (HWND)hwnd;
        if (!PInvoke.IsWindow(window) || !PInvoke.GetWindowRect(window, out RECT rect)) return null;
        int width = rect.right - rect.left, height = rect.bottom - rect.top;
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels) return null;

        using var surface = new DibSurface(width, height);
        const PRINT_WINDOW_FLAGS RenderFullContent = (PRINT_WINDOW_FLAGS)2;
        if (!PInvoke.PrintWindow(window, surface.Dc, RenderFullContent)) return null;

        var buffer = surface.ToPixelBuffer();
        // A window that ignored the request leaves the surface untouched (all zero).
        bool anything = buffer.Data.AsSpan().IndexOfAnyExcept((byte)0) >= 0;
        if (!anything) return null;
        buffer.MakeOpaque();
        return buffer;
    }
}

public sealed record CursorImage(PixelBuffer Image, PixelPoint TopLeft);

/// <summary>Reads the current mouse cursor as an image with real alpha, ready to composite onto a capture.</summary>
public static unsafe class CursorCapture
{
    private const int Canvas = 256;

    public static CursorImage? Capture()
    {
        CURSORINFO info = default;
        info.cbSize = (uint)sizeof(CURSORINFO);
        if (!PInvoke.GetCursorInfo(ref info)) return null;
        if ((info.flags & CURSORINFO_FLAGS.CURSOR_SHOWING) == 0 || info.hCursor.IsNull) return null;

        var icon = (HICON)(nint)info.hCursor.Value;
        if (!PInvoke.GetIconInfo(icon, out ICONINFO iconInfo)) return null;
        try
        {
            // Draw the cursor on black and on white. The difference between the two gives exact alpha
            // for every cursor type (colour, monochrome, alpha-blended) without parsing bitmap formats.
            using var onBlack = new DibSurface(Canvas, Canvas);
            using var onWhite = new DibSurface(Canvas, Canvas);
            onBlack.Fill(0xFF000000);
            onWhite.Fill(0xFFFFFFFF);
            if (!PInvoke.DrawIconEx(onBlack.Dc, 0, 0, icon, 0, 0, 0, default, DI_FLAGS.DI_NORMAL)) return null;
            if (!PInvoke.DrawIconEx(onWhite.Dc, 0, 0, icon, 0, 0, 0, default, DI_FLAGS.DI_NORMAL)) return null;

            var black = new ReadOnlySpan<byte>(onBlack.Bits, Canvas * Canvas * 4);
            var white = new ReadOnlySpan<byte>(onWhite.Bits, Canvas * Canvas * 4);
            var image = new PixelBuffer(Canvas, Canvas);
            int minX = Canvas, minY = Canvas, maxX = -1, maxY = -1;
            for (int y = 0; y < Canvas; y++)
            {
                for (int x = 0; x < Canvas; x++)
                {
                    int i = (y * Canvas + x) * 4;
                    // For each channel: white = c*a + (1-a), black = c*a, so a = 1 - (white - black).
                    int diff = Math.Max(white[i] - black[i], Math.Max(white[i + 1] - black[i + 1], white[i + 2] - black[i + 2]));
                    int alpha;
                    byte b, g, r;
                    if (diff < 0)
                    {
                        // Inverting (XOR) cursor pixels: show them as a solid contrasting colour.
                        alpha = 255; b = g = r = 0;
                    }
                    else
                    {
                        alpha = 255 - diff;
                        if (alpha <= 0) continue;
                        b = (byte)Math.Min(255, black[i] * 255 / alpha);
                        g = (byte)Math.Min(255, black[i + 1] * 255 / alpha);
                        r = (byte)Math.Min(255, black[i + 2] * 255 / alpha);
                    }

                    image.SetPixel(x, y, new ColorRgba(r, g, b, (byte)alpha));
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            if (maxX < 0) return null;
            var used = PixelRect.FromLTRB(minX, minY, maxX + 1, maxY + 1);
            var topLeft = new PixelPoint(
                info.ptScreenPos.X - (int)iconInfo.xHotspot + used.X,
                info.ptScreenPos.Y - (int)iconInfo.yHotspot + used.Y);
            return new CursorImage(image.Crop(used), topLeft);
        }
        finally
        {
            if (!iconInfo.hbmColor.IsNull) PInvoke.DeleteObject(iconInfo.hbmColor);
            if (!iconInfo.hbmMask.IsNull) PInvoke.DeleteObject(iconInfo.hbmMask);
        }
    }

    /// <summary>Alpha-blends the cursor onto a capture whose top-left corner is at <paramref name="captureOrigin"/>.</summary>
    public static void Composite(PixelBuffer target, PixelPoint captureOrigin, CursorImage cursor)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(cursor);
        int ox = cursor.TopLeft.X - captureOrigin.X, oy = cursor.TopLeft.Y - captureOrigin.Y;
        var src = cursor.Image;
        for (int y = 0; y < src.Height; y++)
        {
            int ty = oy + y;
            if (ty < 0 || ty >= target.Height) continue;
            for (int x = 0; x < src.Width; x++)
            {
                int tx = ox + x;
                if (tx < 0 || tx >= target.Width) continue;
                var c = src.GetPixel(x, y);
                if (c.A == 0) continue;
                var d = target.GetPixel(tx, ty);
                int a = c.A, ia = 255 - a;
                target.SetPixel(tx, ty, new ColorRgba(
                    (byte)((c.R * a + d.R * ia) / 255),
                    (byte)((c.G * a + d.G * ia) / 255),
                    (byte)((c.B * a + d.B * ia) / 255),
                    (byte)Math.Max(d.A, c.A)));
            }
        }
    }
}
