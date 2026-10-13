using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ShotIt.App.Editing;
using ShotIt.App.Themes;
using ShotIt.Core.Display;
using ShotIt.Core.Geometry;
using ShotIt.Native;

namespace ShotIt.App.Services;

/// <summary>
/// A coloured outline just outside an area of the screen that is being scrolled or recorded.
/// Mouse input passes straight through it, and it is left out of every capture.
/// </summary>
internal sealed class AreaOutlineWindow : Window
{
    public AreaOutlineWindow(PixelRect region, MonitorDescriptor monitor, Color color)
    {
        SessionPanelWindow.Prepare(this);
        ThemeMode = ThemeMode.None;
        IsHitTestVisible = false;
        Content = new Border { BorderBrush = new SolidColorBrush(color), BorderThickness = new Thickness(3) };
        int thickness = (int)Math.Ceiling(3 * monitor.Scale);
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            WindowInterop.MakeToolWindow(handle);
            WindowInterop.SetClickThrough(handle, true);
            WindowInterop.ExcludeFromCapture(handle);
            WindowInterop.SetBounds(handle, region.Inflate(thickness), topmost: true);
        };
    }
}

/// <summary>
/// The small panel shown beside an area that is being scrolled or recorded: a line of status with
/// buttons to finish or cancel. It never takes the keyboard focus and is left out of every capture.
/// </summary>
internal sealed class SessionPanelWindow : Window
{
    private const int ShadowMargin = 12;
    private readonly TextBlock _status;

    /// <param name="avoid">A point the panel must not cover when it has to sit inside the area.</param>
    public SessionPanelWindow(PixelRect region, MonitorDescriptor monitor, PixelPoint? avoid, string status,
        string finishTip, string cancelTip, Action finish, Action cancel)
    {
        Prepare(this);
        SizeToContent = SizeToContent.WidthAndHeight;

        _status = new TextBlock
        {
            Text = status,
            Foreground = Brushes.White,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0),
            MinWidth = 150,
        };
        var done = ToolPalette.MakeButton(Icons.Create(Icons.Check), finishTip);
        done.Click += (_, _) => finish();
        var close = ToolPalette.MakeButton(Icons.Create(Icons.Close), cancelTip);
        close.Click += (_, _) => cancel();

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_status);
        row.Children.Add(ToolPalette.Separator());
        row.Children.Add(done);
        row.Children.Add(close);
        Content = new Border { Style = (Style)Application.Current.FindResource("BarPanel"), Margin = new Thickness(ShadowMargin), Child = row };

        bool hidden = false;
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            WindowInterop.MakeToolWindow(handle);
            WindowInterop.MakeNoActivate(handle);
            hidden = WindowInterop.ExcludeFromCapture(handle);
        };
        Loaded += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            var size = new PixelSize((int)Math.Ceiling(ActualWidth * monitor.Scale), (int)Math.Ceiling(ActualHeight * monitor.Scale));
            WindowInterop.MoveTo(handle, Place(region, monitor, avoid, size, hidden));
            WindowInterop.BringToTopmost(handle);
        };
    }

    internal static void Prepare(Window window)
    {
        window.WindowStyle = WindowStyle.None;
        window.AllowsTransparency = true;
        window.Background = Brushes.Transparent;
        window.ShowInTaskbar = false;
        window.ShowActivated = false;
        window.Topmost = true;
        window.ResizeMode = ResizeMode.NoResize;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
    }

    /// <summary>Below the area when there is room, else above it, else in a corner of it.</summary>
    private static PixelPoint Place(PixelRect region, MonitorDescriptor monitor, PixelPoint? avoid, PixelSize size, bool hidden)
    {
        var work = monitor.WorkArea;
        int x = Math.Clamp(region.X + (region.Width - size.Width) / 2, work.X, Math.Max(work.X, work.Right - size.Width));
        if (region.Bottom + size.Height <= work.Bottom) return new PixelPoint(x, region.Bottom);
        if (region.Y - size.Height >= work.Y) return new PixelPoint(x, region.Y - size.Height);

        // Inside the area the panel is left out of the picture; where that is not possible, the corner hides the least.
        int inside = Math.Max(work.X, Math.Min(region.Right, work.Right) - size.Width);
        int y = Math.Max(region.Y, work.Y);
        if (!hidden || avoid is not { } point || !new PixelRect(inside, y, size.Width, size.Height).Contains(point)) return new PixelPoint(inside, y);
        return new PixelPoint(Math.Max(region.X, work.X), y);
    }

    public void SetStatus(string text) => _status.Text = text;
}
