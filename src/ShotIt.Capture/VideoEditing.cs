using System.IO;
using ShotIt.Core.Export;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using ShotIt.Core.Video;
using ShotIt.Native;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace ShotIt.Capture;

/// <summary>Thrown when a video cannot be read or an edit cannot be exported; the message is fit to show.</summary>
public sealed class VideoEditException : Exception
{
    public VideoEditException()
    {
    }

    public VideoEditException(string message)
        : base(message)
    {
    }

    public VideoEditException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Trims, crops and adds sound to a video using the media components built into Windows, and writes
/// the result as a new MP4 or GIF. The file being edited is only ever read.
/// </summary>
public static class VideoEditor
{
    /// <summary>GIFs wider than this are scaled down; anything larger is impractically big as a GIF.</summary>
    public const int MaxGifWidth = 1000;

    private static async Task<StorageFile> OpenAsync(string path, string what)
    {
        try
        {
            return await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is FileNotFoundException or UnauthorizedAccessException or ArgumentException or IOException)
        {
            throw new VideoEditException($"The {what} “{Path.GetFileName(path)}” could not be opened.", ex);
        }
    }

    private static async Task<MediaClip> LoadAsync(string path)
    {
        var file = await OpenAsync(path, "video");
        try
        {
            return await MediaClip.CreateFromFileAsync(file);
        }
        catch (Exception ex)
        {
            // Windows reports every unreadable or unsupported file as a bare COM error.
            throw new VideoEditException($"“{Path.GetFileName(path)}” is not a video Windows can read.", ex);
        }
    }

    private static async Task<BackgroundAudioTrack> LoadAudioAsync(string path)
    {
        var file = await OpenAsync(path, "audio file");
        try
        {
            return await BackgroundAudioTrack.CreateFromFileAsync(file);
        }
        catch (Exception ex)
        {
            throw new VideoEditException($"“{Path.GetFileName(path)}” is not an audio file Windows can read.", ex);
        }
    }

    private static VideoFrameReader OpenReader(string path)
    {
        try
        {
            return new VideoFrameReader(Path.GetFullPath(path));
        }
        catch (InvalidOperationException ex)
        {
            throw new VideoEditException($"“{Path.GetFileName(path)}” is not a video Windows can read.", ex);
        }
    }

    private static VideoInfo Describe(MediaClip clip)
    {
        var video = clip.GetVideoEncodingProperties();
        return new VideoInfo(clip.OriginalDuration, new PixelSize((int)video.Width, (int)video.Height), clip.EmbeddedAudioTracks.Count > 0);
    }

    private static uint FrameRate(MediaClip clip)
    {
        var rate = clip.GetVideoEncodingProperties().FrameRate;
        return rate.Denominator == 0 ? 30 : Math.Clamp(rate.Numerator / rate.Denominator, 1, 60);
    }

    /// <summary>Reads the length, picture size and whether there is sound.</summary>
    public static async Task<VideoInfo> ProbeAsync(string path) => Describe(await LoadAsync(path));

    /// <summary>Checks that a file is audio Windows can read, and says how long it is.</summary>
    public static async Task<TimeSpan> AudioLengthAsync(string path) => (await LoadAudioAsync(path)).OriginalDuration;

    /// <summary>Reads the picture shown at a given time, for showing a still.</summary>
    public static Task<PixelBuffer> FrameAsync(string path, TimeSpan time) => Task.Run(() =>
    {
        using var reader = OpenReader(path);
        reader.Seek(time);
        PixelBuffer? last = null;
        // Decoding starts at an earlier key picture; read forward to the one that is wanted.
        while (reader.Read(out var frame, out var at))
        {
            last = frame;
            if (at >= time - TimeSpan.FromMilliseconds(1)) break;
        }

        return last ?? throw new VideoEditException($"“{Path.GetFileName(path)}” has no picture at that time.");
    });

    /// <summary>
    /// Reads the pictures between two times in order, handing each to <paramref name="use"/> with its time
    /// counted from <paramref name="start"/>.
    /// </summary>
    /// <param name="spacing">The least time between two pictures handed over; zero hands over every one.</param>
    private static void ReadFrames(string source, PixelSize size, TimeSpan start, TimeSpan length, TimeSpan spacing,
        Action<PixelBuffer, TimeSpan> use, CancellationToken cancel)
    {
        using var reader = OpenReader(source);
        reader.Seek(start);
        // Decoders may pad the picture to a multiple of sixteen; only the real picture is used.
        var picture = new PixelRect(0, 0, Math.Min(size.Width, reader.Width), Math.Min(size.Height, reader.Height));
        var slack = TimeSpan.FromMilliseconds(1);
        TimeSpan? due = null;
        try
        {
            while (reader.Read(out var frame, out var time))
            {
                cancel.ThrowIfCancellationRequested();
                var at = time - start;
                if (at >= length) break;
                // Pictures before the start, and ones closer together than asked for, are passed over.
                if (at < (due ?? TimeSpan.Zero) - slack) continue;
                if (at < TimeSpan.Zero) at = TimeSpan.Zero;
                use(frame.Width == picture.Width && frame.Height == picture.Height ? frame : frame.Crop(picture), at);
                var next = (due ?? at) + spacing;
                due = spacing == TimeSpan.Zero ? at + slack + slack : next < at ? at + spacing : next;
            }
        }
        catch (InvalidOperationException ex)
        {
            throw new VideoEditException($"“{Path.GetFileName(source)}” could not be read to the end. {ex.Message}", ex);
        }

        if (due is null) throw new VideoEditException("There is no picture in the chosen part of the video.");
    }

    /// <summary>Copies the sound between two times out of a video into an audio file of its own.</summary>
    private static async Task ExtractSoundAsync(string source, TimeSpan start, TimeSpan end, string destination, CancellationToken cancel)
    {
        File.Create(destination).Dispose();
        var transcoder = new MediaTranscoder { TrimStartTime = start, TrimStopTime = end };
        var prepared = await transcoder.PrepareFileTranscodeAsync(await OpenAsync(source, "video"),
            await StorageFile.GetFileFromPathAsync(Path.GetFullPath(destination)), MediaEncodingProfile.CreateM4a(AudioEncodingQuality.High));
        if (!prepared.CanTranscode) throw new VideoEditException($"Windows could not read the sound of the video ({prepared.FailureReason}).");
        var operation = prepared.TranscodeAsync();
        using var registration = cancel.Register(operation.Cancel);
        try
        {
            await operation;
        }
        catch (TaskCanceledException)
        {
            throw new OperationCanceledException(cancel);
        }
    }

    /// <summary>Adds the sound an edit asks for to a composition whose picture is already cut to length.</summary>
    /// <param name="ownSound">The sound of the video itself, already cut to length, or null when the composition's clip carries it.</param>
    private static async Task AddSoundAsync(MediaComposition composition, VideoEdit edit, VideoInfo info, string? ownSound)
    {
        var length = edit.OutputDuration(info);
        if (ownSound is not null)
        {
            var own = await LoadAudioAsync(ownSound);
            if (own.OriginalDuration > length) own.TrimTimeFromEnd = own.OriginalDuration - length;
            own.Volume = edit.Volume;
            composition.BackgroundAudioTracks.Add(own);
        }

        if (edit.AudioPath is { } path && edit.AudioVolume > 0)
        {
            var added = await LoadAudioAsync(path);
            // Audio that runs on after the picture ends would leave the video frozen on its last frame.
            if (added.OriginalDuration > length) added.TrimTimeFromEnd = added.OriginalDuration - length;
            added.Volume = edit.AudioVolume;
            composition.BackgroundAudioTracks.Add(added);
        }
    }

    private static MediaEncodingProfile Profile(PixelSize size, uint rate, bool sound)
    {
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        if (!sound) profile.Audio = null;
        profile.Video.Width = (uint)size.Width;
        profile.Video.Height = (uint)size.Height;
        profile.Video.FrameRate.Numerator = rate;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        profile.Video.Bitrate = (uint)Math.Clamp((long)size.Width * size.Height * rate / 8, 1_500_000, 20_000_000);
        return profile;
    }

    private static async Task RenderAsync(MediaComposition composition, MediaEncodingProfile profile, string destination,
        IProgress<double>? progress, double from, double to, CancellationToken cancel)
    {
        StorageFile file;
        try
        {
            File.Create(destination).Dispose();
            file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(destination));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new VideoEditException($"“{destination}” could not be written. {ex.Message}", ex);
        }

        try
        {
            var operation = composition.RenderToFileAsync(file, MediaTrimmingPreference.Precise, profile);
            operation.Progress = (_, percent) => progress?.Report(from + (to - from) * percent / 100);
            using var registration = cancel.Register(operation.Cancel);
            var reason = await operation;
            if (reason != TranscodeFailureReason.None)
                throw new VideoEditException($"Windows could not encode the video ({reason}).");
        }
        catch (TaskCanceledException)
        {
            throw new OperationCanceledException(cancel);
        }
    }

    /// <summary>Writes the edited video as an MP4 file.</summary>
    /// <param name="progress">Receives how far the export is, from 0 to 1.</param>
    public static Task ExportMp4Async(string source, VideoEdit edit, string destination,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            return Task.FromException(new VideoEditException("Choose a different name: a video cannot be saved over the file it is being read from."));
        // Everything runs away from the caller's thread: parts of the work wait on the encoder.
        return Task.Run(() => ExportMp4CoreAsync(source, edit, destination, progress, cancel), CancellationToken.None);
    }

    private static async Task ExportMp4CoreAsync(string source, VideoEdit edit, string destination, IProgress<double>? progress, CancellationToken cancel)
    {
        var clip = await LoadAsync(source);
        var info = Describe(clip);
        edit = edit.Normalize(info);
        uint rate = FrameRate(clip);
        var size = edit.OutputSize(info);
        var length = edit.OutputDuration(info);
        bool sound = edit.HasSound(info);

        string? cropped = null, ownSound = null;
        bool done = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            if (edit.Crop is not { } crop)
            {
                clip.TrimTimeFromStart = edit.Start;
                clip.TrimTimeFromEnd = info.Duration - edit.Start - length;
                clip.Volume = edit.Volume;
                var composition = new MediaComposition();
                composition.Clips.Add(clip);
                await AddSoundAsync(composition, edit, info, ownSound: null);
                await RenderAsync(composition, Profile(size, rate, sound), destination, progress, 0, 1, cancel);
                done = true;
                return;
            }

            // Windows has no dependable way to crop while it converts, so the picture is cut frame by
            // frame into a silent video first, and the sound is put back on afterwards.
            cropped = sound ? destination + ".picture.mp4" : destination;
            double share = sound ? 0.7 : 1.0;
            using (var sink = new Mp4Sink(cropped, crop.Size, (int)rate))
            {
                ReadFrames(source, info.Size, edit.Start, length, TimeSpan.Zero, (frame, at) =>
                {
                    sink.Add(frame.Crop(crop), at);
                    progress?.Report(share * Math.Min(1, at / length));
                }, cancel);
                await sink.FinishAsync(length);
            }

            if (sound)
            {
                var picture = await LoadAsync(cropped);
                picture.Volume = 0;
                var withSound = new MediaComposition();
                withSound.Clips.Add(picture);
                if (info.HasAudio && edit.Volume > 0)
                {
                    ownSound = destination + ".sound.m4a";
                    await ExtractSoundAsync(source, edit.Start, edit.Start + length, ownSound, cancel);
                }

                await AddSoundAsync(withSound, edit, info, ownSound);
                await RenderAsync(withSound, Profile(size, rate, sound: true), destination, progress, share, 1, cancel);
            }

            done = true;
        }
        catch (RecordingUnavailableException ex)
        {
            throw new VideoEditException(ex.Message, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VideoEditException($"“{destination}” could not be written. {ex.Message}", ex);
        }
        finally
        {
            if (cropped is not null && cropped != destination) TryDelete(cropped);
            if (ownSound is not null) TryDelete(ownSound);
            if (!done) TryDelete(destination);
        }
    }

    /// <summary>Writes the edited video as an animated GIF. Sound is dropped, and very wide pictures are scaled down.</summary>
    public static Task ExportGifAsync(string source, VideoEdit edit, string destination, int framesPerSecond,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        ArgumentNullException.ThrowIfNull(edit);
        return Task.Run(() => ExportGifCoreAsync(source, edit, destination, framesPerSecond, progress, cancel), CancellationToken.None);
    }

    private static async Task ExportGifCoreAsync(string source, VideoEdit edit, string destination, int framesPerSecond, IProgress<double>? progress, CancellationToken cancel)
    {
        var info = Describe(await LoadAsync(source));
        edit = edit.Normalize(info);
        var length = edit.OutputDuration(info);
        var size = edit.Crop?.Size ?? info.Size;
        var shown = size.Width > MaxGifWidth
            ? new PixelSize(MaxGifWidth, Math.Max(1, (int)Math.Round((double)size.Height * MaxGifWidth / size.Width)))
            : size;
        var interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(framesPerSecond, 1, 30));
        bool done = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
            using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
            var writer = new GifWriter(output, shown.Width, shown.Height);
            ReadFrames(source, info.Size, edit.Start, length, interval, (frame, at) =>
            {
                if (edit.Crop is { } crop) frame = frame.Crop(crop);
                if (shown != size) frame = ImageEffects.Resize(frame, shown.Width, shown.Height);
                writer.AddFrame(frame, at);
                progress?.Report(Math.Min(1, at / length));
            }, cancel);
            writer.Finish(length);
            done = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new VideoEditException($"“{destination}” could not be written. {ex.Message}", ex);
        }
        finally
        {
            if (!done) TryDelete(destination);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
