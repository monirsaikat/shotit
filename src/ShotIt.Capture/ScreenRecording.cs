using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using ShotIt.Core.Export;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace ShotIt.Capture;

/// <summary>Somewhere a recording's frames go: a file being written as they arrive.</summary>
public interface IVideoSink : IDisposable
{
    /// <summary>Adds what the area looked like at <paramref name="time"/> since the start. May wait when the encoder is behind.</summary>
    void Add(PixelBuffer frame, TimeSpan time);

    /// <summary>Completes the file. <paramref name="end"/> is when the recording stopped.</summary>
    Task FinishAsync(TimeSpan end);
}

/// <summary>Thrown when this computer cannot produce the requested kind of recording.</summary>
public sealed class RecordingUnavailableException : Exception
{
    public RecordingUnavailableException()
    {
    }

    public RecordingUnavailableException(string message)
        : base(message)
    {
    }

    public RecordingUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Writes an animated GIF.</summary>
public sealed class GifSink : IVideoSink
{
    private readonly FileStream _file;
    private readonly GifWriter _writer;

    public GifSink(string path, PixelSize size)
    {
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        _writer = new GifWriter(_file, size.Width, size.Height);
    }

    public void Add(PixelBuffer frame, TimeSpan time) => _writer.AddFrame(frame, time);

    public Task FinishAsync(TimeSpan end)
    {
        _writer.Finish(end);
        _file.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose() => _file.Dispose();
}

/// <summary>
/// Writes an H.264 video in an MP4 file using the encoder built into Windows. Frames are handed to the
/// encoder as it asks for them; a short queue in between lets capture carry on while a frame is compressed.
/// </summary>
public sealed class Mp4Sink : IVideoSink
{
    /// <summary>Media Foundation's MF_MT_VIDEO_NOMINAL_RANGE attribute and two of its values.</summary>
    private static readonly Guid NominalRange = new("C21B8EE5-B956-4071-8DAF-325EDF5CAB11");
    private const uint FullRange = 1, VideoRange = 2;

    private readonly BlockingCollection<(byte[] Pixels, TimeSpan Time)> _queue = new(boundedCapacity: 8);
    private readonly FileStream _file;
    private readonly IRandomAccessStream _stream;
    private readonly PixelSize _size;
    private readonly TimeSpan _frameDuration;
    private readonly Task _encoding;
    private bool _closed;

    public Mp4Sink(string path, PixelSize size, int framesPerSecond)
    {
        // H.264 works on blocks of two by two pixels, so an odd last row or column is dropped.
        _size = new PixelSize(size.Width & ~1, size.Height & ~1);
        if (_size.Width < 16 || _size.Height < 16) throw new RecordingUnavailableException("The area is too small to record as video.");
        _frameDuration = TimeSpan.FromSeconds(1.0 / framesPerSecond);
        _file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        _stream = _file.AsRandomAccessStream();
        // On the thread pool, so the encoder never waits for the thread that is feeding it frames.
        _encoding = Task.Run(() => EncodeAsync(framesPerSecond));
    }

    /// <summary>The size of the video, which is the area rounded down to even numbers.</summary>
    public PixelSize Size => _size;

    private async Task EncodeAsync(int framesPerSecond)
    {
        var input = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_size.Width, (uint)_size.Height);
        input.FrameRate.Numerator = (uint)framesPerSecond;
        input.FrameRate.Denominator = 1;
        // Screen pixels use the whole range from 0 to 255, while video keeps to 16 to 235. Unless both
        // ends say so, the numbers are stored as they are and every player then stretches them:
        // near-white turns pure white and dark greys turn black.
        input.Properties[NominalRange] = PropertyValue.CreateUInt32(FullRange);
        var source = new MediaStreamSource(new VideoStreamDescriptor(input)) { BufferTime = TimeSpan.Zero };
        source.Starting += (_, e) => e.Request.SetActualStartPosition(TimeSpan.Zero);
        source.SampleRequested += (_, e) =>
        {
            // Waiting here is how the encoder is paced; leaving the sample unset tells it the recording is over.
            if (!_queue.TryTake(out var frame, Timeout.Infinite)) return;
            var sample = MediaStreamSample.CreateFromBuffer(frame.Pixels.AsBuffer(), frame.Time);
            sample.Duration = _frameDuration;
            sample.KeyFrame = true;
            e.Request.Sample = sample;
        };

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Audio = null;
        profile.Video.Width = (uint)_size.Width;
        profile.Video.Height = (uint)_size.Height;
        profile.Video.FrameRate.Numerator = (uint)framesPerSecond;
        profile.Video.FrameRate.Denominator = 1;
        profile.Video.PixelAspectRatio.Numerator = 1;
        profile.Video.PixelAspectRatio.Denominator = 1;
        profile.Video.Properties[NominalRange] = PropertyValue.CreateUInt32(VideoRange);
        // Screens are mostly flat colour and sharp text: generous enough to keep text crisp, capped for huge areas.
        profile.Video.Bitrate = (uint)Math.Clamp((long)_size.Width * _size.Height * framesPerSecond / 8, 1_500_000, 20_000_000);

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true, AlwaysReencode = true };
        var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(source, _stream, profile);
        if (!prepared.CanTranscode)
            throw new RecordingUnavailableException($"Windows cannot encode video on this computer ({prepared.FailureReason}). Record a GIF instead.");
        await prepared.TranscodeAsync();
    }

    public void Add(PixelBuffer frame, TimeSpan time)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_encoding.IsCompleted)
        {
            // The encoder stopped by itself, which only happens when it failed; surface why.
            _encoding.GetAwaiter().GetResult();
            throw new RecordingUnavailableException("The video encoder stopped unexpectedly.");
        }

        // The encoder keeps the bytes until it has compressed them, so it gets a copy of its own. Windows
        // reads uncompressed pictures from the bottom row up, so the rows are handed over in that order.
        int stride = _size.Width * 4;
        var pixels = new byte[stride * _size.Height];
        for (int y = 0; y < _size.Height; y++)
            frame.Row(y)[..stride].CopyTo(pixels.AsSpan((_size.Height - 1 - y) * stride, stride));
        while (!_queue.TryAdd((pixels, time), 100))
            if (_encoding.IsCompleted) _encoding.GetAwaiter().GetResult();
    }

    public async Task FinishAsync(TimeSpan end)
    {
        _queue.CompleteAdding();
        await _encoding;
        await _stream.FlushAsync();
        Close();
    }

    private void Close()
    {
        if (_closed) return;
        _closed = true;
        _stream.Dispose();
        _file.Dispose();
        _queue.Dispose();
    }

    public void Dispose()
    {
        if (_closed) return;
        if (!_queue.IsAddingCompleted) _queue.CompleteAdding();
        // Let the encoder see the end of the frames and let go of the file before it is closed.
        try { _encoding.Wait(TimeSpan.FromSeconds(5)); }
        catch (AggregateException) { }
        Close();
    }
}

public sealed record RecordingOptions
{
    public int FramesPerSecond { get; init; } = 15;

    /// <summary>The recording ends by itself after this long.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(5);
}

public sealed record RecordingResult(TimeSpan Duration, int Frames, bool ReachedLimit);

/// <summary>Photographs an area at a steady rate and feeds the pictures to a video sink until told to stop.</summary>
public static class ScreenRecorder
{
    /// <param name="grab">Takes one picture of the area. Called from the recording thread.</param>
    /// <param name="decorate">Draws on a picture before it is stored (pointer, click marks, captions).</param>
    /// <param name="progress">Receives the time recorded so far, about four times a second.</param>
    /// <param name="stop">Ends the recording; the file is still completed.</param>
    public static Task<RecordingResult> RunAsync(Func<PixelBuffer> grab, IVideoSink sink, RecordingOptions options,
        Action<PixelBuffer, TimeSpan>? decorate = null, IProgress<TimeSpan>? progress = null, CancellationToken stop = default)
    {
        ArgumentNullException.ThrowIfNull(grab);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(options);
        return Task.Factory.StartNew(() => Run(grab, sink, options, decorate, progress, stop),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    private static async Task<RecordingResult> Run(Func<PixelBuffer> grab, IVideoSink sink, RecordingOptions options,
        Action<PixelBuffer, TimeSpan>? decorate, IProgress<TimeSpan>? progress, CancellationToken stop)
    {
        var interval = TimeSpan.FromSeconds(1.0 / Math.Clamp(options.FramesPerSecond, 1, 60));
        var watch = Stopwatch.StartNew();
        var lastReport = TimeSpan.FromSeconds(-1);
        int frames = 0;
        long tick = 0;
        bool limit = false;

        while (!stop.IsCancellationRequested)
        {
            var time = watch.Elapsed;
            if (time >= options.MaxDuration)
            {
                limit = true;
                break;
            }

            var frame = grab();
            decorate?.Invoke(frame, time);
            sink.Add(frame, time);
            frames++;

            if (time - lastReport >= TimeSpan.FromMilliseconds(250))
            {
                lastReport = time;
                progress?.Report(time);
            }

            // Aim for the next tick of a steady clock, skipping ticks that capture or encoding ran over.
            // A wait can end a moment early, so the tick never repeats.
            tick = Math.Max(tick + 1, watch.Elapsed.Ticks / interval.Ticks + 1);
            var wait = TimeSpan.FromTicks(tick * interval.Ticks) - watch.Elapsed;
            if (wait > TimeSpan.Zero) stop.WaitHandle.WaitOne(wait);
        }

        var end = watch.Elapsed;
        await sink.FinishAsync(end).ConfigureAwait(false);
        return new RecordingResult(end, frames, limit);
    }
}
