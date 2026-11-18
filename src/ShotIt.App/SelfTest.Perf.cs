using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ShotIt.App.Infrastructure;
using ShotIt.App.Overlay;
using ShotIt.App.Services;
using ShotIt.Capture;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using ShotIt.Core.Settings;
using ShotIt.Native;

namespace ShotIt.App;

/// <summary>
/// The "perf" scenario: measures how smooth the capture overlay is. It opens the overlay over a
/// desktop-like picture, moves and drags through the same code paths the mouse uses, and reports
/// how long each frame took and how long the screen was blocked at each step.
/// It shares the desktop with whoever is at the computer: part of it moves the real pointer, and using
/// the mouse or keyboard while it runs changes the numbers or makes it fail. It is not run by CI.
/// </summary>
internal static partial class SelfTest
{
    /// <summary>Frame times of one measured phase, in milliseconds.</summary>
    private sealed record FrameStats(int Frames, double AverageMs, double P95Ms, double WorstMs, double UiAverageMs, double UiWorstMs, double CpuPercent);

    /// <summary>A picture with flat areas, edges and some busy patches, roughly like a desktop full of windows.</summary>
    private static ScreenFrame DesktopLikeFrame()
    {
        var topology = Monitors.GetTopology();
        var bounds = topology.VirtualBounds;
        var image = new PixelBuffer(bounds.Width, bounds.Height);
        byte[] d = image.Data;
        for (int y = 0; y < bounds.Height; y++)
        {
            int row = y * image.Stride;
            for (int x = 0; x < bounds.Width; x++)
            {
                int i = row + x * 4;
                int block = (x / 96) * 7 + (y / 64) * 13;
                bool busy = block % 5 == 0 && (y / 4) % 3 != 0;
                int noise = busy ? ((x * 2654435761u + (uint)y * 40503u) >> 13 & 1) == 0 ? 0 : 150 : 0;
                d[i] = (byte)((block * 29 + 40 + noise) & 0xFF);
                d[i + 1] = (byte)((block * 17 + 60 + noise) & 0xFF);
                d[i + 2] = (byte)((block * 11 + 80 + noise) & 0xFF);
                d[i + 3] = 255;
            }
        }

        return new ScreenFrame(image, topology, "Synthetic", TimeSpan.Zero);
    }

    /// <summary>Runs <paramref name="step"/> once per displayed frame for a while and reports how long the frames took.</summary>
    private static async Task<FrameStats> MeasureFrames(Dispatcher dispatcher, int milliseconds, Action<int> step)
    {
        var intervals = new List<double>();
        var ui = new List<double>();
        using var process = Process.GetCurrentProcess();
        var cpuBefore = process.TotalProcessorTime;
        var clock = Stopwatch.StartNew();
        double last = -1;
        int index = 0;

        void OnRendering(object? sender, EventArgs e)
        {
            double now = clock.Elapsed.TotalMilliseconds;
            if (last >= 0) intervals.Add(now - last);
            last = now;
            step(index++);
            // Layout and painting run right after this event; a lower priority callback marks their end.
            dispatcher.BeginInvoke(() => ui.Add(clock.Elapsed.TotalMilliseconds - now), DispatcherPriority.Loaded);
        }

        CompositionTarget.Rendering += OnRendering;
        await Task.Delay(milliseconds);
        CompositionTarget.Rendering -= OnRendering;
        process.Refresh();
        // Share of one processor core the whole process used, painting thread included.
        double cpu = Math.Round((process.TotalProcessorTime - cpuBefore).TotalMilliseconds / clock.Elapsed.TotalMilliseconds * 100, 1);
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);

        if (intervals.Count == 0) return new FrameStats(0, 0, 0, 0, 0, 0, cpu);
        var sorted = intervals.Order().ToList();
        return new FrameStats(intervals.Count, Math.Round(intervals.Average(), 2), Math.Round(sorted[(int)(sorted.Count * 0.95)], 2),
            Math.Round(sorted[^1], 2), ui.Count == 0 ? 0 : Math.Round(ui.Average(), 2), ui.Count == 0 ? 0 : Math.Round(ui.Max(), 2), cpu);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "PostMessageW", ExactSpelling = true)]
    private static extern int PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>What a fast mouse did to the overlay: how far behind the pointer the overlay fell.</summary>
    private sealed record InputStats(int Sent, double AverageLagMs, double WorstLagMs, double DrainMs, double ScreenCatchUpMs, FrameStats Frames);

    /// <summary>
    /// Sends mouse moves to the overlay window the way a 1000 Hz mouse does, through the normal window
    /// message path, and measures how far behind the newest position the overlay is at each frame.
    /// </summary>
    private static async Task<InputStats> MeasureInput(Dispatcher dispatcher, OverlayWindow window, OverlaySession session, bool buttonDown, int milliseconds)
    {
        const uint MouseMove = 0x0200;
        const int Span = 1400, Left = 200, Top = 200;
        var bounds = window.Monitor.Bounds;
        nint hwnd = window.Handle;
        int sent = 0;
        using var stop = new CancellationTokenSource();

        var poster = Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            while (!stop.IsCancellationRequested)
            {
                int due = (int)clock.ElapsedMilliseconds;
                while (sent < due)
                {
                    int i = sent;
                    int x = Left + i % Span, y = Top + (i / Span) % 600;
                    // While a button is held WPF reads the real pointer position, so a drag has to move the real pointer.
                    if (buttonDown) MouseInput.MoveTo(new PixelPoint(bounds.X + x, bounds.Y + y));
                    else _ = PostMessage(hwnd, MouseMove, 0, (y << 16) | (x & 0xFFFF));
                    Volatile.Write(ref sent, i + 1);
                }

                Thread.SpinWait(200);
            }
        });

        var lags = new List<double>();
        int Processed()
        {
            var p = session.CursorPx;
            int x = p.X - bounds.X - Left, y = p.Y - bounds.Y - Top;
            return x < 0 || y < 0 || x >= Span ? -1 : y * Span + x;
        }

        var frames = await MeasureFrames(dispatcher, milliseconds, _ =>
        {
            int done = Processed(), total = Volatile.Read(ref sent);
            if (done >= 0 && total > 0) lags.Add(Math.Max(0, total - 1 - done));
        });
        stop.Cancel();
        await poster;

        // One message per millisecond was sent, so a backlog of n messages is n milliseconds of lag.
        var drain = Stopwatch.StartNew();
        while (Processed() != sent - 1 && drain.ElapsedMilliseconds < 5000)
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

        // The mouse has stopped. How long until the screen shows the region where the mouse ended up?
        double catchUp = -1;
        if (buttonDown && session.Selection.Current is { IsEmpty: false } final)
        {
            var probe = new PixelRect(final.Right, final.Y + final.Height / 2, 1, 1);
            catchUp = await Task.Run(() =>
            {
                while (drain.ElapsedMilliseconds < 5000)
                {
                    var pixel = GdiScreen.Capture(probe).GetPixel(0, 0);
                    if (pixel.R == 0x3D && pixel.G == 0x7B && pixel.B == 0xFF) return Math.Round(drain.Elapsed.TotalMilliseconds, 1);
                }

                return -1.0;
            });
        }

        return new InputStats(sent, lags.Count == 0 ? -1 : Math.Round(lags.Average(), 1), lags.Count == 0 ? -1 : lags.Max(),
            Math.Round(drain.Elapsed.TotalMilliseconds, 1), catchUp, frames);
    }

    private static double Timed(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        return Math.Round(watch.Elapsed.TotalMilliseconds, 1);
    }

    private static async Task Perf(AppServices app, Report report)
    {
        var dispatcher = app.Dispatcher;
        var frame = DesktopLikeFrame();
        var primary = frame.Topology.Primary;
        var area = primary.Bounds;
        report.Values["monitors"] = frame.Topology.Monitors.Select(m => $"{m.Bounds} @ {m.Dpi} dpi").ToArray();
        report.Values["renderTier"] = RenderCapability.Tier >> 16;
        app.Controller.FrameSourceOverride = () => frame;
        app.Settings.Capture.AfterRegion = AfterCaptureAction.ShowToolbar;
        app.Settings.Capture.ShowMagnifier = true;
        app.Settings.Capture.SnapToWindows = true;

        TimeSpan shown = TimeSpan.Zero;
        app.Controller.OverlayShown += t => shown = t;

        // A real capture of the screen, for the time the hotkey spends before the overlay can be built.
        var real = new List<double>();
        for (int i = 0; i < 5; i++) real.Add(Math.Round(app.CaptureService.CaptureAll(includeCursor: false).Elapsed.TotalMilliseconds, 1));
        report.Values["realCaptureMs"] = real.ToArray();

        // Warm up once so one-time costs are not counted.
        app.Controller.StartRegion();
        await Settle(600);
        app.Controller.CancelSession("self-test");
        await Settle(300);

        var starts = new List<double>();
        var blocked = new List<double>();
        for (int i = 0; i < 5; i++)
        {
            double start = Timed(() => app.Controller.StartRegion());
            // How long the screen stays unresponsive after the hotkey: until the first frame has been painted.
            var watch = Stopwatch.StartNew();
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            blocked.Add(Math.Round(start + watch.Elapsed.TotalMilliseconds, 1));
            starts.Add(Math.Round(shown.TotalMilliseconds, 1));
            await Settle(200);
            app.Controller.CancelSession("self-test");
            await Settle(200);
        }

        report.Values["overlayStartMs"] = starts.ToArray();
        report.Values["overlayFirstFrameMs"] = blocked.ToArray();

        app.Controller.StartRegion();
        await Settle(500);
        var session = app.Controller.CurrentSession;
        if (session is null)
        {
            report.Failures.Add("No session for the smoothness check");
            return;
        }

        // 0. A fast mouse, through the real input path: first just moving, then dragging out a region.
        if (app.Controller.OverlayWindows.FirstOrDefault(w => w.Monitor == primary) is { } target)
        {
            const uint LeftDown = 0x0201, LeftUp = 0x0202;
            report.Values["mouseHover"] = await MeasureInput(dispatcher, target, session, buttonDown: false, 2500);
            _ = PostMessage(target.Handle, LeftDown, 1, (120 << 16) | 120);
            await Settle(100);
            var pointer = Monitors.GetCursorPosition();
            report.Values["mouseDrag"] = await MeasureInput(dispatcher, target, session, buttonDown: true, 2500);
            MouseInput.MoveTo(pointer);
            _ = PostMessage(target.Handle, LeftUp, 0, (700 << 16) | 900);
            await Settle(600);
            app.Controller.CancelSession("self-test");
            await Settle(300);
            app.Controller.StartRegion();
            await Settle(500);
            session = app.Controller.CurrentSession;
            if (session is null)
            {
                report.Failures.Add("No session after the mouse check");
                return;
            }
        }

        PixelPoint At(int i, int speed) => new(
            area.X + 60 + Math.Abs((i * speed) % (2 * (area.Width - 120)) - (area.Width - 120)),
            area.Y + 60 + Math.Abs((i * speed * 5 / 9) % (2 * (area.Height - 120)) - (area.Height - 120)));

        // 1. Moving the pointer before anything is selected (crosshair, loupe, window highlight).
        var hover = await MeasureFrames(dispatcher, 2500, i => session.MouseMove(At(i, 9)));
        report.Values["hover"] = hover;

        // 2. The cost of handling one mouse move on its own, without painting.
        double perMove = Timed(() =>
        {
            for (int i = 0; i < 2000; i++) session.MouseMove(At(i, 3));
        }) / 2000;
        report.Values["mouseMoveHandlingMs"] = Math.Round(perMove, 4);

        // 3. Dragging out a region.
        var from = new PixelPoint(area.X + 40, area.Y + 40);
        session.MouseDown(from, MouseButton.Left, 1);
        var drag = await MeasureFrames(dispatcher, 2500, i => session.MouseMove(At(i, 9)));
        report.Values["dragRegion"] = drag;

        // How long a change takes to reach the screen: move the corner, then watch for the border to arrive there.
        var latency = new List<double>();
        for (int i = 0; i < 24; i++)
        {
            session.MouseMove(new PixelPoint(area.X + 500 + (i * 53) % 900, area.Y + 300 + (i * 37) % 500));
            if (session.Selection.Current is not { } now) continue;
            var probe = new PixelRect(now.Right, now.Y + now.Height / 2, 1, 1);
            var watch = Stopwatch.StartNew();
            double arrived = await Task.Run(() =>
            {
                while (watch.ElapsedMilliseconds < 1000)
                {
                    var pixel = GdiScreen.Capture(probe).GetPixel(0, 0);
                    if (pixel.R == 0x3D && pixel.G == 0x7B && pixel.B == 0xFF) return watch.Elapsed.TotalMilliseconds;
                }

                return -1.0;
            });
            latency.Add(Math.Round(arrived, 1));
            await Task.Delay(30);
        }

        report.Values["screenLatencyMs"] = latency.ToArray();

        // 4. Letting go of a large region: everything that happens before the screen responds again.
        var end = new PixelPoint(area.Right - 40, area.Bottom - 40);
        session.MouseMove(end);
        double release = Timed(() => session.MouseUp(end, MouseButton.Left));
        var afterRelease = Stopwatch.StartNew();
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        report.Values["releaseMs"] = release;
        report.Values["releaseToIdleMs"] = Math.Round(release + afterRelease.Elapsed.TotalMilliseconds, 1);
        report.Check(session.Phase == OverlayPhase.Adjusting, "The region was not selected");
        // The copy is prepared in the background; it must still arrive.
        var selected = session.Selection.Current?.Size;
        for (int i = 0; i < 40 && ClipboardService.GetImage()?.Size != selected; i++) await Settle(50);
        report.Check(selected is not null && ClipboardService.GetImage()?.Size == selected, "The selected region did not reach the clipboard");

        // 5. Moving the finished region around (toolbars visible).
        session.MouseUp(end, MouseButton.Left);
        var shrink = new PixelRect(area.X + 300, area.Y + 200, 700, 420);
        session.MouseDown(new PixelPoint(area.Right - 40, area.Bottom - 40), MouseButton.Left, 1);
        session.MouseMove(new PixelPoint(shrink.Right, shrink.Bottom));
        session.MouseUp(new PixelPoint(shrink.Right, shrink.Bottom), MouseButton.Left);
        await Settle(300);
        var grab = session.Selection.Current is { } current ? current.Center : shrink.Center;
        session.MouseDown(grab, MouseButton.Left, 1);
        var move = await MeasureFrames(dispatcher, 2500, i => session.MouseMove(new PixelPoint(grab.X + (int)(120 * Math.Sin(i / 9.0)), grab.Y + (int)(80 * Math.Cos(i / 7.0)))));
        session.MouseUp(grab, MouseButton.Left);
        report.Values["moveRegion"] = move;
        await Settle(300);

        // 6. Copying the result and closing the overlay.
        session.Selection.SetRegion(area);
        double copy = Timed(() => session.RequestAction(OverlayAction.Copy));
        report.Values["copyFullMonitorMs"] = copy;
        report.Check(!app.Controller.IsSessionActive, "The overlay did not close after copying");
        await Settle(500);
        report.Check(ClipboardService.GetImage()?.Size == area.Size, "The copied picture is not the selected monitor");

        foreach (var (name, stats) in new[] { ("hover", hover), ("dragRegion", drag), ("moveRegion", move) })
            report.Check(stats.Frames > 0 && stats.P95Ms <= 34, $"{name}: 95% of frames should take at most 34 ms, took {stats.P95Ms} ms");
        report.Check(release <= 50, $"Releasing the mouse blocked the screen for {release} ms");
        report.Check(copy <= 120, $"Copying blocked the screen for {copy} ms");
    }
}
