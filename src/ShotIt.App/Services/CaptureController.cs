using System.Diagnostics;
using System.Runtime;
using System.Windows;
using System.Windows.Threading;
using Serilog;
using ShotIt.App.Infrastructure;
using ShotIt.App.Overlay;
using ShotIt.Capture;
using ShotIt.Core.Display;
using ShotIt.Core.Editing;
using ShotIt.Core.Geometry;
using ShotIt.Core.Settings;
using ShotIt.Native;

namespace ShotIt.App.Services;

/// <summary>
/// Runs capture sessions: freezes the screen, shows one overlay per monitor, and turns the user's
/// choice into a <see cref="CaptureResult"/>. Also owns every way a session can end early
/// (Esc, focus loss, display change, sleep, watchdog) so the screen can never stay stuck dimmed.
/// </summary>
public sealed class CaptureController
{
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(10);

    private readonly AppServices _app;
    private readonly ILogger _log;
    private readonly List<OverlayWindow> _windows = [];
    private readonly DispatcherTimer _watchdog;
    private readonly DispatcherTimer _focusCheck;
    private readonly uint _processId = (uint)Environment.ProcessId;
    private MonitorTopology? _windowTopology;
    private OverlaySession? _session;
    private CountdownWindow? _countdown;

    public CaptureController(AppServices app)
    {
        _app = app;
        _log = app.Log;
        _watchdog = new DispatcherTimer { Interval = SessionTimeout };
        _watchdog.Tick += (_, _) => CancelSession("session timed out");
        _focusCheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _focusCheck.Tick += (_, _) => OnFocusCheck();
    }

    public bool IsSessionActive => _session is not null;

    /// <summary>The session in progress, for the self-test.</summary>
    internal OverlaySession? CurrentSession => _session;

    /// <summary>Replaces the real screen capture with a supplied frame. Used only by the self-test.</summary>
    internal Func<ScreenFrame>? FrameSourceOverride { get; set; }

    /// <summary>Raised with the duration from request to visible overlay; used by the self-test and logs.</summary>
    public event Action<TimeSpan>? OverlayShown;

    // ------------------------------------------------------------------ window pool

    /// <summary>Creates the overlay windows ahead of time so the first capture is as fast as the rest.</summary>
    public void Prewarm()
    {
        if (_session is not null) return;
        try
        {
            EnsureWindows(Monitors.GetTopology());
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Overlay prewarm failed");
        }
    }

    private void EnsureWindows(MonitorTopology topology)
    {
        if (_windowTopology is not null && _windowTopology.LayoutEquals(topology) && _windows.Count == topology.Monitors.Count) return;
        DisposeWindows();
        foreach (var monitor in topology.Monitors)
        {
            var window = new OverlayWindow(monitor);
            window.FocusLost += OnOverlayFocusLost;
            _windows.Add(window);
        }

        _windowTopology = topology;
    }

    private void DisposeWindows()
    {
        foreach (var window in _windows)
        {
            window.FocusLost -= OnOverlayFocusLost;
            window.Detach();
            window.Close();
        }

        _windows.Clear();
        _windowTopology = null;
    }

    /// <summary>The monitor layout changed: any open session is stale and the pooled windows are the wrong size.</summary>
    public void OnDisplayChanged()
    {
        CancelSession("display configuration changed");
        DisposeWindows();
        // Rebuild lazily once the new layout has settled.
        _app.Dispatcher.BeginInvoke(Prewarm, DispatcherPriority.ApplicationIdle);
    }

    public void OnSuspending() => CancelSession("system is going to sleep");

    // ------------------------------------------------------------------ starting a session

    public void StartRegion(OverlayMode mode = OverlayMode.Region, PixelRect? preselect = null)
    {
        if (_session is not null)
        {
            // Pressing the hotkey again while the overlay is open just brings it back to the front.
            FocusOverlayUnderCursor();
            return;
        }

        CloseCountdown();
        var watch = Stopwatch.StartNew();
        try
        {
            if (WindowList.IsExclusiveFullScreenAppRunning())
            {
                // An overlay cannot appear on top of an exclusive full-screen game; capture directly instead.
                CaptureFullScreen(allMonitors: false, viaFullScreenApp: true);
                return;
            }

            var settings = _app.Settings;
            _app.CaptureService.Preference = settings.Capture.Backend;
            var frame = FrameSourceOverride?.Invoke() ?? _app.CaptureService.CaptureAll(settings.Capture.IncludeCursor);
            var snap = SnapIndex.Build(frame.VirtualBounds, _processId);

            if (mode == OverlayMode.FixedSize)
            {
                var cursor = Monitors.GetCursorPosition();
                var monitor = frame.Topology.FromPoint(cursor);
                preselect = new PixelRect(cursor.X - settings.Capture.FixedWidth / 2, cursor.Y - settings.Capture.FixedHeight / 2,
                    settings.Capture.FixedWidth, settings.Capture.FixedHeight).MoveInside(monitor.Bounds);
            }

            var session = new OverlaySession(frame, snap, settings, mode, preselect);
            session.ActionRequested += OnAction;
            EnsureWindows(frame.Topology);
            _session = session;

            var cursorMonitor = frame.Topology.FromPoint(session.CursorPx);
            for (int i = 0; i < _windows.Count; i++)
            {
                var monitor = frame.Topology.Monitors[i];
                _windows[i].Attach(session, monitor, showHint: monitor == cursorMonitor && preselect is null, OnToolOptionsChanged);
            }

            // Show the windows that will not take focus first, the active one last.
            foreach (var window in _windows.Where(w => w.Monitor != cursorMonitor)) window.ShowOverlay(activate: false);
            _windows.First(w => w.Monitor == cursorMonitor).ShowOverlay(activate: true);

            if (!_app.Hotkeys.RegisterSessionEscape()) _log.Information("Esc is registered elsewhere; using the focused-window key path");
            _watchdog.Start();
            watch.Stop();
            _log.Information("Overlay shown in {Elapsed} ms ({Backend}, {Monitors} monitor(s), capture {Capture} ms)",
                watch.ElapsedMilliseconds, frame.Backend, frame.Topology.Monitors.Count, (int)frame.Elapsed.TotalMilliseconds);
            OverlayShown?.Invoke(watch.Elapsed);
        }
        catch (CaptureUnavailableException ex)
        {
            _log.Information("Capture unavailable: {Message}", ex.Message);
            EndSession();
            _app.Notifier.Warn("Cannot capture right now", ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Starting a capture session failed");
            EndSession();
            _app.Notifier.Warn("Capture failed", "ShotIt could not start the capture. Details are in the log file.");
        }
    }

    public void StartDelayed()
    {
        if (_session is not null || _countdown is not null) return;
        var countdown = new CountdownWindow(_app.Settings.Capture.DelaySeconds);
        _countdown = countdown;
        countdown.Finished += () =>
        {
            if (!ReferenceEquals(_countdown, countdown)) return;
            _countdown = null;
            // Let the countdown window disappear from the screen before the screen is frozen.
            _app.Dispatcher.BeginInvoke(() => StartRegion(), DispatcherPriority.ApplicationIdle);
        };
        countdown.Cancelled += () =>
        {
            if (ReferenceEquals(_countdown, countdown)) _countdown = null;
        };
        countdown.Show();
    }

    private void CloseCountdown()
    {
        var countdown = _countdown;
        _countdown = null;
        countdown?.Abort();
    }

    public void RepeatLastRegion()
    {
        if (_app.Settings.Capture.LastRegion is not { Length: 4 } r)
        {
            StartRegion();
            return;
        }

        var region = new PixelRect(r[0], r[1], r[2], r[3]);
        if (_app.Settings.Capture.AfterRegion == AfterCaptureAction.ShowToolbar)
        {
            StartRegion(OverlayMode.Preselected, region);
            return;
        }

        CaptureDirect(() =>
        {
            var frame = _app.CaptureService.CaptureAll(_app.Settings.Capture.IncludeCursor);
            var clipped = region.Intersect(frame.VirtualBounds);
            if (clipped.IsEmpty) throw new CaptureUnavailableException("The last region is no longer on any monitor.");
            return new CaptureResult(DocState.FromImage(frame.Crop(clipped)), screenRegion: clipped);
        }, _app.Settings.Capture.AfterRegion, "Region captured");
    }

    public void CaptureFullScreen(bool allMonitors, bool viaFullScreenApp = false)
    {
        if (_session is not null) return;
        CaptureDirect(() =>
        {
            _app.CaptureService.Preference = _app.Settings.Capture.Backend;
            var frame = _app.CaptureService.CaptureAll(_app.Settings.Capture.IncludeCursor);
            var region = allMonitors ? frame.VirtualBounds : frame.Topology.FromPoint(Monitors.GetCursorPosition()).Bounds;
            return new CaptureResult(DocState.FromImage(frame.Crop(region)), screenRegion: region);
        }, _app.Settings.Capture.AfterFullScreen, viaFullScreenApp ? "Full screen captured (a full-screen app is running)" : "Full screen captured");
    }

    public void CaptureActiveWindow()
    {
        if (_session is not null) return;
        CaptureDirect(() =>
        {
            nint foreground = WindowList.GetForeground();
            var capture = _app.CaptureService.CaptureWindow(foreground, _app.Settings.Capture.IncludeCursor)
                ?? throw new CaptureUnavailableException("There is no active window to capture.");
            return new CaptureResult(DocState.FromImage(capture.Image), capture.ProcessName, capture.Title, capture.Bounds);
        }, _app.Settings.Capture.AfterFullScreen, "Window captured");
    }

    private void CaptureDirect(Func<CaptureResult> capture, AfterCaptureAction after, string doneMessage)
    {
        try
        {
            var result = capture();
            var action = after == AfterCaptureAction.ShowToolbar ? OverlayAction.Edit : OverlaySession.Map(after);
            _app.Actions.Run(result, action, notifyOnCopy: doneMessage);
        }
        catch (CaptureUnavailableException ex)
        {
            _app.Notifier.Warn("Cannot capture right now", ex.Message);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Direct capture failed");
            _app.Notifier.Warn("Capture failed", "ShotIt could not capture the screen. Details are in the log file.");
        }
    }

    // ------------------------------------------------------------------ during a session

    private void OnToolOptionsChanged(ToolOptions options)
    {
        _app.Settings.Editor.Tool = options;
        _app.SaveSettingsSoon();
    }

    private void FocusOverlayUnderCursor()
    {
        if (_session is null) return;
        var monitor = _session.Topology.FromPoint(Monitors.GetCursorPosition());
        _windows.FirstOrDefault(w => w.Monitor == monitor)?.TakeFocus();
    }

    /// <summary>Esc from the session-wide hotkey: cancels the innermost thing in progress.</summary>
    public void HandleEscape()
    {
        if (_session is null)
        {
            _app.Hotkeys.UnregisterSessionEscape();
            return;
        }

        foreach (var window in _windows)
            if (window.CancelTextEdit()) return;
        _session.Escape();
    }

    private void OnOverlayFocusLost()
    {
        if (_session is null) return;
        _focusCheck.Stop();
        _focusCheck.Start();
    }

    private void OnFocusCheck()
    {
        _focusCheck.Stop();
        if (_session is null) return;
        nint foreground = WindowList.GetForeground();
        // Our own menus, pop-ups and text boxes also deactivate the overlay; those are fine.
        if (foreground == 0 || WindowList.GetProcessId(foreground) == _processId) return;

        FocusOverlayUnderCursor();
        nint after = WindowList.GetForeground();
        if (after != 0 && WindowList.GetProcessId(after) != _processId)
            CancelSession("another application took the foreground");
    }

    public void CancelSession(string reason)
    {
        if (_session is null)
        {
            CloseCountdown();
            return;
        }

        _log.Information("Capture session cancelled: {Reason}", reason);
        EndSession();
    }

    private void EndSession()
    {
        _watchdog.Stop();
        _focusCheck.Stop();
        _app.Hotkeys.UnregisterSessionEscape();
        var session = _session;
        _session = null;
        if (session is not null)
        {
            session.ActionRequested -= OnAction;
            session.End();
        }

        foreach (var window in _windows)
        {
            try
            {
                window.Detach();
                window.Hide();
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Hiding an overlay window failed");
            }
        }

        if (session is not null)
        {
            // A frozen 4K frame is tens of megabytes; hand it back promptly instead of waiting for memory pressure.
            _app.Dispatcher.BeginInvoke(() =>
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
            }, DispatcherPriority.ApplicationIdle);
        }
    }

    private void OnAction(OverlayAction action)
    {
        var session = _session;
        if (session is null) return;
        if (action == OverlayAction.Cancel)
        {
            EndSession();
            return;
        }

        try
        {
            foreach (var window in _windows) window.CommitTextEdit();
            var built = session.BuildResult();
            if (built is null) return;

            _app.Settings.Capture.LastRegion = [built.ScreenRegion.X, built.ScreenRegion.Y, built.ScreenRegion.Width, built.ScreenRegion.Height];
            _app.Settings.Editor.Tool = session.Editor.Options;
            _app.SaveSettingsSoon();

            // Take the overlay away first so the screen is usable again immediately.
            EndSession();
            var result = new CaptureResult(built.Document, built.SourceApp, built.SourceTitle, built.ScreenRegion);
            _app.Actions.Run(result, action);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Completing the capture failed");
            EndSession();
            _app.Notifier.Warn("Capture failed", "ShotIt could not finish the capture. Details are in the log file.");
        }
    }

    public void Shutdown()
    {
        CancelSession("application is exiting");
        CloseCountdown();
        DisposeWindows();
    }
}
