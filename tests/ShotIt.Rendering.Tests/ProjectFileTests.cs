using System.IO;
using System.IO.Compression;
using System.Text;
using ShotIt.Core.Annotations;
using ShotIt.Core.Editing;
using ShotIt.Core.Imaging;
using ShotIt.Core.Settings;
using ShotIt.Rendering;

namespace ShotIt.Rendering.Tests;

public sealed class ProjectFileTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "shotit-project-" + Guid.NewGuid().ToString("N"));

    public ProjectFileTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, true);

    private string File(string name = "a.shotit") => Path.Combine(_folder, name);

    private static PixelBuffer Image()
    {
        var image = new PixelBuffer(64, 48);
        for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
                image.SetPixel(x, y, new ColorRgba((byte)(x * 4), (byte)(y * 5), (byte)(x + y)));
        return image;
    }

    private static DocState FullDocument()
    {
        var pasted = new PixelBuffer(4, 4);
        pasted.Fill(ColorRgba.Red);
        return DocState.FromImage(Image())
            .Add(new ArrowAnnotation { Start = new(1, 2), End = new(30, 40), StrokeWidth = 5, Color = ColorRgba.Yellow })
            .Add(new LineAnnotation { Points = [new(3, 3), new(9, 9)] })
            .Add(new ShapeAnnotation { Rect = new(2, 2, 20, 10), Kind = ShapeKind.Ellipse })
            .Add(new FreehandAnnotation { Points = [new(1, 1), new(2, 5), new(6, 7)] })
            .Add(new HighlighterAnnotation { Points = [new(5, 20), new(50, 20)] })
            .Add(new AreaHighlightAnnotation { Rect = new(4, 4, 10, 10) })
            .Add(new SpotlightAnnotation { Rect = new(10, 10, 20, 20) })
            .Add(new TextAnnotation { Position = new(5, 5), Text = "Héllo “wörld”\nline 2", Bold = true })
            .Add(new CalloutAnnotation { Box = new(20, 20, 30, 15), Tail = new(10, 40), Text = "note" })
            .Add(new StepAnnotation { Center = new(12, 12), Number = 3 })
            .Add(new MagnifierAnnotation { Source = new(30, 5, 10, 10), Dest = new(40, 20, 20, 20) })
            .Add(new BlurAnnotation { Rect = new(0, 0, 16, 16) })
            .Add(new PixelateAnnotation { Rect = new(16, 16, 16, 16) })
            .Add(new RedactAnnotation { Rect = new(40, 30, 20, 8) })
            .Add(new StickerAnnotation { Rect = new(50, 2, 10, 10) })
            .Add(new ImageAnnotation { Rect = new(1, 30, 8, 8), EncodedImage = ImageCodec.Encode(pasted, ImageFormat.Png) })
            .Add(new MeasureAnnotation { Start = new(0, 0), End = new(30, 40) })
            with
            {
                Canvas = new CanvasStyle { Padding = 24, Background = BackgroundKind.Gradient, CornerRadius = 8, Shadow = true },
                Watermark = new Watermark { Text = "© me", Opacity = 0.4 },
            };
    }

    [Fact]
    public void EveryAnnotationTypeSurvivesARoundTrip()
    {
        var document = FullDocument();
        ProjectFile.Save(File(), document);
        var loaded = ProjectFile.Load(File());

        Assert.Equal(document.Base.Size, loaded.Base.Size);
        Assert.Equal(document.Base.Data, loaded.Base.Data);
        Assert.Equal(document.Items.Count, loaded.Items.Count);
        for (int i = 0; i < document.Items.Count; i++)
        {
            Assert.Equal(document.Items[i].GetType(), loaded.Items[i].GetType());
            Assert.Equal(document.Items[i].Id, loaded.Items[i].Id);
            Assert.Equal(document.Items[i].Bounds, loaded.Items[i].Bounds);
        }

        Assert.Equal("Héllo “wörld”\nline 2", loaded.Items.OfType<TextAnnotation>().Single().Text);
        Assert.Equal(ColorRgba.Yellow, loaded.Items.OfType<ArrowAnnotation>().Single().Color);
        Assert.Equal(document.Items.OfType<ImageAnnotation>().Single().EncodedImage, loaded.Items.OfType<ImageAnnotation>().Single().EncodedImage);
        Assert.Equal(document.Canvas, loaded.Canvas);
        Assert.Equal(document.Watermark, loaded.Watermark);
    }

    [Fact]
    public void TheReloadedDocumentRendersIdentically()
    {
        Sta.Run(() =>
        {
            var document = FullDocument();
            ProjectFile.Save(File(), document);
            var loaded = ProjectFile.Load(File());
            Assert.Equal(DocumentRenderer.RenderFinal(document).Data, DocumentRenderer.RenderFinal(loaded).Data);
        });
    }

    [Fact]
    public void SavingReplacesAnExistingProjectAndLeavesNoTemporaryFile()
    {
        ProjectFile.Save(File(), FullDocument());
        ProjectFile.Save(File(), DocState.FromImage(Image()).Add(new LineAnnotation { Points = [new(0, 0), new(5, 5)] }));
        Assert.Single(ProjectFile.Load(File()).Items);
        Assert.Equal(["a.shotit"], Directory.GetFiles(_folder).Select(Path.GetFileName));
    }

    [Fact]
    public void OnlyDocumentsWithSomethingToEditNeedAProject()
    {
        var plain = DocState.FromImage(Image());
        Assert.False(ProjectFile.HasEditableContent(plain));
        Assert.True(ProjectFile.HasEditableContent(plain.Add(new RedactAnnotation { Rect = new(0, 0, 5, 5) })));
        Assert.True(ProjectFile.HasEditableContent(plain with { Canvas = new CanvasStyle { Padding = 10 } }));
        Assert.True(ProjectFile.HasEditableContent(plain with { Watermark = new Watermark { Text = "x" } }));
    }

    [Fact]
    public void DamagedFilesAreRejectedWithInvalidData()
    {
        System.IO.File.WriteAllText(File("garbage.shotit"), "this is not a zip");
        Assert.Throws<InvalidDataException>(() => ProjectFile.Load(File("garbage.shotit")));

        ProjectFile.Save(File(), FullDocument());
        byte[] bytes = System.IO.File.ReadAllBytes(File());
        System.IO.File.WriteAllBytes(File("cut.shotit"), bytes[..(bytes.Length / 2)]);
        Assert.Throws<InvalidDataException>(() => ProjectFile.Load(File("cut.shotit")));
    }

    private void WriteZip(string name, string? json, byte[]? png)
    {
        using var zip = ZipFile.Open(File(name), ZipArchiveMode.Create);
        if (json is not null)
        {
            using var stream = zip.CreateEntry("document.json").Open();
            stream.Write(Encoding.UTF8.GetBytes(json));
        }

        if (png is not null)
        {
            using var stream = zip.CreateEntry("base.png").Open();
            stream.Write(png);
        }
    }

    [Fact]
    public void IncompleteOrForeignProjectsAreRejected()
    {
        byte[] png = ImageCodec.Encode(Image(), ImageFormat.Png);
        WriteZip("no-image.shotit", "{\"version\":1,\"items\":[]}", null);
        WriteZip("no-document.shotit", null, png);
        WriteZip("bad-json.shotit", "{ nope", png);
        WriteZip("bad-image.shotit", "{\"version\":1,\"items\":[]}", [1, 2, 3, 4]);
        WriteZip("newer.shotit", "{\"version\":99,\"items\":[]}", png);
        WriteZip("unknown-type.shotit", "{\"version\":1,\"items\":[{\"$type\":\"teleporter\"}]}", png);
        foreach (string name in new[] { "no-image", "no-document", "bad-json", "bad-image", "newer", "unknown-type" })
            Assert.Throws<InvalidDataException>(() => ProjectFile.Load(File(name + ".shotit")));
    }

    [Fact]
    public void AMinimalHandWrittenProjectLoads()
    {
        WriteZip("minimal.shotit", "{\"version\":1}", ImageCodec.Encode(Image(), ImageFormat.Png));
        var loaded = ProjectFile.Load(File("minimal.shotit"));
        Assert.Empty(loaded.Items);
        Assert.True(loaded.Canvas.IsNone);
        Assert.Null(loaded.Watermark);
    }
}
