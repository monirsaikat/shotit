using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;

namespace ShotIt.Core.Tests;

public class ScrollStitcherTests
{
    /// <summary>A white page of "text lines": bands of random dark marks with blank rows between them.</summary>
    internal static PixelBuffer Page(int width, int height, int seed = 1)
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

    private static PixelBuffer View(PixelBuffer page, int position, int height) =>
        page.Crop(new PixelRect(0, position, page.Width, height));

    private static ScrollFrame Frame(PixelBuffer page, int position, int height) => new(View(page, position, height));

    private static void AssertSameRows(PixelBuffer expected, int expectedRow, PixelBuffer actual, int actualRow, int count)
    {
        for (int y = 0; y < count; y++)
            Assert.True(expected.Row(expectedRow + y).SequenceEqual(actual.Row(actualRow + y)), $"Row {actualRow + y} differs");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(120)]
    [InlineData(300)]
    public void The_offset_between_two_views_is_found(int offset)
    {
        var page = Page(640, 2000);
        var match = ScrollMatcher.Match(Frame(page, 400, 480), Frame(page, 400 + offset, 480));
        Assert.Equal(offset, match!.Value.Offset);
    }

    [Fact]
    public void Identical_views_have_not_moved()
    {
        var page = Page(640, 1000);
        Assert.Equal(0, ScrollMatcher.Match(Frame(page, 100, 480), Frame(page, 100, 480))!.Value.Offset);
    }

    [Fact]
    public void Unrelated_views_cannot_be_matched()
    {
        Assert.Null(ScrollMatcher.Match(new ScrollFrame(Page(640, 480, seed: 1)), new ScrollFrame(Page(640, 480, seed: 2))));
    }

    [Fact]
    public void A_moving_scroll_bar_does_not_spoil_the_match()
    {
        var page = Page(640, 2000);
        PixelBuffer WithScrollBar(int position)
        {
            var view = View(page, position, 480);
            int thumb = position * 480 / 2000;
            for (int y = 0; y < 480; y++)
                for (int x = 624; x < 640; x++)
                    view.SetPixel(x, y, y >= thumb && y < thumb + 115 ? new ColorRgba(120, 120, 120) : new ColorRgba(235, 235, 235));
            return view;
        }

        var match = ScrollMatcher.Match(new ScrollFrame(WithScrollBar(300)), new ScrollFrame(WithScrollBar(460)));
        Assert.Equal(160, match!.Value.Offset);
    }

    [Fact]
    public void A_side_bar_that_stays_put_does_not_spoil_the_match()
    {
        var page = Page(400, 2000);
        var sideBar = Page(240, 480, seed: 9);
        PixelBuffer WithSideBar(int position)
        {
            var view = new PixelBuffer(640, 480);
            view.Blit(sideBar, 0, 0);
            view.Blit(View(page, position, 480), 240, 0);
            return view;
        }

        var match = ScrollMatcher.Match(new ScrollFrame(WithSideBar(100)), new ScrollFrame(WithSideBar(230)));
        Assert.Equal(130, match!.Value.Offset);
    }

    [Fact]
    public void Views_are_joined_back_into_the_page()
    {
        var page = Page(640, 3000);
        var stitcher = new ScrollStitcher(100_000);
        int position = 0;
        foreach (int step in new[] { 0, 150, 150, 97, 200, 200, 200, 200, 200, 200, 200, 200, 200, 200, 123 })
        {
            position = Math.Min(position + step, 3000 - 480);
            Assert.Equal(ScrollAddResult.Added, stitcher.Add(Frame(page, position, 480)));
        }

        Assert.Equal(ScrollAddResult.NoMovement, stitcher.Add(Frame(page, position, 480)));
        var joined = stitcher.Build();
        Assert.Equal(page.Size, joined.Size);
        AssertSameRows(page, 0, joined, 0, 3000);
    }

    [Fact]
    public void A_sticky_header_and_footer_appear_once()
    {
        var page = Page(640, 2000);
        var header = Page(640, 60, seed: 5);
        var footer = Page(640, 40, seed: 6);
        PixelBuffer Window(int position)
        {
            var view = View(page, position, 480);
            view.Blit(header, 0, 0);
            view.Blit(footer, 0, 440);
            return view;
        }

        var stitcher = new ScrollStitcher(100_000);
        for (int position = 0; position <= 2000 - 480; position += 152)
            Assert.Equal(ScrollAddResult.Added, stitcher.Add(new ScrollFrame(Window(position))));
        stitcher.Add(new ScrollFrame(Window(2000 - 480)));

        var joined = stitcher.Build();
        Assert.Equal(2000, joined.Height);
        AssertSameRows(header, 0, joined, 0, 60);
        AssertSameRows(page, 60, joined, 60, 2000 - 60 - 40);
        AssertSameRows(footer, 0, joined, 1960, 40);
    }

    [Fact]
    public void The_picture_stops_at_the_height_limit()
    {
        var page = Page(640, 3000);
        var stitcher = new ScrollStitcher(1000);
        Assert.Equal(ScrollAddResult.Added, stitcher.Add(Frame(page, 0, 480)));
        Assert.Equal(ScrollAddResult.Added, stitcher.Add(Frame(page, 200, 480)));
        Assert.Equal(ScrollAddResult.Added, stitcher.Add(Frame(page, 400, 480)));
        Assert.Equal(ScrollAddResult.Full, stitcher.Add(Frame(page, 600, 480)));
        var joined = stitcher.Build();
        Assert.Equal(1000, joined.Height);
        AssertSameRows(page, 0, joined, 0, 1000);
    }

    [Fact]
    public void A_view_that_cannot_be_matched_is_left_out()
    {
        var page = Page(640, 2000);
        var stitcher = new ScrollStitcher(100_000);
        stitcher.Add(Frame(page, 0, 480));
        Assert.Equal(ScrollAddResult.NoMatch, stitcher.Add(new ScrollFrame(Page(640, 480, seed: 3))));
        Assert.Equal(ScrollAddResult.Added, stitcher.Add(Frame(page, 100, 480)));
        Assert.Equal(580, stitcher.Height);
    }
}
