using ShotIt.Capture;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;

namespace ShotIt.Capture.Tests;

public class ScrollingCaptureTests
{
    private static readonly ScrollCaptureOptions Fast = new()
    {
        PollInterval = TimeSpan.FromMilliseconds(1),
        ReactionTimeout = TimeSpan.FromMilliseconds(30),
        SettleTimeout = TimeSpan.FromMilliseconds(500),
        EndOfPageWait = TimeSpan.FromMilliseconds(1),
    };

    private static PixelBuffer Page(int width, int height, int seed = 1)
    {
        var random = new Random(seed);
        var page = new PixelBuffer(width, height);
        page.Fill(ColorRgba.White);
        for (int y = 6; y + 12 < height; y += 20)
        {
            int indent = random.Next(0, width / 4), length = random.Next(width / 3, width - indent);
            for (int row = y; row < y + 12; row++)
                for (int x = indent; x < indent + length; x++)
                    if (random.Next(3) == 0) page.SetPixel(x, row, new ColorRgba((byte)random.Next(90), (byte)random.Next(90), (byte)random.Next(90)));
        }

        return page;
    }

    /// <summary>A window onto a tall page that scrolls smoothly: each photograph finds it a little closer to where it is going.</summary>
    private sealed class FakeWindow(PixelBuffer page, int viewHeight, int pixelsPerNotch) : IScrollSource
    {
        private int _position;
        private int _target;

        public int ScrollCalls { get; private set; }
        public int StopAfterCalls { get; init; } = int.MaxValue;
        public bool Frozen { get; init; }

        /// <summary>The window stops moving for this many photographs in the middle of every scroll.</summary>
        public int Stutter { get; init; }

        private int _stalled;

        public PixelBuffer Grab()
        {
            if (_position < _target && _position % 90 == 45 && _stalled < Stutter) _stalled++;
            else
            {
                _stalled = 0;
                _position = Math.Min(_target, _position + 45);
            }

            return page.Crop(new PixelRect(0, _position, page.Width, viewHeight));
        }

        public bool Scroll(int notches)
        {
            if (ScrollCalls >= StopAfterCalls) return false;
            ScrollCalls++;
            if (!Frozen) _target = Math.Min(page.Height - viewHeight, _target + notches * pixelsPerNotch);
            return true;
        }
    }

    private static void AssertSameRows(PixelBuffer expected, PixelBuffer actual, int count)
    {
        for (int y = 0; y < count; y++)
            Assert.True(expected.Row(y).SequenceEqual(actual.Row(y)), $"Row {y} differs");
    }

    [Fact]
    public async Task A_page_is_captured_from_top_to_bottom()
    {
        var page = Page(640, 4000);
        var window = new FakeWindow(page, 480, pixelsPerNotch: 40);
        var heights = new List<int>();
        var result = await ScrollingCapture.RunAsync(window, Fast, new ImmediateProgress(heights.Add));

        Assert.Equal(ScrollStopReason.ReachedEnd, result.Reason);
        Assert.Equal(page.Size, result.Image.Size);
        AssertSameRows(page, result.Image, 4000);
        Assert.Equal(4000, heights[^1]);
        // Bigger steps are taken once one notch has been measured.
        Assert.True(window.ScrollCalls < 30, $"{window.ScrollCalls} scroll steps");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public async Task A_window_that_stutters_while_scrolling_is_still_captured_whole(int stutter)
    {
        var page = Page(640, 4000);
        var result = await ScrollingCapture.RunAsync(new FakeWindow(page, 480, pixelsPerNotch: 40) { Stutter = stutter }, Fast);
        Assert.Equal(ScrollStopReason.ReachedEnd, result.Reason);
        Assert.Equal(page.Size, result.Image.Size);
        AssertSameRows(page, result.Image, 4000);
    }

    [Fact]
    public async Task An_area_that_does_not_scroll_gives_just_what_is_visible()
    {
        var page = Page(640, 4000);
        var result = await ScrollingCapture.RunAsync(new FakeWindow(page, 480, 100) { Frozen = true }, Fast);
        Assert.Equal(ScrollStopReason.NothingScrolled, result.Reason);
        Assert.Equal(480, result.Image.Height);
    }

    [Fact]
    public async Task Stopping_early_keeps_what_was_captured()
    {
        var page = Page(640, 4000);
        var result = await ScrollingCapture.RunAsync(new FakeWindow(page, 480, 100) { StopAfterCalls = 3 }, Fast);
        Assert.Equal(ScrollStopReason.Stopped, result.Reason);
        Assert.InRange(result.Image.Height, 580, 3999);
        AssertSameRows(page, result.Image, result.Image.Height);
    }

    [Fact]
    public async Task A_stop_request_ends_the_capture()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var result = await ScrollingCapture.RunAsync(new FakeWindow(Page(640, 4000), 480, 100), Fast, stop: stop.Token);
        Assert.Equal(ScrollStopReason.Stopped, result.Reason);
        Assert.Equal(480, result.Image.Height);
    }

    [Fact]
    public async Task The_capture_ends_at_the_height_limit()
    {
        var page = Page(640, 4000);
        var result = await ScrollingCapture.RunAsync(new FakeWindow(page, 480, 100), Fast with { MaxHeight = 1500 });
        Assert.Equal(ScrollStopReason.HeightLimit, result.Reason);
        Assert.Equal(1500, result.Image.Height);
        AssertSameRows(page, result.Image, 1500);
    }

    private sealed class ImmediateProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
