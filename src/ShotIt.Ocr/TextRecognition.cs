using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using ZXing;
using ZXing.Common;

namespace ShotIt.Ocr;

/// <param name="Confidence">How sure the engine is, from 0 to 1. The Windows engine does not say, so its words are 1.</param>
public sealed record RecognizedWord(string Text, RectD Bounds, double Confidence = 1);

public sealed record RecognizedLine(string Text, IReadOnlyList<RecognizedWord> Words);

public sealed record RecognizedText(string Text, IReadOnlyList<RecognizedLine> Lines, string Language)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}

public sealed record OcrLanguage(string Tag, string DisplayName);

/// <summary>Thrown when text recognition cannot run, with a message suitable for the user.</summary>
public sealed class OcrUnavailableException : Exception
{
    public OcrUnavailableException()
    {
    }

    public OcrUnavailableException(string message)
        : base(message)
    {
    }

    public OcrUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Text recognition with the engine built into Windows, and with the engine inside ShotIt for
/// Bengali, which Windows cannot read. Everything runs on this computer: no image ever leaves
/// the machine.
/// </summary>
public static class TextRecognizer
{
    /// <summary>Languages that can be read: those with a recognition pack installed in Windows, and Bengali.</summary>
    public static IReadOnlyList<OcrLanguage> AvailableLanguages()
    {
        var languages = new List<OcrLanguage>();
        try
        {
            languages.AddRange(OcrEngine.AvailableRecognizerLanguages.Select(l => new OcrLanguage(l.LanguageTag, l.DisplayName)));
        }
        catch (Exception)
        {
        }

        if (BengaliRecognizer.IsAvailable) languages.Add(new OcrLanguage(BengaliRecognizer.Tag, BengaliRecognizer.DisplayName));
        return languages;
    }

    private static OcrEngine CreateEngine(string? languageTag)
    {
        OcrEngine? engine = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(languageTag) && Language.IsWellFormed(languageTag))
            {
                var language = new Language(languageTag);
                if (OcrEngine.IsLanguageSupported(language)) engine = OcrEngine.TryCreateFromLanguage(language);
            }

            engine ??= OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine is null && OcrEngine.AvailableRecognizerLanguages.Count > 0)
                engine = OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages[0]);
        }
        catch (Exception ex)
        {
            throw new OcrUnavailableException("Windows text recognition could not be started.", ex);
        }

        return engine ?? throw new OcrUnavailableException(
            "No text recognition language is installed. Add one in Windows Settings › Time & language › Language & region (choose a language, then its optional features).");
    }

    /// <summary>
    /// Scales the image into the range the engine handles well: it rejects very large images and
    /// misses small text, so small captures are enlarged and huge ones reduced.
    /// </summary>
    public static (PixelBuffer Image, double Scale) Prepare(PixelBuffer image, int maxDimension)
    {
        ArgumentNullException.ThrowIfNull(image);
        int longest = Math.Max(image.Width, image.Height);
        double scale = 1;
        if (longest > maxDimension) scale = (double)maxDimension / longest;
        else if (longest * 2 <= Math.Min(maxDimension, 4000) && image.Height < 1400) scale = 2;
        // The engine also refuses images narrower than about 40 pixels.
        if (Math.Min(image.Width, image.Height) * scale < 40) scale = Math.Min(40.0 / Math.Min(image.Width, image.Height), (double)maxDimension / longest);
        if (Math.Abs(scale - 1) < 1e-9) return (image, 1);
        int w = Math.Clamp((int)Math.Round(image.Width * scale), 1, maxDimension);
        int h = Math.Clamp((int)Math.Round(image.Height * scale), 1, maxDimension);
        return (ImageEffects.Resize(image, w, h), (double)w / image.Width);
    }

    /// <summary>
    /// Recognises text. Word bounds are returned in the coordinates of the original image.
    /// With no language given, Windows reads the image first and what it could not read is then
    /// checked for Bengali; if there is any, the whole image is read as Bengali and English.
    /// </summary>
    public static async Task<RecognizedText> RecognizeAsync(PixelBuffer image, string? languageTag = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width < 4 || image.Height < 4) return new RecognizedText(string.Empty, [], string.Empty);
        if (BengaliRecognizer.Handles(languageTag)) return BengaliRecognizer.Recognize(image, cancellationToken);
        if (!string.IsNullOrWhiteSpace(languageTag) || !BengaliRecognizer.IsAvailable)
            return await WindowsAsync(image, languageTag, cancellationToken).ConfigureAwait(false);

        RecognizedText windows;
        try
        {
            windows = await WindowsAsync(image, languageTag, cancellationToken).ConfigureAwait(false);
        }
        catch (OcrUnavailableException)
        {
            // No Windows recognition language is installed: the engine inside ShotIt still reads English.
            return BengaliRecognizer.Recognize(image, cancellationToken);
        }

        try
        {
            var read = windows.Lines.SelectMany(l => l.Words).ToList();
            var rest = BengaliRecognizer.Glance(read.Count == 0 ? image : BengaliRecognizer.Without(image, read), cancellationToken);
            return BengaliRecognizer.ContainsBengali(rest) ? BengaliRecognizer.Recognize(image, cancellationToken) : windows;
        }
        catch (OcrUnavailableException)
        {
            return windows;
        }
    }

    private static async Task<RecognizedText> WindowsAsync(PixelBuffer image, string? languageTag, CancellationToken cancellationToken)
    {
        var engine = CreateEngine(languageTag);
        var (prepared, scale) = Prepare(image, (int)OcrEngine.MaxImageDimension);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(prepared.Data.AsBuffer(), BitmapPixelFormat.Bgra8, prepared.Width, prepared.Height, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap).AsTask(cancellationToken).ConfigureAwait(false);

            var lines = new List<RecognizedLine>(result.Lines.Count);
            var text = new StringBuilder();
            foreach (var line in result.Lines)
            {
                var words = line.Words
                    .Select(w => new RecognizedWord(w.Text, new RectD(w.BoundingRect.X / scale, w.BoundingRect.Y / scale, w.BoundingRect.Width / scale, w.BoundingRect.Height / scale)))
                    .ToList();
                lines.Add(new RecognizedLine(line.Text, words));
                if (text.Length > 0) text.Append(Environment.NewLine);
                text.Append(line.Text);
            }

            return new RecognizedText(text.ToString(), lines, engine.RecognizerLanguage.LanguageTag);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new OcrUnavailableException("Windows could not read text from this image.", ex);
        }
    }
}

public sealed record ScannedCode(string Text, string Format)
{
    /// <summary>True when the content is an http or https address that is safe to offer as a link.</summary>
    public bool IsWebLink => Uri.TryCreate(Text.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}

/// <summary>Finds QR codes and common barcodes in an image.</summary>
public static class CodeScanner
{
    // UPC-E, ITF and Codabar are left out on purpose: their checks are so weak that ordinary
    // text in a screenshot is regularly "decoded" as one of them.
    private static readonly BarcodeFormat[] Formats =
    [
        BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC, BarcodeFormat.PDF_417,
        BarcodeFormat.EAN_13, BarcodeFormat.UPC_A, BarcodeFormat.CODE_128, BarcodeFormat.CODE_93,
    ];

    public static IReadOnlyList<ScannedCode> Scan(PixelBuffer image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Width < 8 || image.Height < 8) return [];
        var found = new List<ScannedCode>();
        // Codes drawn light-on-dark or very small are common in screenshots, so several passes are tried.
        foreach (var candidate in Candidates(image))
        {
            Collect(candidate, found);
            if (found.Count > 0) break;
        }

        return found;
    }

    private static IEnumerable<PixelBuffer> Candidates(PixelBuffer image)
    {
        yield return image;
        int longest = Math.Max(image.Width, image.Height);
        if (longest <= 1200) yield return ImageEffects.Resize(image, image.Width * 2, image.Height * 2);
        if (longest > 2400)
        {
            double scale = 1600.0 / longest;
            yield return ImageEffects.Resize(image, Math.Max(8, (int)(image.Width * scale)), Math.Max(8, (int)(image.Height * scale)));
        }
    }

    private static void Collect(PixelBuffer image, List<ScannedCode> found)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, TryInverted = true, PossibleFormats = Formats },
        };
        Result[]? results;
        try
        {
            var source = new RGBLuminanceSource(image.Data, image.Width, image.Height, RGBLuminanceSource.BitmapFormat.BGRA32);
            results = reader.DecodeMultiple(source);
            if (results is null || results.Length == 0)
            {
                var single = reader.Decode(source);
                results = single is null ? null : [single];
            }
        }
        catch (Exception ex) when (ex is ReaderException or ArgumentException or IndexOutOfRangeException)
        {
            return;
        }

        if (results is null) return;
        foreach (var result in results)
        {
            if (string.IsNullOrEmpty(result?.Text)) continue;
            if (found.Any(f => f.Text == result.Text)) continue;
            found.Add(new ScannedCode(result.Text, FormatName(result.BarcodeFormat)));
        }
    }

    private static string FormatName(BarcodeFormat format) => format switch
    {
        BarcodeFormat.QR_CODE => "QR code",
        BarcodeFormat.DATA_MATRIX => "Data Matrix",
        BarcodeFormat.AZTEC => "Aztec",
        BarcodeFormat.PDF_417 => "PDF417",
        _ => format.ToString().Replace('_', '-'),
    };
}
