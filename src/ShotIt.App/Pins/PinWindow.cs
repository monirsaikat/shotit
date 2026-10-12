using ShotIt.Core.Settings;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ShotIt.App.Infrastructure;
using ShotIt.App.Services;
using ShotIt.Core.Editing;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using ShotIt.Native;
using ShotIt.Rendering;

namespace ShotIt.App.Pins;

/// <summary>
/// A screenshot floating above all other windows. Drag to move, wheel to zoom, Ctrl+wheel for opacity,
/// double-click or Esc to close, right-click for more.
/// </summary>
public sealed class PinWindow : Window
{
    private const double Frame = 8; // room around the image for the shadow, in DIPs

    private readonly PinManager _manager;
    private readonly PixelBuffer _image;
    private readonly Image _picture;
    private readonly Border _border;
    private nint _handle;
    private double _zoom = 1;
    private bool _clickThrough;

    public PinWindow(PinManager manager, PixelBuffer image, PixelPoint? topLeftPx, double zoom = 1)
    {
        _manager = manager;
        _image = image;
        _zoom = Math.Clamp(zoom, 0.1, 8);
        Title = "ShotIt pin";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ThemeMode = ThemeMode.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        Cursor = Cursors.SizeAll;

        _picture = new Image { Source = image.ToBitmapSource(), Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_picture, BitmapScalingMode.HighQuality);
        _border = new Border
        {
            Margin = new Thickness(Frame),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xCC, 0x3D, 0x7B, 0xFF)),
            Background = Brushes.White,
            Child = _picture,
            Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 2, Direction = 270, Opacity = 0.4 },
        };
        Content = _border;

        var monitor = Monitors.GetTopology().FromPoint(topLeftPx ?? Monitors.GetCursorPosition());
        ApplyZoom(monitor.Dpi);

        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            WindowInterop.MakeToolWindow(_handle);
            int inset = (int)Math.Round((Frame + 1) * monitor.Scale);
            var target = topLeftPx is { } p
                ? new PixelPoint(p.X - inset, p.Y - inset)
                : new PixelPoint(monitor.WorkArea.X + (monitor.WorkArea.Width - image.Width) / 2 - inset, monitor.WorkArea.Y + (monitor.WorkArea.Height - image.Height) / 2 - inset);
            WindowInterop.MoveTo(_handle, target);
        };
        DpiChanged += (_, e) => ApplyZoom((int)Math.Round(e.NewDpi.PixelsPerInchX));

        MouseLeftButtonDown += OnLeftDown;
        MouseWheel += OnWheel;
        KeyDown += OnKeyDown;
        ContextMenuOpening += (_, _) => ContextMenu = BuildMenu();
        ContextMenu = new ContextMenu();
        Closed += (_, _) => _manager.Remove(this);
    }

    public PixelBuffer PinnedImage => _image;

    public double Zoom => _zoom;

    /// <summary>Where the top-left pixel of the image sits on the virtual screen, or null before the window exists.</summary>
    public PixelPoint? ImagePosition
    {
        get
        {
            if (_handle == 0) return null;
            var bounds = WindowList.GetBounds(_handle);
            int inset = (int)Math.Round((Frame + 1) * WindowInterop.GetDpi(_handle) / 96.0);
            return new PixelPoint(bounds.X + inset, bounds.Y + inset);
        }
    }

    /// <summary>At zoom 1 the pin shows one image pixel per device pixel, exactly as it was on screen.</summary>
    private void ApplyZoom(int dpi)
    {
        _picture.Width = Math.Max(8, _image.Width * 96.0 / dpi * _zoom);
        _picture.Height = Math.Max(8, _image.Height * 96.0 / dpi * _zoom);
        RenderOptions.SetBitmapScalingMode(_picture, Math.Abs(_zoom - 1) < 0.001 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    private int CurrentDpi => _handle != 0 ? WindowInterop.GetDpi(_handle) : 96;

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.1, 8);
        ApplyZoom(CurrentDpi);
    }

    private void OnLeftDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            Close();
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was released before the drag could start.
        }
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? 0.1 : -0.1), 0.2, 1.0);
        else
            SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.C when ctrl:
                ClipboardService.SetImage(_image);
                break;
            case Key.S when ctrl:
                _manager.SaveAs(this);
                break;
            case Key.Add or Key.OemPlus:
                SetZoom(_zoom * 1.1);
                break;
            case Key.Subtract or Key.OemMinus:
                SetZoom(_zoom / 1.1);
                break;
            case Key.D0 or Key.NumPad0:
                SetZoom(1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        MenuItem Add(string header, Action action, string? gesture = null, bool isChecked = false)
        {
            var item = new MenuItem { Header = header, InputGestureText = gesture ?? string.Empty, IsChecked = isChecked };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
            return item;
        }

        Add("Copy", () => ClipboardService.SetImage(_image), "Ctrl+C");
        Add("Save as…", () => _manager.SaveAs(this), "Ctrl+S");
        Add("Open in editor", () => _manager.Edit(this));
        menu.Items.Add(new Separator());
        Add("Actual size", () => SetZoom(1), "0");
        Add("Zoom in", () => SetZoom(_zoom * 1.25), "+");
        Add("Zoom out", () => SetZoom(_zoom / 1.25), "−");
        var opacity = new MenuItem { Header = "Opacity" };
        foreach (int percent in new[] { 100, 80, 60, 40, 20 })
        {
            var item = new MenuItem { Header = percent + " %", IsChecked = Math.Abs(Opacity * 100 - percent) < 5 };
            item.Click += (_, _) => Opacity = percent / 100.0;
            opacity.Items.Add(item);
        }

        menu.Items.Add(opacity);
        Add("Click-through (restore from the tray menu)", () => SetClickThrough(true));
        menu.Items.Add(new Separator());
        Add("Close", Close, "Esc");
        Add("Close all pins", _manager.CloseAll);
        return menu;
    }

    public void SetClickThrough(bool enabled)
    {
        if (_handle == 0 || _clickThrough == enabled) return;
        _clickThrough = enabled;
        WindowInterop.SetClickThrough(_handle, enabled);
        _border.BorderBrush = new SolidColorBrush(enabled ? Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xCC, 0x3D, 0x7B, 0xFF));
    }

    /// <summary>Moves the pin back onto a monitor if a display change left it stranded off-screen.</summary>
    public void EnsureOnScreen()
    {
        if (_handle == 0) return;
        var bounds = WindowList.GetBounds(_handle);
        var topology = Monitors.GetTopology();
        if (topology.Monitors.Any(m => m.WorkArea.IntersectsWith(bounds))) return;
        var work = topology.Primary.WorkArea;
        WindowInterop.MoveTo(_handle, new PixelPoint(work.X + 40, work.Y + 40));
    }
}

/// <summary>Keeps track of every pinned image.</summary>
public sealed class PinManager
{
    private readonly AppServices _app;
    private readonly List<PinWindow> _pins = [];
    private bool _hidden;

    public PinManager(AppServices app) => _app = app;

    public int Count => _pins.Count;

    public IReadOnlyList<PinWindow> All => _pins;

    public PinWindow Pin(PixelBuffer image, PixelPoint? topLeftPx = null, double zoom = 1)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (_hidden) ToggleAll();
        var window = new PinWindow(this, image, topLeftPx, zoom);
        _pins.Add(window);
        window.Show();
        return window;
    }

    internal void Remove(PinWindow window) => _pins.Remove(window);

    /// <summary>Hides or shows all pins (global hotkey).</summary>
    public void ToggleAll()
    {
        _hidden = !_hidden;
        foreach (var pin in _pins.ToArray())
        {
            if (_hidden) pin.Hide();
            else pin.Show();
        }
    }

    public void RestoreInteraction()
    {
        foreach (var pin in _pins) pin.SetClickThrough(false);
    }

    public void CloseAll()
    {
        foreach (var pin in _pins.ToArray()) pin.Close();
        _pins.Clear();
        _hidden = false;
    }

    public void OnDisplayChanged()
    {
        foreach (var pin in _pins) pin.EnsureOnScreen();
    }

    // ------------------------------------------------------------------ remembering pins between runs

    private const int MaxRememberedPins = 40;

    private sealed record SavedPin(string File, int X, int Y, double Zoom);

    /// <summary>
    /// Writes the open pins to disk so <see cref="RestoreSession"/> can bring them back, or removes
    /// what was stored when the user has the option off. Never throws: this runs while shutting down.
    /// </summary>
    public int SaveSession()
    {
        string folder = _app.Paths.PinsDirectory;
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            if (!_app.Settings.General.RestorePins || _pins.Count == 0) return 0;

            Directory.CreateDirectory(folder);
            var saved = new List<SavedPin>();
            foreach (var pin in _pins.Take(MaxRememberedPins))
            {
                if (pin.ImagePosition is not { } position) continue;
                string file = saved.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".png";
                File.WriteAllBytes(Path.Combine(folder, file), ImageCodec.Encode(pin.PinnedImage, ImageFormat.Png));
                saved.Add(new SavedPin(file, position.X, position.Y, pin.Zoom));
            }

            File.WriteAllText(Path.Combine(folder, "pins.json"), System.Text.Json.JsonSerializer.Serialize(saved));
            return saved.Count;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _app.Log.Warning(ex, "Pinned images could not be stored for the next start");
            return 0;
        }
    }

    /// <summary>Brings back the pins stored by <see cref="SaveSession"/>. Unreadable entries are skipped.</summary>
    public int RestoreSession()
    {
        string folder = _app.Paths.PinsDirectory;
        string index = Path.Combine(folder, "pins.json");
        if (!_app.Settings.General.RestorePins || !File.Exists(index)) return 0;
        int restored = 0;
        try
        {
            if (new FileInfo(index).Length > 64 * 1024) return 0;
            var saved = System.Text.Json.JsonSerializer.Deserialize<List<SavedPin>>(File.ReadAllText(index)) ?? [];
            foreach (var entry in saved.Take(MaxRememberedPins))
            {
                // Only plain file names inside the pins folder are accepted.
                if (entry is null || string.IsNullOrEmpty(entry.File) || Path.GetFileName(entry.File) != entry.File) continue;
                string path = Path.Combine(folder, entry.File);
                if (!File.Exists(path)) continue;
                try
                {
                    var pin = Pin(ImageCodec.DecodeFile(path), new PixelPoint(entry.X, entry.Y), double.IsFinite(entry.Zoom) ? entry.Zoom : 1);
                    pin.EnsureOnScreen();
                    restored++;
                }
                catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException or ArgumentException)
                {
                    _app.Log.Information("A remembered pin could not be restored: {Message}", ex.Message);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or NotSupportedException)
        {
            _app.Log.Warning(ex, "Remembered pins could not be read");
        }

        return restored;
    }

    internal void SaveAs(PinWindow pin)
    {
        var result = new CaptureResult(DocState.FromImage(pin.PinnedImage));
        string? path = _app.Output.SaveAs(result, pin, out string? error);
        if (error is not null) _app.Notifier.Warn("Could not save", error);
        else if (path is not null) _app.SaveSettingsSoon();
    }

    internal void Edit(PinWindow pin) => _app.OpenEditor(new CaptureResult(DocState.FromImage(pin.PinnedImage)));
}
