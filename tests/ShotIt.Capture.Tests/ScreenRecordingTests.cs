using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using ShotIt.Capture;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;

namespace ShotIt.Capture.Tests;

public sealed class ScreenRecordingTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("shotit-rec").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Red at the top, blue at the bottom, with a white bar that moves right as time passes.</summary>
    private static PixelBuffer Frame(int width, int height, int step)
    {
        var image = new PixelBuffer(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                image.SetPixel(x, y, x >= step * 4 && x < step * 4 + 20 ? ColorRgba.White : y < height / 2 ? new ColorRgba(220, 20, 20) : new ColorRgba(20, 20, 220));
        return image;
    }

    private sealed class ListSink : IVideoSink
    {
        public List<TimeSpan> Times { get; } = [];
        public TimeSpan? End { get; private set; }

        public void Add(PixelBuffer frame, TimeSpan time) => Times.Add(time);

        public Task FinishAsync(TimeSpan end)
        {
            End = end;
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public async Task Frames_are_taken_at_a_steady_rate_until_stopped()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(600));
        var sink = new ListSink();
        int decorated = 0;
        var result = await ScreenRecorder.RunAsync(() => Frame(64, 64, 0), sink, new RecordingOptions { FramesPerSecond = 20 },
            decorate: (_, _) => decorated++, stop: stop.Token);

        Assert.False(result.ReachedLimit);
        Assert.Equal(sink.Times.Count, result.Frames);
        Assert.Equal(result.Frames, decorated);
        Assert.InRange(result.Frames, 6, 14);
        Assert.Equal(result.Duration, sink.End);
        Assert.True(sink.Times.SequenceEqual(sink.Times.Order()));
    }

    [Fact]
    public async Task A_recording_ends_by_itself_at_the_time_limit()
    {
        var sink = new ListSink();
        var result = await ScreenRecorder.RunAsync(() => Frame(64, 64, 0), sink,
            new RecordingOptions { FramesPerSecond = 30, MaxDuration = TimeSpan.FromMilliseconds(300) });
        Assert.True(result.ReachedLimit);
        Assert.InRange(result.Duration.TotalMilliseconds, 300, 900);
    }

    [Fact]
    public async Task A_gif_recording_is_a_complete_file()
    {
        string path = Path.Combine(_folder, "test.gif");
        using (var sink = new GifSink(path, new PixelSize(160, 120)))
        {
            for (int i = 0; i < 10; i++) sink.Add(Frame(160, 120, i), TimeSpan.FromMilliseconds(i * 100));
            await sink.FinishAsync(TimeSpan.FromSeconds(1));
        }

        byte[] bytes = File.ReadAllBytes(path);
        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(bytes, 0, 6));
        Assert.Equal(0x3B, bytes[^1]);
    }

    [Fact]
    public async Task A_video_recording_plays_back_the_right_way_up_at_the_right_size_and_length()
    {
        string path = Path.Combine(_folder, "test.mp4");
        // An odd size on purpose: the video is one pixel smaller each way.
        try
        {
            using var sink = new Mp4Sink(path, new PixelSize(321, 241), framesPerSecond: 20);
            Assert.Equal(new PixelSize(320, 240), sink.Size);
            for (int i = 0; i < 40; i++) sink.Add(Frame(321, 241, i), TimeSpan.FromMilliseconds(i * 50));
            await sink.FinishAsync(TimeSpan.FromSeconds(2));
        }
        catch (RecordingUnavailableException)
        {
            return; // Windows editions without media features cannot encode video at all
        }

        var file = await StorageFile.GetFileFromPathAsync(path);
        var video = await file.Properties.GetVideoPropertiesAsync();
        Assert.Equal(320u, video.Width);
        Assert.Equal(240u, video.Height);
        Assert.InRange(video.Duration.TotalSeconds, 1.7, 2.3);

        var composition = new MediaComposition();
        composition.Clips.Add(await MediaClip.CreateFromFileAsync(file));
        using var thumbnail = await composition.GetThumbnailAsync(TimeSpan.FromSeconds(1), 320, 240, VideoFramePrecision.NearestFrame);
        var decoder = await BitmapDecoder.CreateAsync(thumbnail);
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] pixels = data.DetachPixelData();
        ColorRgba At(int x, int y)
        {
            int i = (y * (int)decoder.PixelWidth + x) * 4;
            return new ColorRgba(pixels[i + 2], pixels[i + 1], pixels[i]);
        }

        var top = At(300, 30);
        var bottom = At(300, 210);
        Assert.True(top.R > 150 && top.B < 90, $"The top of the video is {top}, expected red");
        Assert.True(bottom.B > 150 && bottom.R < 90, $"The bottom of the video is {bottom}, expected blue");

        // Colours must come back as they went in. Before the colour range was declared to the encoder,
        // every player stretched them: 220 became 255 and 20 became 0.
        var frame = await VideoEditor.FrameAsync(path, TimeSpan.FromSeconds(1));
        var red = frame.GetPixel(300, 30);
        var blue = frame.GetPixel(300, 210);
        Assert.True(Math.Abs(red.R - 220) < 12 && Math.Abs(red.G - 20) < 12 && Math.Abs(red.B - 20) < 12, $"Red came back as {red}");
        Assert.True(Math.Abs(blue.R - 20) < 12 && Math.Abs(blue.G - 20) < 12 && Math.Abs(blue.B - 220) < 12, $"Blue came back as {blue}");
    }
}
