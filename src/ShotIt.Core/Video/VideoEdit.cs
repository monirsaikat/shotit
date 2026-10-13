using ShotIt.Core.Geometry;

namespace ShotIt.Core.Video;

/// <summary>What is known about a video file before editing it.</summary>
public sealed record VideoInfo(TimeSpan Duration, PixelSize Size, bool HasAudio);

/// <summary>
/// The changes to make to a video: which part of its length and of its picture to keep, and what it
/// should sound like. The original file is never altered; an edit is applied when exporting a copy.
/// </summary>
public sealed record VideoEdit
{
    /// <summary>The shortest piece that can be kept.</summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromMilliseconds(100);

    /// <summary>The smallest picture that can be kept, in pixels each way.</summary>
    public const int MinSide = 16;

    /// <summary>Where the kept part begins.</summary>
    public TimeSpan Start { get; init; }

    /// <summary>Where the kept part ends; null keeps everything to the end.</summary>
    public TimeSpan? End { get; init; }

    /// <summary>The part of the picture to keep, in pixels of the original; null keeps all of it.</summary>
    public PixelRect? Crop { get; init; }

    /// <summary>Loudness of the video's own sound, from 0 (silent) to 1 (unchanged).</summary>
    public double Volume { get; init; } = 1;

    /// <summary>An audio file to play along with the video, or null.</summary>
    public string? AudioPath { get; init; }

    /// <summary>Loudness of the added audio, from 0 to 1.</summary>
    public double AudioVolume { get; init; } = 1;

    public bool IsUnchanged => Start == TimeSpan.Zero && End is null && Crop is null && Volume == 1 && AudioPath is null;

    /// <summary>Repairs anything out of range for the given video, so the exporter never has to check.</summary>
    public VideoEdit Normalize(VideoInfo video)
    {
        ArgumentNullException.ThrowIfNull(video);
        var length = video.Duration;
        var shortest = length < MinDuration ? length : MinDuration;
        var start = Clamp(Start, TimeSpan.Zero, length - shortest);
        TimeSpan? end = End is { } e && e < length ? Clamp(e, start + shortest, length) : null;

        PixelRect? crop = null;
        if (Crop is { } wanted)
        {
            var frame = new PixelRect(0, 0, video.Size.Width, video.Size.Height);
            var kept = wanted.Intersect(frame);
            // Video encoders work on blocks of two by two pixels.
            int width = Math.Max(MinSide, kept.Width) & ~1, height = Math.Max(MinSide, kept.Height) & ~1;
            width = Math.Min(width, video.Size.Width & ~1);
            height = Math.Min(height, video.Size.Height & ~1);
            int x = Math.Clamp(kept.IsEmpty ? wanted.X : kept.X, 0, video.Size.Width - width);
            int y = Math.Clamp(kept.IsEmpty ? wanted.Y : kept.Y, 0, video.Size.Height - height);
            var result = new PixelRect(x, y, width, height);
            if (result != frame) crop = result;
        }

        return this with
        {
            Start = start,
            End = end,
            Crop = crop,
            Volume = Math.Clamp(Volume, 0, 1),
            AudioVolume = Math.Clamp(AudioVolume, 0, 1),
            AudioPath = string.IsNullOrWhiteSpace(AudioPath) ? null : AudioPath,
        };
    }

    /// <summary>How long the exported video will be.</summary>
    public TimeSpan OutputDuration(VideoInfo video)
    {
        ArgumentNullException.ThrowIfNull(video);
        return (End ?? video.Duration) - Start;
    }

    /// <summary>The picture size of the exported video, always even each way.</summary>
    public PixelSize OutputSize(VideoInfo video)
    {
        ArgumentNullException.ThrowIfNull(video);
        var size = Crop?.Size ?? video.Size;
        return new PixelSize(size.Width & ~1, size.Height & ~1);
    }

    /// <summary>True when the exported video will have any sound.</summary>
    public bool HasSound(VideoInfo video)
    {
        ArgumentNullException.ThrowIfNull(video);
        return (video.HasAudio && Volume > 0) || (AudioPath is not null && AudioVolume > 0);
    }

    private static TimeSpan Clamp(TimeSpan value, TimeSpan min, TimeSpan max) => value < min ? min : value > max ? max : value;
}
