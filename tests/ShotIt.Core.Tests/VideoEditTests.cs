using ShotIt.Core.Geometry;
using ShotIt.Core.Video;

namespace ShotIt.Core.Tests;

public class VideoEditTests
{
    private static readonly VideoInfo Video = new(TimeSpan.FromSeconds(10), new PixelSize(1281, 721), HasAudio: false);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Fact]
    public void An_untouched_edit_changes_nothing()
    {
        var edit = new VideoEdit().Normalize(Video);
        Assert.True(edit.IsUnchanged);
        Assert.Equal(S(10), edit.OutputDuration(Video));
        Assert.Equal(new PixelSize(1280, 720), edit.OutputSize(Video));
        Assert.False(edit.HasSound(Video));
    }

    [Fact]
    public void The_kept_part_stays_inside_the_video()
    {
        var edit = new VideoEdit { Start = S(-3), End = S(25) }.Normalize(Video);
        Assert.Equal(TimeSpan.Zero, edit.Start);
        Assert.Null(edit.End);

        edit = new VideoEdit { Start = S(4), End = S(6.5) }.Normalize(Video);
        Assert.Equal(S(2.5), edit.OutputDuration(Video));
    }

    [Fact]
    public void An_end_before_the_start_still_leaves_something_to_watch()
    {
        var edit = new VideoEdit { Start = S(8), End = S(2) }.Normalize(Video);
        Assert.Equal(S(8), edit.Start);
        Assert.Equal(VideoEdit.MinDuration, edit.OutputDuration(Video));

        edit = new VideoEdit { Start = S(30) }.Normalize(Video);
        Assert.Equal(VideoEdit.MinDuration, edit.OutputDuration(Video));
    }

    [Fact]
    public void A_crop_is_kept_inside_the_picture_with_even_sides()
    {
        var edit = new VideoEdit { Crop = new PixelRect(100, 50, 301, 201) }.Normalize(Video);
        Assert.Equal(new PixelRect(100, 50, 300, 200), edit.Crop);
        Assert.Equal(new PixelSize(300, 200), edit.OutputSize(Video));

        edit = new VideoEdit { Crop = new PixelRect(1200, 700, 400, 400) }.Normalize(Video);
        Assert.Equal(new PixelRect(1200, 700, 80, 20), edit.Crop);
    }

    [Fact]
    public void A_crop_too_small_or_outside_the_picture_grows_to_the_smallest_allowed()
    {
        var edit = new VideoEdit { Crop = new PixelRect(1279, 719, 2, 2) }.Normalize(Video);
        Assert.Equal(new PixelRect(1265, 705, 16, 16), edit.Crop);

        edit = new VideoEdit { Crop = new PixelRect(5000, 5000, 100, 100) }.Normalize(Video);
        Assert.Equal(new PixelSize(16, 16), edit.Crop!.Value.Size);
        Assert.True(new PixelRect(0, 0, 1281, 721).Intersect(edit.Crop.Value) == edit.Crop.Value);
    }

    [Fact]
    public void A_crop_of_the_whole_picture_is_no_crop()
    {
        var even = new VideoInfo(S(5), new PixelSize(640, 480), false);
        Assert.Null(new VideoEdit { Crop = new PixelRect(-10, -10, 2000, 2000) }.Normalize(even).Crop);
    }

    [Fact]
    public void Sound_comes_from_the_video_or_from_added_audio()
    {
        var withSound = Video with { HasAudio = true };
        Assert.True(new VideoEdit().HasSound(withSound));
        Assert.False(new VideoEdit { Volume = 0 }.HasSound(withSound));
        Assert.True(new VideoEdit { Volume = 0, AudioPath = "music.mp3" }.HasSound(withSound));
        Assert.False(new VideoEdit { AudioPath = "music.mp3", AudioVolume = 0 }.HasSound(Video));
        Assert.Null(new VideoEdit { AudioPath = "  " }.Normalize(Video).AudioPath);
        Assert.Equal(1, new VideoEdit { Volume = 7, AudioVolume = -2 }.Normalize(Video).Volume);
    }
}
