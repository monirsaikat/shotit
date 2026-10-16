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

    /// <summary>Largest scrolling capture in pixels (about 256 MB of BGRA), whatever the height limit in settings.</summary>
    private const int MaxScrollPixels = 64_000_000;

    private readonly AppServices _app;
    private readonly ILogger _log;
    private readonly List<OverlayWindow> _windows = [];
    private readonly DispatcherTimer _watchdog;
    private readonly DispatcherTimer _focusCheck;
    private readonly uint _processId = (uint)Environment.ProcessId;
    private MonitorTopology? _windowTopology;
    private OverlaySession? _session;
    private CountdownWindow? _countdown;
    private ScrollCaptureSession? _scroll;
    private RecordingSession? _recording;

    public CaptureController(AppServices app)
    {
        _app = app;
        _log = app.Log;
        _watchdog = new DispatcherTimer { Interval = SessionTimeout };
        _watchdog.Tick += (_, _) => CancelSession("session timed out");
        _focusCheck = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _focusCheck.Tick += (_, _) => OnFocusCheck();
    }

    public bool IsSessionActive => _session is not null || _scroll is not null || _recording is not null;

    public bool IsRecording => _recording is not null;

    /// <summary>The most recent video recorded since ShotIt started, if it is still there.</summary>
    public string? LastVideo { get; private set; }

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

    public void StartRegion(OverlayMode mode = OverlayMode.Region, PixelRect? preselect = null,
        AfterCaptureAction? afterRegionOverride = null, OverlayPurpose purpose = OverlayPurpose.Capture)
    {
        if (_scroll is not null || _recording is not null) return;
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

            var session = new OverlaySession(frame, snap, settings, mode, preselect, afterRegionOverride) { Purpose = purpose };
            session.ActionRequested += OnAction;
            session.SelectionCompleted += OnSelectionCompleted;
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
        if (IsSessionActive || _countdown is not null) return;
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
        if (_scroll is not null || _recording is not null) return;
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
        if (IsSessionActive) return;
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
        if (IsSessionActive) return;
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

    // ------------------------------------------------------------------ scrolling capture

    /// <summary>Asks for the area that scrolls, then scrolls it to the end and joins everything it showed.</summary>
    public void StartScrolling()
    {
        if (IsSessionActive) return;
        // Any action other than the toolbar makes the session report the region as soon as it is selected.
        StartRegion(OverlayMode.Region, afterRegionOverride: AfterCaptureAction.Copy, purpose: OverlayPurpose.Scrolling);
    }

    private (string App, string Title) DescribeSource(PixelRect region)
    {
        if (WindowList.DescribeWindow(WindowList.GetForeground()) is { } window && window.Bounds.Contains(region.Center))
            return (WindowList.GetProcessName(window.ProcessId), window.Title);
        return (string.Empty, string.Empty);
    }

    /// <summary>Scrolls the given part of the screen and delivers the joined picture like any other capture.</summary>
    internal async Task StartScrollingAt(PixelRect area)
    {
        if (IsSessionActive) return;
        var topology = Monitors.GetTopology();
        var region = area.Intersect(topology.VirtualBounds);
        if (region.Width < 32 || region.Height < 64)
        {
            _app.Notifier.Warn("Area too small", "A scrolling capture needs an area at least 64 pixels high.");
            return;
        }

        var (sourceApp, sourceTitle) = DescribeSource(region);
        var scroll = new ScrollCaptureSession(region, topology.FromPoint(region.Center));
        _scroll = scroll;
        ScrollCaptureResult result;
        try
        {
            scroll.Show();
            _app.Hotkeys.RegisterSessionEscape();
            int limit = Math.Min(_app.Settings.Capture.ScrollMaxHeight, MaxScrollPixels / region.Width);
            result = await scroll.RunAsync(new ScrollCaptureOptions
            {
                MaxHeight = Math.Max(region.Height, limit),
                Trace = line => _log.Debug("Scrolling capture {Step}", line),
            });
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Scrolling capture failed");
            _app.Notifier.Warn("Capture failed", "ShotIt could not finish the scrolling capture. Details are in the log file.");
            return;
        }
        finally
        {
            scroll.Close();
            _scroll = null;
            _app.Hotkeys.UnregisterSessionEscape();
        }

        if (scroll.Cancelled)
        {
            _log.Information("Scrolling capture cancelled");
            return;
        }

        _log.Information("Scrolling capture: {Frames} picture(s), {Width}x{Height}, ended with {Reason} ({Interruption})",
            result.Frames, result.Image.Width, result.Image.Height, result.Reason, scroll.Interruption ?? "not interrupted");
        switch (result.Reason)
        {
            case ScrollStopReason.NothingScrolled:
                _app.Notifier.Warn("Nothing scrolled", "The area did not move, so this is an ordinary capture. Select the part of the window that scrolls. Windows running as administrator cannot be scrolled.");
                break;
            case ScrollStopReason.LostTrack:
                _app.Notifier.Warn("Scrolling capture stopped early", "The content changed in a way ShotIt could not follow, so the picture ends there.");
                break;
            case ScrollStopReason.HeightLimit:
                _app.Notifier.Info("Scrolling capture reached its limit", "The picture was stopped at its longest allowed height. The limit can be raised in Settings › Capture.");
                break;
        }

        try
        {
            _app.Settings.Capture.LastRegion = [region.X, region.Y, region.Width, region.Height];
            _app.SaveSettingsSoon();
            var after = _app.Settings.Capture.AfterRegion;
            var action = after == AfterCaptureAction.ShowToolbar ? OverlayAction.Edit : OverlaySession.Map(after);
            _app.Actions.Run(new CaptureResult(DocState.FromImage(result.Image), sourceApp, sourceTitle, region), action,
                notifyOnCopy: "Scrolling capture finished");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Completing the scrolling capture failed");
            _app.Notifier.Warn("Capture failed", "ShotIt could not finish the scrolling capture. Details are in the log file.");
        }
    }

    // ------------------------------------------------------------------ recording

    /// <summary>Asks for the area to record and starts recording it. While a recording is running, stops it instead.</summary>
    public void StartRecording(RecordingFormat format)
    {
        if (_recording is not null)
        {
            StopRecording();
            return;
        }

        if (IsSessionActive) return;
        StartRegion(OverlayMode.Region, afterRegionOverride: AfterCaptureAction.Copy,
            purpose: format == RecordingFormat.Gif ? OverlayPurpose.RecordGif : OverlayPurpose.RecordVideo);
    }

    /// <summary>Ends the recording in progress and keeps the file.</summary>
    public void StopRecording() => _recording?.Finish();

    /// <summary>Ends the recording in progress and throws the file away.</summary>
    internal void CancelRecording() => _recording?.Cancel();

    /// <summary>Records the given part of the screen until stopped, then saves the file and says where it is.</summary>
    internal async Task StartRecordingAt(PixelRect area, RecordingFormat format)
    {
        if (IsSessionActive) return;
        var topology = Monitors.GetTopology();
        var region = area.Intersect(topology.VirtualBounds);
        if (region.Width < 32 || region.Height < 32)
        {
            _app.Notifier.Warn("Area too small", "A recording needs an area of at least 32 by 32 pixels.");
            return;
        }

        RecordingSession recording;
        RecordingResult result;
        try
        {
            var (sourceApp, sourceTitle) = DescribeSource(region);
            string path = _app.Output.NewRecordingPath(format == RecordingFormat.Gif ? ".gif" : ".mp4", sourceApp, sourceTitle, region.Size);
            recording = new RecordingSession(region, topology.FromPoint(region.Center), _app.Settings.Recording, format, path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
        {
            _log.Warning(ex, "Preparing a recording failed");
            _app.Notifier.Warn("Could not record", $"The recording could not be saved to “{_app.Output.SaveFolder}”. Choose another folder in Settings › Output.");
            return;
        }

        _recording = recording;
        try
        {
            recording.Show();
            _app.Hotkeys.RegisterSessionEscape();
            result = await recording.RunAsync();
        }
        catch (RecordingUnavailableException ex)
        {
            _log.Warning(ex, "Recording is not available");
            _app.Notifier.Warn("Could not record", ex.Message);
            return;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Recording failed");
            _app.Notifier.Warn("Recording failed", "ShotIt could not finish the recording. Details are in the log file.");
            return;
        }
        finally
        {
            recording.Close();
            _recording = null;
            _app.Hotkeys.UnregisterSessionEscape();
        }

        if (recording.Cancelled)
        {
            _log.Information("Recording cancelled");
            return;
        }

        _log.Information("Recording saved: {Format}, {Width}x{Height}, {Seconds:0.0} s, {Frames} frame(s)",
            format, region.Width, region.Height, result.Duration.TotalSeconds, result.Frames);
        string file = System.IO.Path.GetFileName(recording.Path);
        string length = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{(int)result.Duration.TotalMinutes}:{result.Duration.Seconds:00}");
        bool copied = ClipboardService.SetFile(recording.Path);
        string detail = $"{file}  ·  {length}" + (copied ? "  ·  the file is on the clipboard" : string.Empty)
            + (result.ReachedLimit ? "  ·  stopped at the longest recording allowed in Settings › Capture" : string.Empty);
        _app.Notifier.Info("Recording saved", detail, () => CaptureActions.RevealInExplorer(recording.Path));
        if (format == RecordingFormat.Video)
        {
            LastVideo = recording.Path;
            if (_app.Settings.Recording.EditAfterRecording) _ = _app.OpenVideoEditorAsync(recording.Path);
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
        if (_scroll is not null)
        {
            // Esc ends the scrolling and keeps the picture so far; the panel's Cancel button discards it.
            _scroll.Finish();
            return;
        }

        if (_recording is not null)
        {
            _recording.Finish();
            return;
        }

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
        _scroll?.Cancel();
        // A recording is worth keeping whatever interrupted it.
        _recording?.Finish();
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
            session.SelectionCompleted -= OnSelectionCompleted;
            session.End();
        }

        foreach (var window in _windows)
        {
            try
            {
                window.Hide();
                window.Detach();
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

        var purpose = action switch
        {
            OverlayAction.Scroll => OverlayPurpose.Scrolling,
            OverlayAction.RecordVideo => OverlayPurpose.RecordVideo,
            OverlayAction.RecordGif => OverlayPurpose.RecordGif,
            _ => session.Purpose,
        };
        if (purpose != OverlayPurpose.Capture)
        {
            if (session.Selection.Current is not { IsEmpty: false } area) return;
            EndSession();
            // Let the overlay leave the screen before the area is photographed.
            _app.Dispatcher.BeginInvoke(() => _ = purpose switch
            {
                OverlayPurpose.Scrolling => StartScrollingAt(area),
                OverlayPurpose.RecordGif => StartRecordingAt(area, RecordingFormat.Gif),
                _ => StartRecordingAt(area, RecordingFormat.Video),
            }, DispatcherPriority.ApplicationIdle);
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

    private void OnSelectionCompleted()
    {
        var session = _session;
        if (session?.BuildResult() is not { } built) return;
        try
        {
            var result = new CaptureResult(built.Document, built.SourceApp, built.SourceTitle, built.ScreenRegion);
            if (!ClipboardService.SetImage(result.Image))
                _app.Notifier.Warn("Could not copy", "Another program is holding the clipboard. Use the toolbar to try again.");
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Copying the selected region failed");
            _app.Notifier.Warn("Could not copy", "Use the toolbar to try again. Details are in the log file.");
        }
    }

    public void Shutdown()
    {
        CancelSession("application is exiting");
        CloseCountdown();
        DisposeWindows();
    }
}
