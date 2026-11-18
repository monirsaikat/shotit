using System.Diagnostics;
using System.Windows.Threading;

namespace ShotIt.App.Infrastructure;

/// <summary>
/// Limits how often an update runs, however often it is asked for. A fast mouse reports its position up
/// to a thousand times a second; repainting for each report keeps the interface thread and the painting
/// thread busy with pictures nobody sees, and the screen falls behind the pointer.
/// The first request runs at once, so nothing feels delayed; requests that follow too closely are
/// folded into one update a few milliseconds later. Must be used from the interface thread.
/// </summary>
public sealed class FrameThrottle
{
    /// <summary>Shortest time between two updates: fast enough for a 120 Hz screen.</summary>
    private static readonly long MinTicks = Stopwatch.Frequency / 120;

    private readonly Action _update;
    private readonly DispatcherTimer _timer;
    private long _last = long.MinValue / 2;
    private bool _pending;
    private bool _running;

    public FrameThrottle(Action update)
    {
        _update = update ?? throw new ArgumentNullException(nameof(update));
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += (_, _) => OnTimer();
    }

    /// <summary>Asks for the update to run: now if the last one was a while ago, otherwise shortly.</summary>
    public void Request()
    {
        if (_running)
        {
            // The update itself caused another change; pick it up afterwards instead of recursing.
            _pending = true;
            return;
        }

        long wait = MinTicks - (Stopwatch.GetTimestamp() - _last);
        if (wait <= 0)
        {
            Run();
            return;
        }

        if (_pending) return;
        _pending = true;
        _timer.Interval = TimeSpan.FromSeconds(Math.Max(0.001, (double)wait / Stopwatch.Frequency));
        _timer.Start();
    }

    /// <summary>Drops a pending update.</summary>
    public void Cancel()
    {
        _pending = false;
        _timer.Stop();
    }

    private void OnTimer()
    {
        _timer.Stop();
        if (_pending) Run();
    }

    private void Run()
    {
        _pending = false;
        _timer.Stop();
        _running = true;
        try
        {
            _update();
        }
        finally
        {
            _running = false;
            _last = Stopwatch.GetTimestamp();
        }

        if (_pending)
        {
            _pending = false;
            Request();
        }
    }
}
