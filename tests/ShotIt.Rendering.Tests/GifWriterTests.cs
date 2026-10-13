using System.IO;
using System.Windows.Media.Imaging;
using ShotIt.Core.Export;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using ShotIt.Rendering;

namespace ShotIt.Rendering.Tests;

/// <summary>The animation is written by ShotIt's own encoder and read back here by the decoder built into Windows.</summary>
public class GifWriterTests
{
    private static readonly ColorRgba[] Colors = Enumerable.Range(0, 200)
        .Select(i => new ColorRgba((byte)(i % 8 * 32 + 4), (byte)(i / 8 % 8 * 32 + 4), (byte)(i / 64 * 64 + 4)))
        .ToArray();

    private static PixelBuffer Noise(int width, int height, int seed)
    {
        var random = new Random(seed);
        var image = new PixelBuffer(width, height);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                image.SetPixel(x, y, Colors[random.Next(Colors.Length)]);
        return image;
    }

    private static GifBitmapDecoder Decode(MemoryStream stream)
    {
        stream.Position = 0;
        return new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
    }

    private static int Query(BitmapFrame frame, string path) => Convert.ToInt32(((BitmapMetadata)frame.Metadata).GetQuery(path), System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(64, 48)]
    [InlineData(640, 480)] // enough pixels to fill and reset the compression dictionary many times
    [InlineData(1, 1)]
    public void A_single_frame_of_few_colours_reads_back_exactly(int width, int height) => Sta.Run(() =>
    {
        var image = Noise(width, height, seed: 3);
        using var stream = new MemoryStream();
        var writer = new GifWriter(stream, width, height);
        writer.AddFrame(image, TimeSpan.Zero);
        writer.Finish(TimeSpan.FromSeconds(1));

        var decoder = Decode(stream);
        Assert.Single(decoder.Frames);
        var decoded = new FormatConvertedBitmap(decoder.Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0).ToPixelBuffer();
        Assert.Equal(image.Size, decoded.Size);
        Assert.True(image.Data.AsSpan(0, width * height * 4).SequenceEqual(decoded.Data.AsSpan(0, width * height * 4)));
    });

    [Fact]
    public void Many_colours_are_reduced_to_close_ones() => Sta.Run(() =>
    {
        var image = new PixelBuffer(256, 256);
        for (int y = 0; y < 256; y++)
            for (int x = 0; x < 256; x++)
                image.SetPixel(x, y, new ColorRgba((byte)x, (byte)y, (byte)((x + y) / 2)));
        using var stream = new MemoryStream();
        var writer = new GifWriter(stream, 256, 256);
        writer.AddFrame(image, TimeSpan.Zero);
        writer.Finish(TimeSpan.FromSeconds(1));

        var decoded = new FormatConvertedBitmap(Decode(stream).Frames[0], System.Windows.Media.PixelFormats.Bgra32, null, 0).ToPixelBuffer();
        long error = 0;
        for (int i = 0; i < 256 * 256 * 4; i++) error += Math.Abs(image.Data[i] - decoded.Data[i]);
        Assert.True(error / (256.0 * 256 * 3) < 8, $"Average error per channel is {error / (256.0 * 256 * 3):0.0}");
    });

    [Fact]
    public void Later_frames_hold_only_what_changed() => Sta.Run(() =>
    {
        var first = Noise(200, 120, seed: 5);
        var second = first.Clone();
        var square = new PixelRect(40, 30, 25, 20);
        for (int y = square.Top; y < square.Bottom; y++)
            for (int x = square.Left; x < square.Right; x++)
                second.SetPixel(x, y, new ColorRgba(252, 0, 252));
        // One pixel inside the square keeps its old colour, so it must be left transparent.
        second.SetPixel(50, 40, first.GetPixel(50, 40));

        using var stream = new MemoryStream();
        var writer = new GifWriter(stream, 200, 120);
        writer.AddFrame(first, TimeSpan.Zero);
        writer.AddFrame(first.Clone(), TimeSpan.FromMilliseconds(100)); // nothing new: not stored
        writer.AddFrame(second, TimeSpan.FromMilliseconds(250));
        writer.Finish(TimeSpan.FromMilliseconds(1000));
        Assert.Equal(2, writer.FrameCount);

        var decoder = Decode(stream);
        Assert.Equal(2, decoder.Frames.Count);
        Assert.Equal(25, Query(decoder.Frames[0], "/grctlext/Delay"));
        Assert.Equal(75, Query(decoder.Frames[1], "/grctlext/Delay"));
        Assert.Equal(square.X, Query(decoder.Frames[1], "/imgdesc/Left"));
        Assert.Equal(square.Y, Query(decoder.Frames[1], "/imgdesc/Top"));
        Assert.Equal(square.Width, Query(decoder.Frames[1], "/imgdesc/Width"));
        Assert.Equal(square.Height, Query(decoder.Frames[1], "/imgdesc/Height"));

        var patch = new FormatConvertedBitmap(decoder.Frames[1], System.Windows.Media.PixelFormats.Bgra32, null, 0).ToPixelBuffer();
        // Windows hands back either the bare patch or the patch placed on a full-size canvas.
        int dx = patch.Width == square.Width ? 0 : square.X, dy = patch.Height == square.Height ? 0 : square.Y;
        Assert.Equal(new ColorRgba(252, 0, 252), patch.GetPixel(dx, dy));
        Assert.Equal(0, patch.GetPixel(dx + 10, dy + 10).A);
    });

    [Fact]
    public void Frames_of_the_wrong_size_are_refused()
    {
        var writer = new GifWriter(new MemoryStream(), 10, 10);
        Assert.Throws<ArgumentException>(() => writer.AddFrame(new PixelBuffer(11, 10), TimeSpan.Zero));
    }
}
