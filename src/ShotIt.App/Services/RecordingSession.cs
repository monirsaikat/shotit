using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShotIt.Capture;
using ShotIt.Core.Display;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using ShotIt.Core.Settings;
using ShotIt.Native;
using ShotIt.Rendering;

namespace ShotIt.App.Services;

/// <summary>
/// One screen recording in progress: an outline around the area, a panel with the elapsed time and
/// buttons to finish or cancel, and the marks drawn on every frame (pointer, clicks, shortcuts).
/// </summary>
internal sealed class RecordingSession
{
    private static readonly TimeSpan ClickFade = TimeSpan.FromMilliseconds(350);
    private static readonly TimeSpan CaptionHold = TimeSpan.FromMilliseconds(1200);
    private static readonly ColorRgba LeftColor = new(0xFF, 0xD0, 0x00);
    private static readonly ColorRgba RightColor = new(0x3D, 0x9B, 0xFF);

    private readonly PixelRect _region;
    private readonly double _scale;
    private readonly RecordingSettings _settings;
    private readonly RecordingFormat _format;
    private readonly CancellationTokenSource _stop = new();
    private readonly AreaOutlineWindow _outline;
    private readonly SessionPanelWindow _panel;
    private readonly Dictionary<string, PixelBuffer> _captions = [];
    private readonly List<(PixelPoint At, TimeSpan Since, ColorRgba Color)> _released = [];
    private bool _leftWasDown, _rightWasDown;
    private string? _caption;
    private TimeSpan _captionUntil;
    private bool _closed;

    public RecordingSession(PixelRect region, MonitorDescriptor monitor, RecordingSettings settings, RecordingFormat format, string path)
    {
        _region = region;
        _scale = monitor.Scale;
        _settings = settings;
        _format = format;
        Path = path;
        _outline = new AreaOutlineWindow(region, monitor, Color.FromRgb(0xE8, 0x1C, 0x2E));
        _panel = new SessionPanelWindow(region, monitor, avoid: null, "Recording  0:00",
            "Stop and save the recording (Esc)", "Cancel without saving", Finish, Cancel);
    }

    public string Path { get; }

    /// <summary>The user asked for the recording to be thrown away.</summary>
    public bool Cancelled { get; private set; }

    public void Show()
    {
        _outline.Show();
        _panel.Show();
    }

    /// <summary>Stops recording and keeps the file.</summary>
    public void Finish()
    {
        if (!_closed) _stop.Cancel();
    }

    public void Cancel()
    {
        Cancelled = true;
        Finish();
    }

    public async Task<RecordingResult> RunAsync()
    {
        int rate = _format == RecordingFormat.Gif ? _settings.GifFramesPerSecond : _settings.VideoFramesPerSecond;
        var options = new RecordingOptions { FramesPerSecond = rate, MaxDuration = TimeSpan.FromSeconds(_settings.MaxSeconds) };
        var progress = new Progress<TimeSpan>(time => _panel.SetStatus(string.Create(CultureInfo.InvariantCulture, $"Recording  {(int)time.TotalMinutes}:{time.Seconds:00}")));
        IVideoSink? sink = null;
        bool complete = false;
        try
        {
            sink = _format == RecordingFormat.Gif ? new GifSink(Path, _region.Size) : new Mp4Sink(Path, _region.Size, rate);
            var result = await ScreenRecorder.RunAsync(() => GdiScreen.Capture(_region), sink, options, Decorate, progress, _stop.Token);
            complete = !Cancelled;
            return result;
        }
        finally
        {
            sink?.Dispose();
            Close();
            if (!complete)
            {
                try { File.Delete(Path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        _outline.Close();
        _panel.Close();
        _stop.Dispose();
    }

    // ------------------------------------------------------------------ marks on each frame

    /// <summary>Draws the pointer, click marks and shortcut caption onto one frame. Runs on the recording thread.</summary>
    private void Decorate(PixelBuffer frame, TimeSpan time)
    {
        if (_settings.ShowClicks) DrawClicks(frame, time);
        if (_settings.ShowPointer && CursorCapture.Capture() is { } cursor) CursorCapture.Composite(frame, _region.Location, cursor);
        if (_settings.ShowKeys) DrawCaption(frame, time);
    }

    private void DrawClicks(PixelBuffer frame, TimeSpan time)
    {
        var pointer = Monitors.GetCursorPosition();
        bool left = InputState.IsKeyDown(InputState.LeftButton), right = InputState.IsKeyDown(InputState.RightButton);
        if (_leftWasDown && !left) _released.Add((pointer, time, LeftColor));
        if (_rightWasDown && !right) _released.Add((pointer, time, RightColor));
        _leftWasDown = left;
        _rightWasDown = right;

        double radius = 14 * _scale, thickness = 3 * _scale;
        int x = pointer.X - _region.X, y = pointer.Y - _region.Y;
        if (left) RecordingMarks.DrawRing(frame, x, y, radius, thickness, LeftColor, 0.9);
        if (right) RecordingMarks.DrawRing(frame, x, y, radius, thickness, RightColor, 0.9);

        // A released click leaves a ring that widens and fades, so even a quick click is seen.
        _released.RemoveAll(r => time - r.Since >= ClickFade);
        foreach (var (at, since, color) in _released)
        {
            double progress = (time - since) / ClickFade;
            RecordingMarks.DrawRing(frame, at.X - _region.X, at.Y - _region.Y, radius * (1 + progress), thickness, color, 0.9 * (1 - progress));
        }
    }

    private void DrawCaption(PixelBuffer frame, TimeSpan time)
    {
        if (KeyCaption.Describe(InputState.IsKeyDown) is { } pressed)
        {
            _caption = pressed;
            _captionUntil = time + CaptionHold;
        }

        if (_caption is null || time >= _captionUntil) return;
        if (!_captions.TryGetValue(_caption, out var label))
        {
            string text = _caption;
            // WPF draws the text, and WPF objects belong to the UI thread.
            label = Application.Current.Dispatcher.Invoke(() => RenderCaption(text, _scale));
            _captions[text] = label;
        }

        RecordingMarks.Overlay(frame, label, (frame.Width - label.Width) / 2, frame.Height - label.Height - (int)(16 * _scale));
    }

    /// <summary>A rounded dark label with white text, as a picture with transparency.</summary>
    internal static PixelBuffer RenderCaption(string text, double scale)
    {
        var formatted = TextLayout.Create(text, "Segoe UI", 20 * scale, bold: true, italic: false, Brushes.White);
        double padX = 16 * scale, padY = 8 * scale;
        int width = (int)Math.Ceiling(formatted.Width + padX * 2), height = (int)Math.Ceiling(formatted.Height + padY * 2);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(0xD8, 0x1F, 0x21, 0x25)), null, new Rect(0, 0, width, height), 8 * scale, 8 * scale);
            dc.DrawText(formatted, new Point(padX, padY));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        return bitmap.ToPixelBuffer();
    }
}
