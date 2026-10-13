using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using ShotIt.Ocr;

namespace ShotIt.Ocr.Tests;

public class BengaliRecognizerTests
{
    private const string Font = "Nirmala UI";

    private static readonly string[] Anthem = ["আমার সোনার বাংলা, আমি তোমায় ভালোবাসি", "চিরদিন তোমার আকাশ, তোমার বাতাস", "বাংলাদেশের রাজধানী ঢাকা ২০২৬"];

    private static bool Available => BengaliRecognizer.IsAvailable;

    private static bool HasWindowsEngine => TextRecognizer.AvailableLanguages().Any(l => !BengaliRecognizer.Handles(l.Tag));

    private static RecognizedText Words(params (string Text, double Confidence)[] words) =>
        new(string.Join(' ', words.Select(w => w.Text)), [new RecognizedLine(string.Empty, [.. words.Select(w => new RecognizedWord(w.Text, default, w.Confidence))])], BengaliRecognizer.Tag);

    [Fact]
    public void BengaliIsOfferedAsALanguage()
    {
        if (!Available) return;
        Assert.Contains(TextRecognizer.AvailableLanguages(), l => l.Tag == BengaliRecognizer.Tag);
    }

    [Theory]
    [InlineData("bn", true)]
    [InlineData("bn-BD", true)]
    [InlineData("BN-in", true)]
    [InlineData("bnt", false)]
    [InlineData("en-US", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void BengaliTagsAreRecognised(string? tag, bool handled) => Assert.Equal(handled, BengaliRecognizer.Handles(tag));

    [Fact]
    public async Task ReadsBengaliText()
    {
        if (!Available) return;
        var result = await TextRecognizer.RecognizeAsync(TestImages.Text(Anthem, fontSize: 14, width: 700, font: Font), "bn");
        Assert.Equal(BengaliRecognizer.Tag, result.Language);
        Assert.Equal(3, result.Lines.Count);
        foreach (string word in new[] { "আমার", "সোনার", "বাংলা", "তোমায়", "ভালোবাসি", "চিরদিন", "আকাশ", "বাতাস", "বাংলাদেশের", "রাজধানী", "ঢাকা", "২০২৬" })
            Assert.Contains(word, result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadsLightOnDarkBengali()
    {
        if (!Available) return;
        var result = await TextRecognizer.RecognizeAsync(TestImages.Text(Anthem, fontSize: 16, dark: true, width: 700, font: Font), "bn-BD");
        Assert.Contains("সোনার বাংলা", result.Text, StringComparison.Ordinal);
        Assert.Contains("রাজধানী ঢাকা", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticLanguageFindsBengali()
    {
        if (!Available) return;
        var result = await TextRecognizer.RecognizeAsync(TestImages.Text(Anthem, fontSize: 12, width: 700, font: Font));
        Assert.Equal(BengaliRecognizer.Tag, result.Language);
        Assert.Contains("সোনার বাংলা", result.Text, StringComparison.Ordinal);

        var word = await TextRecognizer.RecognizeAsync(TestImages.Text(["বাংলাদেশ"], fontSize: 14, width: 200, font: Font));
        Assert.Equal("বাংলাদেশ", word.Text.Trim());
    }

    [Fact]
    public async Task AutomaticLanguageReadsBengaliAndEnglishTogether()
    {
        if (!Available) return;
        string[] lines = ["Quarterly revenue report for the whole year", "Invoice number 48213 was paid on time", "এই লাইনটি বাংলায় লেখা", "Settings saved successfully"];
        var result = await TextRecognizer.RecognizeAsync(TestImages.Text(lines, fontSize: 14, width: 900, font: Font));
        Assert.Equal(BengaliRecognizer.Tag, result.Language);
        Assert.Contains("বাংলায় লেখা", result.Text, StringComparison.Ordinal);
        Assert.Contains("Quarterly revenue report", result.Text, StringComparison.Ordinal);
        Assert.Contains("48213", result.Text, StringComparison.Ordinal);
        Assert.Contains("Settings saved successfully", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutomaticLanguageLeavesEnglishToWindows()
    {
        if (!Available || !HasWindowsEngine) return;
        var result = await TextRecognizer.RecognizeAsync(TestImages.Text(["Quarterly revenue report", "Invoice number 48213", "Thank you for your order"]));
        Assert.NotEqual(BengaliRecognizer.Tag, result.Language);
        Assert.Equal(3, result.Lines.Count);
    }

    [Fact]
    public async Task AChosenWindowsLanguageIsNotOverridden()
    {
        if (!Available || !HasWindowsEngine) return;
        string tag = TextRecognizer.AvailableLanguages().First(l => !BengaliRecognizer.Handles(l.Tag)).Tag;
        var result = await TextRecognizer.RecognizeAsync(TestImages.Text(Anthem, fontSize: 14, width: 700, font: Font), tag);
        Assert.NotEqual(BengaliRecognizer.Tag, result.Language);
        Assert.DoesNotContain(result.Text, BengaliRecognizer.IsBengali);
    }

    [Fact]
    public void WordBoundsAreInOriginalImageCoordinates()
    {
        if (!Available) return;
        var image = TestImages.Text(["বাংলাদেশ ঢাকা"], fontSize: 14, width: 300, font: Font);
        var words = BengaliRecognizer.Recognize(image).Lines.SelectMany(l => l.Words).ToList();
        Assert.Equal(2, words.Count);
        // The text was drawn at x = 20, y = 20.
        Assert.InRange(words[0].Bounds.X, 12, 30);
        Assert.InRange(words[0].Bounds.Y, 12, 40);
        Assert.True(words[1].Bounds.X > words[0].Bounds.X + words[0].Bounds.Width);
        Assert.All(words, w => Assert.InRange(w.Bounds.X + w.Bounds.Width, 0, image.Width));
        Assert.All(words, w => Assert.InRange(w.Confidence, 0.25, 1));
    }

    [Fact]
    public void BlankAndTinyImagesGiveEmptyText()
    {
        if (!Available) return;
        var blank = new PixelBuffer(300, 200);
        blank.Fill(ColorRgba.White);
        Assert.True(BengaliRecognizer.Recognize(blank).IsEmpty);
        Assert.True(BengaliRecognizer.Recognize(new PixelBuffer(2, 2)).IsEmpty);
    }

    [Fact]
    public void OnlyTextTheEngineIsSureOfCountsAsBengali()
    {
        Assert.True(BengaliRecognizer.ContainsBengali(Words(("বাংলাদেশ", 0.9))));
        Assert.True(BengaliRecognizer.ContainsBengali(Words(("ShotIt", 0.9), ("দিয়ে", 0.8), ("স্ক্রিনশট", 0.9), ("নিন", 0.7))));
        // What icons and borders are mistaken for: unsure, a single letter, or lost among other words.
        Assert.False(BengaliRecognizer.ContainsBengali(Words(("দির", 0.4), ("নক", 0.3))));
        Assert.False(BengaliRecognizer.ContainsBengali(Words(("ও", 0.95))));
        Assert.False(BengaliRecognizer.ContainsBengali(Words(("২০২৬", 0.95), ("৷", 0.9))));
        Assert.False(BengaliRecognizer.ContainsBengali(Words(("চা", 0.7), ("a", 0.9), ("b", 0.9), ("c", 0.9), ("d", 0.9), ("e", 0.9))));
        Assert.False(BengaliRecognizer.ContainsBengali(Words(("Invoice", 0.9), ("48213", 0.9))));
        Assert.False(BengaliRecognizer.ContainsBengali(new RecognizedText(string.Empty, [], BengaliRecognizer.Tag)));
    }

    [Fact]
    public void PaintingOverWordsLeavesTheRestAlone()
    {
        var image = new PixelBuffer(200, 100);
        image.Fill(new ColorRgba(30, 40, 50));
        for (int x = 20; x < 60; x += 4)
            for (int y = 20; y < 32; y++)
                image.SetPixel(x, y, ColorRgba.White);
        for (int x = 120; x < 160; x += 4)
            for (int y = 60; y < 72; y++)
                image.SetPixel(x, y, ColorRgba.White);

        var cleaned = BengaliRecognizer.Without(image, [new RecognizedWord("word", new RectD(20, 20, 40, 12))]);
        Assert.Equal(new ColorRgba(30, 40, 50), cleaned.GetPixel(20, 25));
        Assert.Equal(new ColorRgba(30, 40, 50), cleaned.GetPixel(56, 31));
        Assert.Equal(ColorRgba.White, cleaned.GetPixel(120, 65));
        Assert.Equal(ColorRgba.White, image.GetPixel(20, 25));

        // Words at the very edge, or outside the image, must not break it.
        var edge = BengaliRecognizer.Without(image, [new RecognizedWord("a", new RectD(-5, -5, 20, 20)), new RecognizedWord("b", new RectD(190, 95, 40, 40)), new RecognizedWord("c", new RectD(500, 500, 10, 10))]);
        Assert.Equal(image.Width, edge.Width);
    }
}
