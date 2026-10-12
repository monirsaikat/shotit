using System.Globalization;
using System.Text;

namespace ShotIt.Core.Export;

/// <summary>Writes a one-page PDF whose page is exactly one JPEG image. No external library needed.</summary>
public static class PdfWriter
{
    /// <param name="jpeg">A baseline JPEG (RGB or grayscale).</param>
    /// <param name="pixelWidth">Image width in pixels.</param>
    /// <param name="pixelHeight">Image height in pixels.</param>
    /// <param name="dpi">Controls the physical page size; 96 keeps screen size.</param>
    public static byte[] FromJpeg(byte[] jpeg, int pixelWidth, int pixelHeight, double dpi = 96)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pixelHeight);

        string w = (pixelWidth * 72.0 / dpi).ToString("0.###", CultureInfo.InvariantCulture);
        string h = (pixelHeight * 72.0 / dpi).ToString("0.###", CultureInfo.InvariantCulture);
        string content = $"q {w} 0 0 {h} 0 0 cm /Im0 Do Q";

        using var ms = new MemoryStream(jpeg.Length + 1024);
        var offsets = new List<long>();

        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void BeginObject()
        {
            offsets.Add(ms.Position);
            Write(string.Create(CultureInfo.InvariantCulture, $"{offsets.Count} 0 obj\n"));
        }

        Write("%PDF-1.4\n");
        // A comment line of high-bit bytes marks the file as binary for transfer tools.
        ms.Write([0x25, 0xE2, 0xE3, 0xCF, 0xD3, 0x0A]);
        BeginObject();
        Write("<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        BeginObject();
        Write("<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        BeginObject();
        Write($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {w} {h}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n");
        BeginObject();
        Write(string.Create(CultureInfo.InvariantCulture,
            $"<< /Type /XObject /Subtype /Image /Width {pixelWidth} /Height {pixelHeight} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {jpeg.Length} >>\nstream\n"));
        ms.Write(jpeg);
        Write("\nendstream\nendobj\n");
        BeginObject();
        Write(string.Create(CultureInfo.InvariantCulture, $"<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n"));

        long xref = ms.Position;
        Write(string.Create(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count + 1}\n"));
        Write("0000000000 65535 f \n");
        foreach (long o in offsets) Write(o.ToString("0000000000", CultureInfo.InvariantCulture) + " 00000 n \n");
        Write(string.Create(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"));
        return ms.ToArray();
    }
}
