using System.Buffers.Binary;
using System.Runtime.InteropServices;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using Tesseract;

namespace ShotIt.Ocr;

/// <summary>
/// Bengali text recognition. Windows has no Bengali recognition pack, so this uses the Tesseract
/// engine and language data that ship inside ShotIt. Like the Windows engine it runs entirely on
/// this computer.
/// </summary>
public static class BengaliRecognizer
{
    /// <summary>The language tag that selects this engine in the settings.</summary>
    public const string Tag = "bn";

    public const string DisplayName = "Bengali (বাংলা)";

    // English is loaded as well, so English words and numbers inside Bengali text are read correctly.
    private const string Languages = "ben+eng";

    // The engine reads best when letters are about 30 pixels tall; text on a screen is 10 to 16.
    private const double MaxScale = 2;
    private const int MaxDimension = 4200;

    // Words the engine itself does not believe in are specks, icons and borders.
    private const double MinConfidence = 25;
    private const double SureConfidence = 0.6;
    private const int GlanceFullSizePixels = 600_000;

    private static readonly object Gate = new();

    // Without this the engine writes progress notes to the console.
    private static readonly Dictionary<string, object> Quiet = new() { ["debug_file"] = "NUL" };

    private static string DataFolder => Path.Combine(AppContext.BaseDirectory, "tessdata");

    // The engine is built with Visual C++ and needs its runtime, which nearly every PC already has.
    private static readonly string[] RuntimeLibraries = ["vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll"];

    private static readonly Lazy<bool> Usable = new(() =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64
        && File.Exists(Path.Combine(DataFolder, "ben.traineddata"))
        && File.Exists(Path.Combine(DataFolder, "eng.traineddata"))
        && File.Exists(Path.Combine(AppContext.BaseDirectory, "x64", "tesseract50.dll"))
        && RuntimeLibraries.All(name => NativeLibrary.TryLoad(name, out _)));

    /// <summary>
    /// False where the engine cannot run: it is only built for 64-bit Intel and AMD processors,
    /// and needs the Microsoft Visual C++ runtime to be installed.
    /// </summary>
    public static bool IsAvailable => Usable.Value;

    /// <summary>True when the tag asks for Bengali, whatever the region ("bn", "bn-BD", "bn-IN").</summary>
    public static bool Handles(string? languageTag) =>
        languageTag is not null
        && (languageTag.Equals(Tag, StringComparison.OrdinalIgnoreCase) || languageTag.StartsWith(Tag + "-", StringComparison.OrdinalIgnoreCase));

    /// <summary>True for Bengali letters and vowel signs. Digits and punctuation do not count.</summary>
    public static bool IsBengali(char c) => c is >= '\u0981' and <= '\u09E3' or '\u09F0' or '\u09F1';

    /// <summary>
    /// True when the text really contains Bengali. Icons and borders are now and then read as a
    /// Bengali letter or two, so only words the engine is sure of are counted, and a few of them
    /// among many others are not enough.
    /// </summary>
    public static bool ContainsBengali(RecognizedText text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int words = 0, bengali = 0;
        foreach (var word in text.Lines.SelectMany(l => l.Words))
        {
            words++;
            if (word.Confidence >= SureConfidence && word.Text.Count(IsBengali) >= 2) bengali++;
        }

        return bengali > 0 && bengali * 5 >= words;
    }

    /// <summary>
    /// A copy of the image with the given words painted over in the colour around them, so that
    /// only what another engine could not read is left.
    /// </summary>
    public static PixelBuffer Without(PixelBuffer image, IEnumerable<RecognizedWord> words)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(words);
        var copy = image.Clone();
        var edge = new List<(int Light, int Offset)>();
        foreach (var word in words)
        {
            int left = Math.Max(0, (int)Math.Floor(word.Bounds.X) - 2), top = Math.Max(0, (int)Math.Floor(word.Bounds.Y) - 2);
            int right = Math.Min(image.Width, (int)Math.Ceiling(word.Bounds.X + word.Bounds.Width) + 2);
            int bottom = Math.Min(image.Height, (int)Math.Ceiling(word.Bounds.Y + word.Bounds.Height) + 2);
            if (right - left < 2 || bottom - top < 2) continue;

            // The background is the colour most of the box edge has: the middle one when sorted by lightness.
            edge.Clear();
            for (int x = left; x < right; x++)
            {
                edge.Add(Light(image, x, top));
                edge.Add(Light(image, x, bottom - 1));
            }

            for (int y = top; y < bottom; y++)
            {
                edge.Add(Light(image, left, y));
                edge.Add(Light(image, right - 1, y));
            }

            edge.Sort();
            var background = image.Data.AsSpan(edge[edge.Count / 2].Offset, 4);
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                    background.CopyTo(copy.Data.AsSpan((y * image.Width + x) * 4, 4));
        }

        return copy;
    }

    private static (int Light, int Offset) Light(PixelBuffer image, int x, int y)
    {
        int at = (y * image.Width + x) * 4;
        return (image.Data[at] + image.Data[at + 1] * 4 + image.Data[at + 2] * 2, at);
    }

    /// <summary>Recognises Bengali and English text. Word bounds are returned in the coordinates of the original image.</summary>
    public static RecognizedText Recognize(PixelBuffer image, CancellationToken cancellationToken = default) =>
        Run(image, Languages, MaxScale, cancellationToken);

    /// <summary>
    /// A reading that is only meant to tell whether an image contains Bengali at all. Large images
    /// are not enlarged for it, which halves the time and still finds any line of Bengali; only
    /// a lone small word can be missed.
    /// </summary>
    public static RecognizedText Glance(PixelBuffer image, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        return Run(image, Languages, (long)image.Width * image.Height > GlanceFullSizePixels ? 1 : MaxScale, cancellationToken);
    }

    private static RecognizedText Run(PixelBuffer image, string languages, double maxScale, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!IsAvailable) throw new OcrUnavailableException("Bengali text recognition is not available on this computer.");
        if (image.Width < 4 || image.Height < 4) return new RecognizedText(string.Empty, [], Tag);
        var (bitmap, scale) = Prepare(image, maxScale);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // The engine is not safe to use from two threads at once.
            lock (Gate)
            {
                using var engine = new TesseractEngine(DataFolder, languages, EngineMode.LstmOnly, [], Quiet, false);
                engine.SetVariable("user_defined_dpi", "300");
                using var pix = Pix.LoadFromMemory(bitmap);
                var lines = Read(engine, pix, PageSegMode.Auto, scale);
                // A single word or a short line is sometimes not seen as a page at all.
                if (lines.Count == 0) lines = Read(engine, pix, PageSegMode.SingleBlock, scale);
                return new RecognizedText(string.Join(Environment.NewLine, lines.Select(l => l.Text)), lines, Tag);
            }
        }
        catch (Exception ex) when (ex is TesseractException or DllNotFoundException or BadImageFormatException or IOException or InvalidOperationException or TypeInitializationException)
        {
            throw new OcrUnavailableException("Bengali text could not be read from this image.", ex);
        }
    }

    private static List<RecognizedLine> Read(TesseractEngine engine, Pix pix, PageSegMode mode, double scale)
    {
        var lines = new List<RecognizedLine>();
        using var page = engine.Process(pix, mode);
        using var iterator = page.GetIterator();
        iterator.Begin();
        var words = new List<RecognizedWord>();
        do
        {
            string? word = iterator.GetText(PageIteratorLevel.Word)?.Trim();
            double confidence = iterator.GetConfidence(PageIteratorLevel.Word);
            if (!string.IsNullOrEmpty(word) && confidence >= MinConfidence && iterator.TryGetBoundingBox(PageIteratorLevel.Word, out var box))
                words.Add(new RecognizedWord(word, new RectD(box.X1 / scale, box.Y1 / scale, box.Width / scale, box.Height / scale), confidence / 100));

            if (iterator.IsAtFinalOf(PageIteratorLevel.TextLine, PageIteratorLevel.Word) && words.Count > 0)
            {
                lines.Add(new RecognizedLine(string.Join(' ', words.Select(w => w.Text)), words));
                words = [];
            }
        }
        while (iterator.Next(PageIteratorLevel.Word));

        if (words.Count > 0) lines.Add(new RecognizedLine(string.Join(' ', words.Select(w => w.Text)), words));
        return lines;
    }

    /// <summary>
    /// Turns the image into what the engine reads best: enlarged, grey, and dark text on a light
    /// background. Returned as an uncompressed bitmap file, which the engine loads directly.
    /// </summary>
    private static (byte[] Bitmap, double Scale) Prepare(PixelBuffer image, double maxScale)
    {
        int longest = Math.Max(image.Width, image.Height);
        double scale = Math.Clamp((double)MaxDimension / longest, 0.1, maxScale);
        if (scale is > 0.9 and < 1.1) scale = 1;
        var source = image;
        if (scale != 1)
        {
            int sw = Math.Max(1, (int)Math.Round(image.Width * scale)), sh = Math.Max(1, (int)Math.Round(image.Height * scale));
            source = ImageEffects.Resize(image, sw, sh);
            scale = (double)sw / image.Width;
        }

        int w = source.Width, h = source.Height;
        var grey = new byte[w * h];
        long dark = 0;
        for (int i = 0, p = 0; i < grey.Length; i++, p += 4)
        {
            int value = (source.Data[p] * 29 + source.Data[p + 1] * 150 + source.Data[p + 2] * 77) >> 8;
            grey[i] = (byte)value;
            if (value < 128) dark++;
        }

        bool invert = dark * 2 > grey.Length;
        const int Header = 14 + 40 + 1024;
        int stride = (w + 3) & ~3;
        var file = new byte[Header + stride * h];
        var span = file.AsSpan();
        "BM"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[2..], file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(span[10..], Header);
        BinaryPrimitives.WriteInt32LittleEndian(span[14..], 40);
        BinaryPrimitives.WriteInt32LittleEndian(span[18..], w);
        BinaryPrimitives.WriteInt32LittleEndian(span[22..], h);
        BinaryPrimitives.WriteInt16LittleEndian(span[26..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(span[28..], 8);
        BinaryPrimitives.WriteInt32LittleEndian(span[34..], stride * h);
        BinaryPrimitives.WriteInt32LittleEndian(span[46..], 256);
        for (int i = 0; i < 256; i++)
        {
            int at = 54 + i * 4;
            file[at] = file[at + 1] = file[at + 2] = (byte)i;
        }

        // Bitmap files store the bottom row first.
        for (int y = 0; y < h; y++)
        {
            var row = span.Slice(Header + (h - 1 - y) * stride, w);
            grey.AsSpan(y * w, w).CopyTo(row);
            if (!invert) continue;
            for (int x = 0; x < w; x++) row[x] = (byte)(255 - row[x]);
        }

        return (file, scale);
    }
}
