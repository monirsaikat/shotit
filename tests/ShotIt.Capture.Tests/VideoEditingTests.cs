using System.IO;
using ShotIt.Capture;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using ShotIt.Core.Video;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;

namespace ShotIt.Capture.Tests;

/// <summary>
/// Edits a real video made on the spot. On Windows editions without media features there is no video
/// encoder at all, so the tests have nothing to run against and pass without checking anything.
/// </summary>
public sealed class VideoEditingTests : IAsyncLifetime
{
    private static readonly ColorRgba Red = new(220, 30, 30), Green = new(30, 200, 30), Blue = new(30, 30, 220), Yellow = new(230, 220, 30);
    private readonly string _folder = Directory.CreateTempSubdirectory("shotit-edit").FullName;
    private string? _video;

    private string File(string name) => Path.Combine(_folder, name);

    /// <summary>Three seconds, 320 by 240, a different colour in each quarter.</summary>
    public async Task InitializeAsync()
    {
        string path = File("source.mp4");
        try
        {
            var frame = new PixelBuffer(320, 240);
            for (int y = 0; y < 240; y++)
                for (int x = 0; x < 320; x++)
                    frame.SetPixel(x, y, y < 120 ? (x < 160 ? Red : Green) : (x < 160 ? Blue : Yellow));
            using var sink = new Mp4Sink(path, new PixelSize(320, 240), framesPerSecond: 20);
            for (int i = 0; i < 60; i++) sink.Add(frame, TimeSpan.FromMilliseconds(i * 50));
            await sink.FinishAsync(TimeSpan.FromSeconds(3));
            _video = path;
        }
        catch (RecordingUnavailableException)
        {
        }
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        return Task.CompletedTask;
    }

    /// <summary>A plain tone as an uncompressed WAV file.</summary>
    private string Tone(double seconds)
    {
        const int Rate = 22050;
        int samples = (int)(Rate * seconds);
        string path = File("tone.wav");
        using var writer = new BinaryWriter(System.IO.File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + samples * 2);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(Rate);
        writer.Write(Rate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples * 2);
        for (int i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / Rate) * 12000));
        return path;
    }

    private static async Task<ColorRgba> ColourAt(string path, TimeSpan time, double fx, double fy)
    {
        var frame = await VideoEditor.FrameAsync(path, time);
        return frame.GetPixel((int)(frame.Width * fx), (int)(frame.Height * fy));
    }

    private static void AssertClose(ColorRgba expected, ColorRgba actual) =>
        Assert.True(Math.Abs(expected.R - actual.R) < 20 && Math.Abs(expected.G - actual.G) < 20 && Math.Abs(expected.B - actual.B) < 20,
            $"Expected about {expected}, found {actual}");

    [Fact]
    public async Task A_video_is_described()
    {
        if (_video is null) return;
        var info = await VideoEditor.ProbeAsync(_video);
        Assert.Equal(new PixelSize(320, 240), info.Size);
        Assert.InRange(info.Duration.TotalSeconds, 2.8, 3.2);
        Assert.False(info.HasAudio);
    }

    [Fact]
    public async Task A_still_shows_the_colours_that_were_recorded()
    {
        if (_video is null) return;
        var frame = await VideoEditor.FrameAsync(_video, TimeSpan.FromSeconds(1.5));
        Assert.Equal(new PixelSize(320, 240), frame.Size);
        AssertClose(Red, frame.GetPixel(80, 60));
        AssertClose(Green, frame.GetPixel(240, 60));
        AssertClose(Blue, frame.GetPixel(80, 180));
        AssertClose(Yellow, frame.GetPixel(240, 180));
    }

    [Fact]
    public async Task Something_that_is_not_a_video_is_refused_with_a_clear_message()
    {
        string path = File("notes.mp4");
        System.IO.File.WriteAllText(path, "not a video");
        var ex = await Assert.ThrowsAsync<VideoEditException>(() => VideoEditor.ProbeAsync(path));
        Assert.Contains("notes.mp4", ex.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<VideoEditException>(() => VideoEditor.ProbeAsync(File("missing.mp4")));
    }

    [Fact]
    public async Task Trimming_and_cropping_keep_the_chosen_part()
    {
        if (_video is null) return;
        string result = File("cut.mp4");
        var progress = new List<double>();
        var edit = new VideoEdit { Start = TimeSpan.FromSeconds(1), End = TimeSpan.FromSeconds(2), Crop = new PixelRect(160, 120, 160, 120) };
        await VideoEditor.ExportMp4Async(_video, edit, result, new Immediate(progress.Add));

        var info = await VideoEditor.ProbeAsync(result);
        Assert.Equal(new PixelSize(160, 120), info.Size);
        Assert.InRange(info.Duration.TotalSeconds, 0.85, 1.2);
        Assert.False(info.HasAudio);
        // The bottom right quarter of the original is yellow, and nothing else may show.
        AssertClose(Yellow, await ColourAt(result, TimeSpan.FromSeconds(0.5), 0.5, 0.5));
        AssertClose(Yellow, await ColourAt(result, TimeSpan.FromSeconds(0.5), 0.05, 0.05));
        AssertClose(Yellow, await ColourAt(result, TimeSpan.FromSeconds(0.5), 0.95, 0.95));
        Assert.NotEmpty(progress);
    }

    [Fact]
    public async Task A_crop_off_centre_keeps_the_right_corner()
    {
        if (_video is null) return;
        string result = File("corner.mp4");
        await VideoEditor.ExportMp4Async(_video, new VideoEdit { Crop = new PixelRect(0, 0, 240, 180) }, result);
        Assert.Equal(new PixelSize(240, 180), (await VideoEditor.ProbeAsync(result)).Size);
        AssertClose(Red, await ColourAt(result, TimeSpan.FromSeconds(1), 0.2, 0.2));
        AssertClose(Green, await ColourAt(result, TimeSpan.FromSeconds(1), 0.9, 0.2));
        AssertClose(Blue, await ColourAt(result, TimeSpan.FromSeconds(1), 0.2, 0.9));
        AssertClose(Yellow, await ColourAt(result, TimeSpan.FromSeconds(1), 0.9, 0.9));
    }

    [Fact]
    public async Task Added_audio_is_cut_to_the_length_of_the_video()
    {
        if (_video is null) return;
        string result = File("sound.mp4");
        await VideoEditor.ExportMp4Async(_video, new VideoEdit { AudioPath = Tone(6), AudioVolume = 0.5 }, result);
        var info = await VideoEditor.ProbeAsync(result);
        Assert.True(info.HasAudio, "The exported video has no sound");
        Assert.InRange(info.Duration.TotalSeconds, 2.8, 3.3);
        Assert.Equal(new PixelSize(320, 240), info.Size);
    }

    [Fact]
    public async Task Cropping_keeps_the_sound_of_the_video_unless_it_is_turned_off()
    {
        if (_video is null) return;
        string withSound = File("original-with-sound.mp4");
        await VideoEditor.ExportMp4Async(_video, new VideoEdit { AudioPath = Tone(3) }, withSound);

        string kept = File("cropped-loud.mp4");
        var edit = new VideoEdit { Start = TimeSpan.FromSeconds(1), Crop = new PixelRect(0, 0, 160, 120) };
        await VideoEditor.ExportMp4Async(withSound, edit, kept);
        var info = await VideoEditor.ProbeAsync(kept);
        Assert.True(info.HasAudio, "Cropping lost the sound");
        Assert.Equal(new PixelSize(160, 120), info.Size);
        Assert.InRange(info.Duration.TotalSeconds, 1.8, 2.3);
        AssertClose(Red, await ColourAt(kept, TimeSpan.FromSeconds(1), 0.5, 0.5));

        string muted = File("cropped-quiet.mp4");
        await VideoEditor.ExportMp4Async(withSound, edit with { Volume = 0 }, muted);
        Assert.False((await VideoEditor.ProbeAsync(muted)).HasAudio);
        Assert.Equal(["cropped-loud.mp4", "cropped-quiet.mp4"], Directory.GetFiles(_folder, "cropped*").Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task Added_audio_at_zero_volume_adds_no_sound()
    {
        if (_video is null) return;
        string result = File("silent.mp4");
        await VideoEditor.ExportMp4Async(_video, new VideoEdit { AudioPath = Tone(1), AudioVolume = 0 }, result);
        Assert.False((await VideoEditor.ProbeAsync(result)).HasAudio);
    }

    [Fact]
    public async Task A_bad_audio_file_is_refused_and_leaves_nothing_behind()
    {
        if (_video is null) return;
        string bad = File("noise.mp3");
        System.IO.File.WriteAllText(bad, "not audio");
        string result = File("never.mp4");
        await Assert.ThrowsAsync<VideoEditException>(() => VideoEditor.ExportMp4Async(_video, new VideoEdit { AudioPath = bad }, result));
        Assert.False(System.IO.File.Exists(result));
    }

    [Fact]
    public async Task A_video_cannot_be_saved_over_itself()
    {
        if (_video is null) return;
        await Assert.ThrowsAsync<VideoEditException>(() => VideoEditor.ExportMp4Async(_video, new VideoEdit(), _video));
        Assert.True(new FileInfo(_video).Length > 1000);
    }

    [Fact]
    public async Task A_cancelled_export_leaves_nothing_behind()
    {
        if (_video is null) return;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        string result = File("cancelled.gif");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => VideoEditor.ExportGifAsync(_video, new VideoEdit(), result, 10, cancel: cancel.Token));
        Assert.False(System.IO.File.Exists(result));
    }

    [Fact]
    public async Task A_gif_is_made_from_the_edited_part()
    {
        if (_video is null) return;
        string result = File("cut.gif");
        var edit = new VideoEdit { Start = TimeSpan.FromSeconds(0.5), End = TimeSpan.FromSeconds(1.5), Crop = new PixelRect(0, 120, 160, 120) };
        await VideoEditor.ExportGifAsync(_video, edit, result, framesPerSecond: 10);

        byte[] bytes = System.IO.File.ReadAllBytes(result);
        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(bytes, 0, 6));
        Assert.Equal(160, bytes[6] | bytes[7] << 8);
        Assert.Equal(120, bytes[8] | bytes[9] << 8);
        Assert.Equal(0x3B, bytes[^1]);

        // The first frame is stored whole: the bottom left quarter of the original, which is blue.
        var decoder = await BitmapDecoder.CreateAsync(await (await StorageFile.GetFileFromPathAsync(result)).OpenReadAsync());
        byte[] pixels = (await decoder.GetPixelDataAsync()).DetachPixelData();
        int i = (60 * 160 + 80) * 4;
        AssertClose(Blue, decoder.BitmapPixelFormat == BitmapPixelFormat.Rgba8 ? new ColorRgba(pixels[i], pixels[i + 1], pixels[i + 2]) : new ColorRgba(pixels[i + 2], pixels[i + 1], pixels[i]));
    }

    private sealed class Immediate(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
