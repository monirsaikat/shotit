using System.Diagnostics;
using ShotIt.Core.Imaging;

namespace ShotIt.Capture;

/// <summary>The area being captured: something that can be photographed and asked to scroll down.</summary>
public interface IScrollSource
{
    PixelBuffer Grab();

    /// <summary>Scrolls down by a number of wheel notches. Returns false when scrolling should stop (the user took the mouse back).</summary>
    bool Scroll(int notches);
}

public sealed record ScrollCaptureOptions
{
    /// <summary>The joined picture is cut off at this height.</summary>
    public int MaxHeight { get; init; } = 20000;

    public int MaxSteps { get; init; } = 500;

    /// <summary>How long to wait for the area to react to a scroll before deciding it did not move.</summary>
    public TimeSpan ReactionTimeout { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>How long a smooth-scrolling animation may take to come to rest.</summary>
    public TimeSpan SettleTimeout { get; init; } = TimeSpan.FromMilliseconds(1500);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(40);

    /// <summary>Extra time given to a page that seems to have ended, in case more content is still loading.</summary>
    public TimeSpan EndOfPageWait { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Receives one line per step, for the log.</summary>
    public Action<string>? Trace { get; init; }
}

public enum ScrollStopReason
{
    ReachedEnd,
    Stopped,
    HeightLimit,
    StepLimit,

    /// <summary>The content changed in a way that could not be followed; the picture ends where it still could.</summary>
    LostTrack,

    /// <summary>The area never moved, so the picture is just what was visible.</summary>
    NothingScrolled,
}

public sealed record ScrollCaptureResult(PixelBuffer Image, ScrollStopReason Reason, int Frames);

/// <summary>
/// Scrolls an area step by step and joins what it shows into one tall picture. Each step waits for the
/// area to come to rest, so smooth scrolling and content that loads late do not tear the result.
/// </summary>
public static class ScrollingCapture
{
    /// <param name="progress">Receives the height of the picture so far.</param>
    /// <param name="stop">Ends the capture early; the picture so far is still returned.</param>
    public static async Task<ScrollCaptureResult> RunAsync(IScrollSource source, ScrollCaptureOptions options,
        IProgress<int>? progress = null, CancellationToken stop = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);

        var stitcher = new ScrollStitcher(options.MaxHeight);
        var current = await SettleAsync(source, new ScrollFrame(source.Grab()), options).ConfigureAwait(false);
        var reason = stitcher.Add(current) == ScrollAddResult.Full ? ScrollStopReason.HeightLimit : ScrollStopReason.StepLimit;
        progress?.Report(stitcher.Height);

        int notches = 1, idle = 0;
        double farthestPerNotch = 0;
        for (int step = 0; step < options.MaxSteps && reason == ScrollStopReason.StepLimit; step++)
        {
            if (stop.IsCancellationRequested || !source.Scroll(notches))
            {
                reason = ScrollStopReason.Stopped;
                break;
            }

            var next = await AfterScrollAsync(source, current, options).ConfigureAwait(false);
            var added = stitcher.Add(next);
            if (added == ScrollAddResult.NoMatch)
            {
                // The area may still have been moving or loading; look once more before giving up.
                await Task.Delay(options.EndOfPageWait, CancellationToken.None).ConfigureAwait(false);
                next = await SettleAsync(source, new ScrollFrame(source.Grab()), options).ConfigureAwait(false);
                added = stitcher.Add(next);
            }

            options.Trace?.Invoke($"step {step}: {notches} notch(es) -> {added}, moved {stitcher.LastOffset}, height {stitcher.Height}");
            switch (added)
            {
                case ScrollAddResult.NoMovement:
                    if (++idle >= 2) reason = stitcher.FrameCount == 1 ? ScrollStopReason.NothingScrolled : ScrollStopReason.ReachedEnd;
                    else await Task.Delay(options.EndOfPageWait, CancellationToken.None).ConfigureAwait(false);
                    break;

                case ScrollAddResult.NoMatch:
                    reason = ScrollStopReason.LostTrack;
                    break;

                default:
                    idle = 0;
                    current = next;
                    progress?.Report(stitcher.Height);
                    if (added == ScrollAddResult.Full)
                    {
                        reason = ScrollStopReason.HeightLimit;
                        break;
                    }

                    // Take bigger steps once it is known how far one notch goes, but keep well over half of
                    // each picture overlapping the last. A slow window may still be finishing an earlier step,
                    // so the farthest a notch has ever gone is what counts, and steps grow only gradually.
                    farthestPerNotch = Math.Max(farthestPerNotch, (double)stitcher.LastOffset / notches);
                    notches = Math.Clamp((int)(next.Height * 0.35 / farthestPerNotch), 1, Math.Min(10, notches * 2));
                    break;
            }
        }

        return new ScrollCaptureResult(stitcher.Build(), reason, stitcher.FrameCount);
    }

    private static bool Moved(ScrollFrame a, ScrollFrame b) => ScrollMatcher.Match(a, b) is not { Offset: 0 };

    /// <summary>Waits for the area to start moving after a scroll, then for it to come to rest.</summary>
    private static async Task<ScrollFrame> AfterScrollAsync(IScrollSource source, ScrollFrame before, ScrollCaptureOptions options)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            await Task.Delay(options.PollInterval, CancellationToken.None).ConfigureAwait(false);
            var frame = new ScrollFrame(source.Grab());
            if (Moved(before, frame)) return await SettleAsync(source, frame, options).ConfigureAwait(false);
            if (watch.Elapsed >= options.ReactionTimeout) return frame;
        }
    }

    /// <summary>
    /// Keeps photographing until three pictures in a row are the same. Two are not enough: a window
    /// that stutters in the middle of a scroll looks still for a moment.
    /// </summary>
    private static async Task<ScrollFrame> SettleAsync(IScrollSource source, ScrollFrame frame, ScrollCaptureOptions options)
    {
        var watch = Stopwatch.StartNew();
        int still = 0;
        while (watch.Elapsed < options.SettleTimeout)
        {
            await Task.Delay(options.PollInterval, CancellationToken.None).ConfigureAwait(false);
            var again = new ScrollFrame(source.Grab());
            still = Moved(frame, again) ? 0 : still + 1;
            frame = again;
            if (still == 2) return frame;
        }

        return frame;
    }
}
