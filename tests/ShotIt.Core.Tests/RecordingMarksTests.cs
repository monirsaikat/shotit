using ShotIt.Core.Imaging;

namespace ShotIt.Core.Tests;

public class RecordingMarksTests
{
    private static Func<int, bool> Down(params int[] keys) => keys.Contains;

    [Theory]
    [InlineData("Ctrl + S", 0x11, 0x53)]
    [InlineData("Ctrl + Shift + S", 0x11, 0x10, 0x53)]
    [InlineData("Alt + F4", 0x12, 0x73)]
    [InlineData("Win + D", 0x5B, 0x44)]
    [InlineData("Enter", 0x0D)]
    [InlineData("Shift + Tab", 0x10, 0x09)]
    [InlineData("F5", 0x74)]
    [InlineData("Ctrl + Alt + Delete", 0x11, 0x12, 0x2E)]
    public void Shortcuts_are_named(string expected, params int[] keys) =>
        Assert.Equal(expected, KeyCaption.Describe(Down(keys)));

    [Theory]
    [InlineData(0x41)]
    [InlineData(0x10, 0x41)]
    [InlineData(0x37)]
    [InlineData(0x20)]
    [InlineData(0x11)]
    [InlineData(0x11, 0x12, 0xA5, 0x51)] // AltGr + Q types a character on many keyboards
    public void Typing_is_never_shown(params int[] keys) =>
        Assert.Null(KeyCaption.Describe(Down(keys)));

    [Fact]
    public void A_ring_colours_its_edge_and_leaves_its_centre_alone()
    {
        var image = new PixelBuffer(100, 100);
        image.Fill(ColorRgba.White);
        RecordingMarks.DrawRing(image, 50, 50, radius: 20, thickness: 4, ColorRgba.Red, opacity: 1);
        Assert.Equal(ColorRgba.Red, image.GetPixel(70, 50));
        Assert.Equal(ColorRgba.Red, image.GetPixel(50, 30));
        Assert.Equal(ColorRgba.White, image.GetPixel(50, 50));
        Assert.Equal(ColorRgba.White, image.GetPixel(80, 50));
    }

    [Fact]
    public void A_ring_at_the_edge_of_the_picture_is_clipped()
    {
        var image = new PixelBuffer(40, 40);
        image.Fill(ColorRgba.White);
        RecordingMarks.DrawRing(image, 0, 0, radius: 30, thickness: 6, ColorRgba.Red, opacity: 0.5);
        RecordingMarks.DrawRing(image, 500, 500, radius: 30, thickness: 6, ColorRgba.Red, opacity: 1);
        var half = image.GetPixel(30, 0);
        Assert.InRange(half.G, 136, 146);
    }

    [Fact]
    public void An_overlay_is_blended_by_its_alpha_and_clipped()
    {
        var image = new PixelBuffer(10, 10);
        image.Fill(ColorRgba.White);
        var label = new PixelBuffer(4, 4);
        label.Fill(new ColorRgba(0, 0, 0, 255));
        label.SetPixel(0, 0, new ColorRgba(0, 0, 0, 0));
        RecordingMarks.Overlay(image, label, 8, 8);
        RecordingMarks.Overlay(image, label, -2, -2);
        Assert.Equal(ColorRgba.White, image.GetPixel(8, 8));
        Assert.Equal(ColorRgba.Black, image.GetPixel(9, 9));
        Assert.Equal(ColorRgba.Black, image.GetPixel(1, 1));
        Assert.Equal(ColorRgba.White, image.GetPixel(2, 2));
    }
}
