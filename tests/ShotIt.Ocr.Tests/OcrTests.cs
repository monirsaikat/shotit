using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShotIt.Core.Imaging;
using ShotIt.Ocr;
using ShotIt.Rendering;
using ZXing;
using ZXing.Common;

namespace ShotIt.Ocr.Tests;

internal static class TestImages
{
    public static T OnSta<T>(Func<T> action)
    {
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }

    /// <summary>Draws lines of text the way a screenshot of a document would look.</summary>
    public static PixelBuffer Text(string[] lines, double fontSize = 20, bool dark = false, int width = 640) => OnSta(() =>
    {
        int height = (int)(lines.Length * fontSize * 1.8 + 40);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(dark ? Brushes.Black : Brushes.White, null, new Rect(0, 0, width, height));
            double y = 20;
            foreach (string line in lines)
            {
                var text = new FormattedText(line, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), fontSize, dark ? Brushes.White : Brushes.Black, 1.0);
                dc.DrawText(text, new Point(20, y));
                y += fontSize * 1.8;
            }
        }

        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return target.ToPixelBuffer();
    });

    public static PixelBuffer Code(string content, BarcodeFormat format, int size = 240, bool inverted = false)
    {
        var writer = new BarcodeWriterGeneric { Format = format, Options = new EncodingOptions { Width = size, Height = format == BarcodeFormat.QR_CODE ? size : size / 3, Margin = 4 } };
        var matrix = writer.Encode(content);
        var image = new PixelBuffer(matrix.Width, matrix.Height);
        for (int y = 0; y < matrix.Height; y++)
            for (int x = 0; x < matrix.Width; x++)
                image.SetPixel(x, y, matrix[x, y] ^ inverted ? ColorRgba.Black : ColorRgba.White);
        return image;
    }
}

public class TextRecognizerTests
{
    private static bool HasEngine => TextRecognizer.AvailableLanguages().Count > 0;

    private static string Normalize(string text) => new(text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    [Fact]
    public async Task ReadsOrdinaryScreenText()
    {
        if (!HasEngine) return;
        var image = TestImages.Text(["Quarterly revenue report", "Invoice number 48213", "Thank you for your order"]);
        var result = await TextRecognizer.RecognizeAsync(image);
        string flat = Normalize(result.Text);
        Assert.Contains("quarterlyrevenuereport", flat, StringComparison.Ordinal);
        Assert.Contains("48213", flat, StringComparison.Ordinal);
        Assert.Contains("thankyouforyourorder", flat, StringComparison.Ordinal);
        Assert.Equal(3, result.Lines.Count);
        Assert.False(result.IsEmpty);
        Assert.NotEmpty(result.Language);
    }

    [Fact]
    public async Task ReadsSmallAndLightOnDarkText()
    {
        if (!HasEngine) return;
        var small = await TextRecognizer.RecognizeAsync(TestImages.Text(["Settings saved successfully"], fontSize: 11));
        Assert.Contains("settingssavedsuccessfully", Normalize(small.Text), StringComparison.Ordinal);
        var dark = await TextRecognizer.RecognizeAsync(TestImages.Text(["Dark mode terminal output"], dark: true));
        Assert.Contains("darkmodeterminaloutput", Normalize(dark.Text), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WordBoundsAreInOriginalImageCoordinates()
    {
        if (!HasEngine) return;
        var image = TestImages.Text(["Alpha Bravo"], fontSize: 14);
        var result = await TextRecognizer.RecognizeAsync(image);
        var words = result.Lines.SelectMany(l => l.Words).ToList();
        Assert.NotEmpty(words);
        Assert.All(words, w =>
        {
            Assert.InRange(w.Bounds.X, 0, image.Width);
            Assert.InRange(w.Bounds.Y, 0, image.Height);
            Assert.InRange(w.Bounds.X + w.Bounds.Width, 0, image.Width + 1);
            Assert.InRange(w.Bounds.Y + w.Bounds.Height, 0, image.Height + 1);
        });
        // The text was drawn at x = 20, y = 20.
        Assert.InRange(words[0].Bounds.X, 12, 32);
        Assert.InRange(words[0].Bounds.Y, 14, 40);
    }

    [Fact]
    public async Task BlankAndTinyImagesGiveEmptyText()
    {
        if (!HasEngine) return;
        var blank = new PixelBuffer(300, 200);
        blank.Fill(ColorRgba.White);
        Assert.True((await TextRecognizer.RecognizeAsync(blank)).IsEmpty);
        Assert.True((await TextRecognizer.RecognizeAsync(new PixelBuffer(2, 2))).IsEmpty);
        var sliver = new PixelBuffer(600, 6);
        sliver.Fill(ColorRgba.White);
        Assert.True((await TextRecognizer.RecognizeAsync(sliver)).IsEmpty);
    }

    [Fact]
    public async Task UnknownLanguageFallsBackToAnInstalledOne()
    {
        if (!HasEngine) return;
        var result = await TextRecognizer.RecognizeAsync(TestImages.Text(["Fallback works"]), "xx-not-a-language");
        Assert.Contains("fallbackworks", Normalize(result.Text), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(200, 100, 2.0)]
    [InlineData(1920, 1080, 2.0)]
    [InlineData(3000, 1000, 1.0)]
    [InlineData(1000, 3000, 1.0)]
    [InlineData(20000, 1000, 0.5)]
    [InlineData(1000, 10, 4.0)]
    public void PrepareKeepsImagesInsideTheEngineLimits(int width, int height, double expectedScale)
    {
        var (image, scale) = TextRecognizer.Prepare(new PixelBuffer(width, height), 10000);
        Assert.Equal(expectedScale, scale, 2);
        Assert.InRange(Math.Max(image.Width, image.Height), 1, 10000);
    }
}

public class CodeScannerTests
{
    [Fact]
    public void ReadsAQrCode()
    {
        var codes = CodeScanner.Scan(TestImages.Code("https://example.com/path?q=1", BarcodeFormat.QR_CODE));
        var code = Assert.Single(codes);
        Assert.Equal("https://example.com/path?q=1", code.Text);
        Assert.Equal("QR code", code.Format);
        Assert.True(code.IsWebLink);
    }

    [Fact]
    public void ReadsAnInvertedQrCode()
    {
        var codes = CodeScanner.Scan(TestImages.Code("light on dark", BarcodeFormat.QR_CODE, inverted: true));
        Assert.Equal("light on dark", Assert.Single(codes).Text);
        Assert.False(codes[0].IsWebLink);
    }

    [Fact]
    public void ReadsACodeInsideALargerScreenshot()
    {
        var page = new PixelBuffer(1400, 900);
        page.Fill(new ColorRgba(245, 245, 245));
        page.Blit(TestImages.Code("WIFI:S:Office;T:WPA;P:secret;;", BarcodeFormat.QR_CODE, 200), 900, 500);
        Assert.Equal("WIFI:S:Office;T:WPA;P:secret;;", Assert.Single(CodeScanner.Scan(page)).Text);
    }

    [Fact]
    public void ReadsALinearBarcode()
    {
        var codes = CodeScanner.Scan(TestImages.Code("SHOTIT-12345", BarcodeFormat.CODE_128, 420));
        Assert.Equal("SHOTIT-12345", Assert.Single(codes).Text);
    }

    [Fact]
    public void ImagesWithoutCodesGiveNothing()
    {
        var blank = new PixelBuffer(400, 300);
        blank.Fill(ColorRgba.White);
        Assert.Empty(CodeScanner.Scan(blank));
        Assert.Empty(CodeScanner.Scan(new PixelBuffer(3, 3)));
        var fromText = CodeScanner.Scan(TestImages.Text(["No codes here, only words"]));
        Assert.True(fromText.Count == 0, string.Join(" | ", fromText.Select(c => c.Format + ":" + c.Text)));
    }

    [Theory]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows", false)]
    [InlineData("  https://a.example  ", true)]
    public void OnlyWebAddressesAreOfferedAsLinks(string text, bool isLink) => Assert.Equal(isLink, new ScannedCode(text, "QR code").IsWebLink);
}
