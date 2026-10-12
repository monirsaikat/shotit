using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ShotIt.App.Editing;
using ShotIt.Core.Display;
using ShotIt.Core.Editing;
using ShotIt.Core.Geometry;
using ShotIt.Core.Selection;
using ShotIt.Native;
using ShotIt.Rendering;

namespace ShotIt.App.Overlay;

/// <summary>
/// The full-screen window covering one monitor during a capture session. It shows the frozen screen
/// at exactly one image pixel per device pixel, forwards input to the shared session and hosts the toolbars.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly Grid _root = new() { Background = Brushes.Transparent, Focusable = true, FocusVisualStyle = null };
    private readonly EditorSurface _surface = new();
    private readonly OverlayLayer _layer = new();
    private readonly Canvas _chrome = new();
    private readonly MagnifierView _magnifier = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _bars = new() { Visibility = Visibility.Collapsed };
    private readonly TextEditHost _textHost;
    private OverlaySession? _session;
    private ToolPalette? _palette;
    private nint _handle;
    private bool _hadInput;

    public OverlayWindow(MonitorDescriptor monitor)
    {
        Monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        Title = "ShotIt capture";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        ThemeMode = ThemeMode.None;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        Cursor = Cursors.Cross;
        RenderOptions.SetBitmapScalingMode(_surface, BitmapScalingMode.NearestNeighbor);

        _root.Children.Add(_surface);
        _root.Children.Add(_layer);
        _root.Children.Add(_chrome);
        _chrome.Children.Add(_magnifier);
        _chrome.Children.Add(_bars);
        Content = _root;

        _textHost = new TextEditHost(_chrome, _surface, OwnsDocPoint);
        _textHost.EditingChanged += OnTextEditingChanged;

        _root.MouseDown += OnMouseDown;
        _root.MouseMove += OnMouseMove;
        _root.MouseUp += OnMouseUp;
        _root.MouseWheel += (_, e) => _session?.Wheel(e.Delta);
        _root.LostMouseCapture += (_, _) => _session?.CaptureLost();
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => FocusLost?.Invoke();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(ApplyBounds);

        // Creating the native window up front keeps the hotkey-to-visible path short.
        _handle = new WindowInteropHelper(this).EnsureHandle();
        WindowInterop.MakeToolWindow(_handle);
        WindowInterop.SetBounds(_handle, monitor.Bounds, topmost: true);
    }

    public MonitorDescriptor Monitor { get; private set; }

    public nint Handle => _handle;

    /// <summary>Raised when the window stops being the active window; the controller decides whether to abort.</summary>
    public event Action? FocusLost;

    private bool OwnsDocPoint(Vec2 doc)
    {
        if (_session is null) return false;
        var origin = _session.Frame.VirtualBounds.Location;
        var px = new PixelPoint((int)Math.Floor(doc.X) + origin.X, (int)Math.Floor(doc.Y) + origin.Y);
        return _session.Topology.FromPoint(px) == Monitor;
    }

    private void OnTextEditingChanged()
    {
        if (_session is not null) _session.IsTextEditing = _textHost.IsEditing;
        if (!_textHost.IsEditing) _root.Focus();
    }

    /// <summary>Cancels an in-progress text edit. Returns true when there was one.</summary>
    public bool CancelTextEdit()
    {
        if (!_textHost.IsEditing) return false;
        _textHost.Close(commit: false);
        return true;
    }

    public void CommitTextEdit() => _textHost.Close(commit: true);

    // ------------------------------------------------------------------ session lifecycle

    public void Attach(OverlaySession session, MonitorDescriptor monitor, bool showHint, Action<ToolOptions> onOptionsChanged)
    {
        Detach();
        _session = session;
        Monitor = monitor;
        _hadInput = false;

        var origin = session.Frame.VirtualBounds.Location;
        _surface.Zoom = 96.0 / monitor.Dpi;
        _surface.Origin = new Vec2(monitor.Bounds.X - origin.X, monitor.Bounds.Y - origin.Y);
        _surface.DrawBase = true;
        _surface.Session = session.Editor;
        _layer.ShowHint = showHint;
        _layer.Configure(session, monitor);
        _magnifier.Configure(session);

        _palette = new ToolPalette(session.Editor) { HorizontalAlignment = HorizontalAlignment.Right };
        _palette.OptionsChanged += onOptionsChanged;
        var actions = new ActionBar(session.RequestAction) { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        _bars.Children.Clear();
        _bars.Children.Add(_palette);
        _bars.Children.Add(actions);

        _textHost.Attach(session.Editor);
        session.Editor.ColorPickRequested += OnColorPick;
        session.Changed += OnSessionChanged;
        OnSessionChanged();
    }

    public void Detach()
    {
        if (_session is null) return;
        _session.Changed -= OnSessionChanged;
        _session.Editor.ColorPickRequested -= OnColorPick;
        _textHost.Detach(_session.Editor);
        _palette?.Detach();
        _palette = null;
        _bars.Children.Clear();
        _bars.Visibility = Visibility.Collapsed;
        _magnifier.Visibility = Visibility.Collapsed;
        _magnifier.Configure(null);
        _layer.Configure(null, null);
        _surface.Session = null;
        _session = null;
        if (_root.IsMouseCaptured) _root.ReleaseMouseCapture();
    }

    public void ShowOverlay(bool activate)
    {
        ApplyBounds();
        if (activate)
        {
            Show();
            Activate();
            WindowInterop.ForceForeground(_handle);
            _root.Focus();
            Keyboard.Focus(_root);
        }
        else
        {
            WindowInterop.ShowNoActivate(_handle);
            Show();
        }

        ApplyBounds();
    }

    public void TakeFocus()
    {
        WindowInterop.ForceForeground(_handle);
        if (!_textHost.IsEditing)
        {
            _root.Focus();
            Keyboard.Focus(_root);
        }
    }

    private void ApplyBounds() => WindowInterop.SetBounds(_handle, Monitor.Bounds, topmost: true);

    // ------------------------------------------------------------------ painting

    private void OnSessionChanged()
    {
        if (_session is null) return;
        var session = _session;
        _surface.ClipArea = session.Editor.ClipArea;
        _surface.InvalidateVisual();
        _layer.InvalidateVisual();
        UpdateMagnifier(session);
        UpdateBars(session);
    }

    private Point ToLocal(PixelPoint p) =>
        new(DpiMath.PxToDip(p.X - Monitor.Bounds.X, Monitor.Dpi), DpiMath.PxToDip(p.Y - Monitor.Bounds.Y, Monitor.Dpi));

    private void UpdateMagnifier(OverlaySession session)
    {
        bool show = session.ShowMagnifier && Monitor.Bounds.Contains(session.CursorPx)
            && (session.Phase == OverlayPhase.Selecting || session.IsShapingRegion);
        if (!show)
        {
            _magnifier.Visibility = Visibility.Collapsed;
            return;
        }

        var cursor = ToLocal(session.CursorPx);
        double width = ActualWidth > 0 ? ActualWidth : DpiMath.PxToDip(Monitor.Bounds.Width, Monitor.Dpi);
        double height = ActualHeight > 0 ? ActualHeight : DpiMath.PxToDip(Monitor.Bounds.Height, Monitor.Dpi);
        const double gap = 22;
        // Keep the loupe on screen by flipping it to the other side of the cursor near an edge.
        double x = cursor.X + gap + MagnifierView.BoxSize > width ? cursor.X - gap - MagnifierView.BoxSize : cursor.X + gap;
        double y = cursor.Y + gap + MagnifierView.TotalHeight > height ? cursor.Y - gap - MagnifierView.TotalHeight : cursor.Y + gap;
        Canvas.SetLeft(_magnifier, Math.Max(0, x));
        Canvas.SetTop(_magnifier, Math.Max(0, y));
        _magnifier.Visibility = Visibility.Visible;
        _magnifier.InvalidateVisual();
    }

    private void UpdateBars(OverlaySession session)
    {
        bool show = session.Phase == OverlayPhase.Adjusting && session.Selection.Current is { IsEmpty: false } && !session.IsShapingRegion;
        if (show)
        {
            // The toolbars appear on the monitor that holds most of the region.
            var region = session.Selection.Current!.Value;
            show = session.Topology.FromRect(region) == Monitor;
        }

        if (!show)
        {
            _bars.Visibility = Visibility.Collapsed;
            return;
        }

        var selection = session.Selection.Current!.Value;
        _bars.Visibility = Visibility.Visible;
        _bars.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _bars.DesiredSize;
        var local = new RectD(
            DpiMath.PxToDip(selection.X - Monitor.Bounds.X, Monitor.Dpi), DpiMath.PxToDip(selection.Y - Monitor.Bounds.Y, Monitor.Dpi),
            DpiMath.PxToDip(selection.Width, Monitor.Dpi), DpiMath.PxToDip(selection.Height, Monitor.Dpi));
        var area = new RectD(0, 0, DpiMath.PxToDip(Monitor.Bounds.Width, Monitor.Dpi), DpiMath.PxToDip(Monitor.Bounds.Height, Monitor.Dpi));
        var position = ToolbarPlacement.Compute(local, size.Width, size.Height, area);
        Canvas.SetLeft(_bars, Math.Round(position.X));
        Canvas.SetTop(_bars, Math.Round(position.Y));
    }

    private void OnColorPick(object? sender, Vec2 doc)
    {
        if (_session is null || _palette is null || !OwnsDocPoint(doc)) return;
        var image = _session.Frame.Image;
        int x = (int)Math.Floor(doc.X), y = (int)Math.Floor(doc.Y);
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return;
        _palette.SetColor(image.GetPixel(x, y).WithAlpha(255));
        _session.Editor.SetTool(ToolKind.Select);
    }

    // ------------------------------------------------------------------ input

    private PixelPoint ToScreenPx(MouseEventArgs e)
    {
        // Mouse positions arrive as DIPs relative to the element; convert back to the exact device pixel.
        var p = e.GetPosition(_root);
        return new PixelPoint(
            Monitor.Bounds.X + (int)Math.Floor(p.X * Monitor.Dpi / 96.0 + 0.001),
            Monitor.Bounds.Y + (int)Math.Floor(p.Y * Monitor.Dpi / 96.0 + 0.001));
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_session is null) return;
        _hadInput = true;
        _layer.ShowHint = false;
        if (_textHost.IsEditing)
        {
            // A click outside the text box finishes the text; it must not also start a drag.
            _textHost.Close(commit: true);
            e.Handled = true;
            return;
        }

        _root.Focus();
        if (e.ChangedButton == MouseButton.Left) _root.CaptureMouse();
        _session.MouseDown(ToScreenPx(e), e.ChangedButton, e.ClickCount);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_session is null) return;
        var px = ToScreenPx(e);
        _session.MouseMove(px);
        UpdateCursor(px, e.GetPosition(_surface));
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_session is null) return;
        var session = _session;
        if (e.ChangedButton == MouseButton.Left && _root.IsMouseCaptured) _root.ReleaseMouseCapture();
        session.MouseUp(ToScreenPx(e), e.ChangedButton);
        e.Handled = true;
    }

    private void UpdateCursor(PixelPoint px, Point surfacePoint)
    {
        if (_session is null) return;
        var session = _session;
        if (session.Phase == OverlayPhase.Selecting || session.IsDragging)
        {
            if (!session.IsDragging) _root.Cursor = Cursors.Cross;
            return;
        }

        var hit = session.LassoClosed ? SelectionHit.None : session.Selection.HitTest(px, (int)Math.Round(6 * Monitor.Scale));
        _root.Cursor = hit switch
        {
            SelectionHit.TopLeft or SelectionHit.BottomRight => Cursors.SizeNWSE,
            SelectionHit.TopRight or SelectionHit.BottomLeft => Cursors.SizeNESW,
            SelectionHit.Top or SelectionHit.Bottom => Cursors.SizeNS,
            SelectionHit.Left or SelectionHit.Right => Cursors.SizeWE,
            SelectionHit.Inside => _surface.CursorAt(surfacePoint) ?? Cursors.SizeAll,
            _ => session.Selection.Current is { } r && r.Contains(px) ? _surface.CursorAt(surfacePoint) ?? Cursors.Arrow : Cursors.Cross,
        };
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_session is null || _textHost.IsEditing) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            // Normally Esc arrives through the session hotkey; this path covers the case where
            // registering that hotkey failed.
            _session.Escape();
            e.Handled = true;
            return;
        }

        if (_session.KeyDown(key, Keyboard.Modifiers))
        {
            if (!_hadInput)
            {
                _hadInput = true;
                _layer.ShowHint = false;
            }

            e.Handled = true;
        }
    }
}
