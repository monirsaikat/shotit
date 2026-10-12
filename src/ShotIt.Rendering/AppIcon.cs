using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShotIt.Rendering;

/// <summary>
/// The ShotIt logo, drawn as vectors so it is sharp at every size: a rounded gradient tile with
/// four capture-corner brackets and a focus dot.
/// </summary>
public static class AppIcon
{
    public static void Draw(DrawingContext dc, double size)
    {
        ArgumentNullException.ThrowIfNull(dc);
        double s = size;
        var tile = new Rect(s * 0.04, s * 0.04, s * 0.92, s * 0.92);
        var gradient = new LinearGradientBrush(Color.FromRgb(0x3D, 0x7B, 0xFF), Color.FromRgb(0x8A, 0x4D, 0xFF), new Point(0, 0), new Point(1, 1));
        dc.DrawRoundedRectangle(gradient, null, tile, s * 0.22, s * 0.22);

        double thickness = Math.Max(1.2, s * 0.085);
        var pen = new Pen(Brushes.White, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        double a = s * 0.27, b = s * 0.73, arm = s * 0.15;
        void Corner(double x, double y, double dx, double dy)
        {
            var g = new StreamGeometry();
            using (var ctx = g.Open())
            {
                ctx.BeginFigure(new Point(x + dx * arm, y), false, false);
                ctx.LineTo(new Point(x, y), true, true);
                ctx.LineTo(new Point(x, y + dy * arm), true, true);
            }

            dc.DrawGeometry(null, pen, g);
        }

        Corner(a, a, 1, 1);
        Corner(b, a, -1, 1);
        Corner(b, b, -1, -1);
        Corner(a, b, 1, -1);
        if (s >= 20) dc.DrawEllipse(Brushes.White, null, new Point(s / 2, s / 2), s * 0.07, s * 0.07);
    }

    public static BitmapSource Render(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) Draw(dc, size);
        var target = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    public static byte[] RenderPng(int size)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Render(size)));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Builds a multi-resolution .ico file with PNG-compressed entries.</summary>
    public static byte[] RenderIco(params int[] sizes)
    {
        if (sizes is null || sizes.Length == 0) sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var images = sizes.Select(RenderPng).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)sizes.Length);
        int offset = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }

        foreach (var image in images) writer.Write(image);
        writer.Flush();
        return stream.ToArray();
    }
}
