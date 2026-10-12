using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using ShotIt.Core.Annotations;
using ShotIt.Core.Editing;
using ShotIt.Core.Settings;

namespace ShotIt.Rendering;

/// <summary>
/// The ".shotit" project: the untouched base image plus every annotation as data, so a capture can be
/// re-opened later with its arrows, text and blurs still editable. It is a zip with two entries.
/// </summary>
public static class ProjectFile
{
    public const string Extension = ".shotit";
    private const int FormatVersion = 1;
    private const string DocumentEntry = "document.json";
    private const string BaseEntry = "base.png";

    // A project is read back from disk, so sizes are bounded before anything is allocated.
    private const long MaxDocumentBytes = 256L * 1024 * 1024;
    private const long MaxImageBytes = 512L * 1024 * 1024;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        MaxDepth = 32,
    };

    private sealed class Manifest
    {
        public int Version { get; set; } = FormatVersion;
        public List<Annotation> Items { get; set; } = [];
        public CanvasStyle Canvas { get; set; } = CanvasStyle.None;
        public Watermark? Watermark { get; set; }
    }

    /// <summary>True when the document holds anything beyond its base image.</summary>
    public static bool HasEditableContent(DocState document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return document.Items.Count > 0 || !document.Canvas.IsNone || document.Watermark is not null;
    }

    public static void Save(string path, DocState document)
    {
        ArgumentNullException.ThrowIfNull(document);
        string? folder = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        string temp = path + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                document = BakeRedactions(document);
                var manifest = new Manifest { Items = [.. document.Items], Canvas = document.Canvas, Watermark = document.Watermark };
                using (var stream = zip.CreateEntry(DocumentEntry, CompressionLevel.Fastest).Open())
                    JsonSerializer.Serialize(stream, manifest, Json);
                // PNG is already compressed; storing it avoids a second, pointless pass.
                using (var stream = zip.CreateEntry(BaseEntry, CompressionLevel.NoCompression).Open())
                {
                    byte[] png = ImageCodec.Encode(document.Base, ImageFormat.Png);
                    stream.Write(png);
                }
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try { File.Delete(temp); }
                catch (IOException) { }
            }
        }
    }

    /// <summary>
    /// A redaction promises that the covered pixels are gone. A project keeps the untouched base image,
    /// so redactions are painted into a copy of it and dropped from the annotation list before saving:
    /// nothing under a redaction can be recovered from the file.
    /// </summary>
    public static DocState BakeRedactions(DocState document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.Items.Any(i => i is RedactAnnotation)) return document;
        var image = document.Base.Clone();
        foreach (var redact in document.Items.OfType<RedactAnnotation>())
        {
            int left = Math.Max(0, (int)Math.Floor(redact.Rect.Left)), top = Math.Max(0, (int)Math.Floor(redact.Rect.Top));
            int right = Math.Min(image.Width, (int)Math.Ceiling(redact.Rect.Right)), bottom = Math.Min(image.Height, (int)Math.Ceiling(redact.Rect.Bottom));
            var color = redact.Color.WithAlpha(255);
            for (int y = top; y < bottom; y++)
                for (int x = left; x < right; x++)
                    image.SetPixel(x, y, color);
        }

        return document with { Base = image, Items = document.Items.RemoveAll(i => i is RedactAnnotation) };
    }

    /// <summary>Reads a project. Throws <see cref="InvalidDataException"/> when the file is not a usable project.</summary>
    public static DocState Load(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(file, ZipArchiveMode.Read);
            var documentEntry = zip.GetEntry(DocumentEntry) ?? throw new InvalidDataException("The project has no document.");
            var baseEntry = zip.GetEntry(BaseEntry) ?? throw new InvalidDataException("The project has no image.");
            if (documentEntry.Length > MaxDocumentBytes || baseEntry.Length > MaxImageBytes) throw new InvalidDataException("The project is too large.");

            Manifest manifest;
            using (var stream = documentEntry.Open())
                manifest = JsonSerializer.Deserialize<Manifest>(ReadBounded(stream, MaxDocumentBytes), Json) ?? throw new InvalidDataException("The project document is empty.");
            if (manifest.Version > FormatVersion) throw new InvalidDataException("The project was made by a newer version of ShotIt.");

            byte[] png;
            using (var stream = baseEntry.Open()) png = ReadBounded(stream, MaxImageBytes);
            var image = ImageCodec.Decode(png);
            var items = (manifest.Items ?? []).Where(i => i is not null).ToImmutableList();
            return new DocState(image, items) { Canvas = manifest.Canvas ?? CanvasStyle.None, Watermark = manifest.Watermark };
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException or FormatException)
        {
            throw new InvalidDataException("The project file is damaged.", ex);
        }
    }

    /// <summary>Reads at most <paramref name="limit"/> bytes; the declared length in a zip cannot be trusted.</summary>
    private static byte[] ReadBounded(Stream stream, long limit)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > limit) throw new InvalidDataException("The project is too large.");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}
