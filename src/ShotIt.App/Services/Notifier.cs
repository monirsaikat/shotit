using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ShotIt.Core.Settings;
using ShotIt.Native;

namespace ShotIt.App.Services;

/// <summary>User-facing notifications. Informational ones respect the setting; warnings are always shown.</summary>
public sealed class Notifier
{
    private readonly AppSettings _settings;
    private TrayIcon? _tray;
    private Action? _onClick;

    public Notifier(AppSettings settings) => _settings = settings;

    public void Attach(TrayIcon tray) => _tray = tray;

    /// <summary>The most recent notification, for tests and for the log.</summary>
    public (string Title, string Text)? Last { get; private set; }

    /// <summary>Forgets the last notification, so a test can wait for the next one.</summary>
    public void Reset() => Last = null;

    public void Info(string title, string text, Action? onClick = null)
    {
        Last = (title, text);
        if (_settings.General.PlaySound) WindowInterop.Beep();
        if (!_settings.General.ShowNotifications) return;
        _onClick = onClick;
        _tray?.ShowBalloon(title, text);
    }

    public void Warn(string title, string text, Action? onClick = null)
    {
        Last = (title, text);
        _onClick = onClick;
        _tray?.ShowBalloon(title, text, warning: true);
    }

    /// <summary>Called when the user clicks the notification.</summary>
    public void OnClicked()
    {
        var action = _onClick;
        _onClick = null;
        try { action?.Invoke(); }
        catch (Exception) { /* a failed convenience action is not worth an error dialog */ }
    }
}

/// <summary>A small always-on-top countdown shown before a delayed capture. Click it to cancel.</summary>
public sealed class CountdownWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly TextBlock _number;
    private int _remaining;
    private bool _done;

    public CountdownWindow(int seconds)
    {
        _remaining = Math.Clamp(seconds, 1, 30);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        Width = 112;
        Height = 112;
        ThemeMode = ThemeMode.None;
        ToolTip = "Click to cancel";
        Cursor = Cursors.Hand;

        _number = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 44,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Content = new Border
        {
            Margin = new Thickness(8),
            CornerRadius = new CornerRadius(48),
            Background = new SolidColorBrush(Color.FromArgb(235, 0x1F, 0x21, 0x25)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x7B, 0xFF)),
            BorderThickness = new Thickness(3),
            Child = _number,
        };

        // Top centre of the monitor the cursor is on.
        var monitor = Monitors.GetTopology().FromPoint(Monitors.GetCursorPosition());
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            WindowInterop.MakeToolWindow(handle);
            int size = (int)Math.Round(112 * monitor.Scale);
            WindowInterop.SetBounds(handle, new ShotIt.Core.Geometry.PixelRect(
                monitor.WorkArea.X + (monitor.WorkArea.Width - size) / 2, monitor.WorkArea.Y + (int)(40 * monitor.Scale), size, size), topmost: true);
        };

        MouseDown += (_, _) => Abort();
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            Update();
            _timer.Start();
        };
    }

    public event Action? Finished;
    public event Action? Cancelled;

    private void Update() => _number.Text = _remaining.ToString(CultureInfo.CurrentCulture);

    private void Tick()
    {
        _remaining--;
        if (_remaining > 0)
        {
            Update();
            return;
        }

        if (_done) return;
        _done = true;
        _timer.Stop();
        Close();
        Finished?.Invoke();
    }

    /// <summary>Closes the countdown without capturing.</summary>
    public void Abort()
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        Close();
        Cancelled?.Invoke();
    }
}
