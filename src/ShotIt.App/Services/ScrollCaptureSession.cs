using System.Globalization;
using System.Windows.Media;
using ShotIt.Capture;
using ShotIt.Core.Display;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using ShotIt.Native;

namespace ShotIt.App.Services;

/// <summary>
/// One scrolling capture in progress: an outline around the area, a small panel to finish or cancel,
/// and the mouse wheel turned on the user's behalf. Moving the mouse hands control back and finishes.
/// </summary>
internal sealed class ScrollCaptureSession : IScrollSource
{
    private readonly PixelRect _region;
    private readonly PixelPoint _park;
    private readonly CancellationTokenSource _stop = new();
    private readonly AreaOutlineWindow _outline;
    private readonly SessionPanelWindow _panel;
    private PixelPoint? _original;
    private bool _userTookOver;
    private bool _closed;

    public ScrollCaptureSession(PixelRect region, MonitorDescriptor monitor)
    {
        _region = region;
        // The pointer rests low in the area. Each picture overwrites the lower part of the one before,
        // so whatever lights up under the pointer is kept only once, in the last picture.
        _park = new PixelPoint(region.X + region.Width / 2, region.Y + region.Height * 4 / 5);
        _outline = new AreaOutlineWindow(region, monitor, Color.FromRgb(0x3D, 0x7B, 0xFF));
        _panel = new SessionPanelWindow(region, monitor, _park, "Scrolling…",
            "Finish and keep the picture (Esc, or move the mouse)", "Cancel without keeping anything", Finish, Cancel);
    }

    /// <summary>The user asked for the capture to be thrown away.</summary>
    public bool Cancelled { get; private set; }

    /// <summary>Why scrolling was handed back before the end of the page, for the log.</summary>
    public string? Interruption { get; private set; }

    public void Show()
    {
        _outline.Show();
        _panel.Show();
    }

    /// <summary>Stops scrolling and keeps the picture so far.</summary>
    public void Finish()
    {
        if (!_closed) _stop.Cancel();
    }

    public void Cancel()
    {
        Cancelled = true;
        Finish();
    }

    public async Task<ScrollCaptureResult> RunAsync(ScrollCaptureOptions options)
    {
        var progress = new Progress<int>(height => _panel.SetStatus(string.Create(CultureInfo.CurrentCulture, $"Scrolling…  {height:N0} px")));
        try
        {
            return await Task.Run(() => ScrollingCapture.RunAsync(this, options, progress, _stop.Token));
        }
        finally
        {
            if (!_userTookOver && _original is { } original) MouseInput.MoveTo(original);
            Close();
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

    PixelBuffer IScrollSource.Grab() => GdiScreen.Capture(_region);

    bool IScrollSource.Scroll(int notches)
    {
        var now = Monitors.GetCursorPosition();
        if (_original is null)
        {
            _original = now;
            // Give the window under the pointer a moment to notice the pointer before the wheel turns.
            MouseInput.MoveTo(_park);
            Thread.Sleep(80);
        }
        else if (Math.Abs(now.X - _park.X) > 3 || Math.Abs(now.Y - _park.Y) > 3)
        {
            // The user moved the mouse: stop steering it and finish with what there is.
            _userTookOver = true;
            Interruption = $"the mouse moved from {_park} to {now}";
            return false;
        }

        // The wheel goes to the window under the pointer.
        if (!MouseInput.MoveTo(_park) || !MouseInput.WheelDown(notches))
        {
            Interruption = "Windows refused the mouse input";
            return false;
        }

        return true;
    }
}
