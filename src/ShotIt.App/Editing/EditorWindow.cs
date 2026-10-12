using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ShotIt.App.Infrastructure;
using ShotIt.App.Overlay;
using ShotIt.App.Services;
using ShotIt.App.Themes;
using ShotIt.Core.Annotations;
using ShotIt.Core.Editing;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using ShotIt.Core.Settings;
using ShotIt.Native;
using ShotIt.Rendering;

namespace ShotIt.App.Editing;

/// <summary>The full editor: every annotation tool plus crop, rotate, resize, adjustments and the presentation frame.</summary>
public sealed class EditorWindow : Window
{
    private static readonly double[] ZoomSteps = [0.1, 0.25, 0.33, 0.5, 0.67, 0.75, 1, 1.25, 1.5, 2, 3, 4, 6, 8];

    private readonly AppServices _app;
    private readonly EditorSession _session;
    private readonly EditorSurface _surface = new();
    private readonly Canvas _textCanvas = new();
    private readonly Grid _stage = new();
    private readonly Border _frame = new();
    private readonly Border _clip = new() { ClipToBounds = true };
    private readonly ScrollViewer _scroll;
    private readonly TextEditHost _textHost;
    private readonly ToolPalette _palette;
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
    private readonly TextBlock _zoomLabel = new() { VerticalAlignment = VerticalAlignment.Center, MinWidth = 48, TextAlignment = TextAlignment.Center };
    private readonly string _sourceApp;
    private readonly string _sourceTitle;
    private CaptureResult _identity;
    private double _zoom = 1;
    private bool _dragging;
    private bool _fitPending = true;
    private bool _closeConfirmed;

    public EditorWindow(AppServices app, CaptureResult result)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        ArgumentNullException.ThrowIfNull(result);
        _identity = result;
        _sourceApp = result.SourceApp;
        _sourceTitle = result.SourceTitle;
        _session = new EditorSession(result.Document);
        _session.SetOptions(app.Settings.Editor.Tool, applyToSelection: false);

        Title = "ShotIt Editor";
        Width = 1100;
        Height = 760;
        MinWidth = 640;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = AppIcon.Render(64);
        UseLayoutRounding = true;

        // --- top bar -------------------------------------------------------------------
        _palette = new ToolPalette(_session, includeCrop: true) { Margin = new Thickness(8, 8, 0, 8), VerticalAlignment = VerticalAlignment.Center };
        _palette.OptionsChanged += options =>
        {
            _app.Settings.Editor.Tool = options;
            _app.SaveSettingsSoon();
        };

        var imageBar = new Border { Style = (Style)FindResource("BarPanel"), Margin = new Thickness(8, 8, 0, 8), VerticalAlignment = VerticalAlignment.Center };
        var imagePanel = new StackPanel { Orientation = Orientation.Horizontal };
        void ImageButton(string icon, string tip, Action action)
        {
            var button = ToolPalette.MakeButton(Icons.Create(icon), tip);
            button.Click += (_, _) => Guard(action);
            imagePanel.Children.Add(button);
        }

        var cropButton = ToolPalette.MakeButton(Icons.Create(Icons.Crop), "Crop (C)");
        cropButton.Click += (_, _) => ShowCropMenu(cropButton);
        imagePanel.Children.Add(cropButton);
        ImageButton(Icons.Rotate, "Rotate 90° clockwise", () => TransformFlattened(ImageEffects.Rotate90));
        ImageButton(Icons.FlipH, "Flip horizontally", () => TransformFlattened(ImageEffects.FlipHorizontal));
        var frameButton = ToolPalette.MakeButton(Icons.Create(Icons.Frame), "Frame, background and watermark");
        frameButton.Click += (_, _) => ShowFramePopup(frameButton);
        imagePanel.Children.Add(frameButton);
        var moreImage = ToolPalette.MakeButton(Icons.Create(Icons.More, thickDots: true), "More image operations");
        moreImage.Click += (_, _) => ShowImageMenu(moreImage);
        imagePanel.Children.Add(moreImage);
        imageBar.Child = imagePanel;

        var actions = new ActionBar(OnAction, includeEdit: false) { Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };

        var top = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(actions, Dock.Right);
        top.Children.Add(actions);
        var left = new WrapPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(_palette);
        left.Children.Add(imageBar);
        top.Children.Add(left);

        // --- stage ---------------------------------------------------------------------
        _stage.Children.Add(_surface);
        _stage.Children.Add(_textCanvas);
        _stage.Background = CheckerBrush();
        _clip.Child = _stage;
        _frame.Child = _clip;
        _frame.HorizontalAlignment = HorizontalAlignment.Center;
        _frame.VerticalAlignment = VerticalAlignment.Center;
        _frame.Margin = new Thickness(24);
        _surface.Session = _session;
        _surface.DrawBase = true;
        _textHost = new TextEditHost(_textCanvas, _surface);
        _textHost.Attach(_session);

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _frame,
            Focusable = false,
        };
        _scroll.SetResourceReference(BackgroundProperty, SystemColors.ControlBrushKey);

        // --- status bar ----------------------------------------------------------------
        var statusBar = new DockPanel { Height = 34 };
        var zoomPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        Button Small(string text, string tip, Action action)
        {
            var b = new Button { Content = text, ToolTip = tip, MinWidth = 30, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(2, 0, 2, 0), Focusable = false };
            b.Click += (_, _) => action();
            return b;
        }

        zoomPanel.Children.Add(Small("Fit", "Fit to window (Ctrl+0)", FitToWindow));
        zoomPanel.Children.Add(Small("−", "Zoom out (Ctrl+−)", () => StepZoom(-1)));
        zoomPanel.Children.Add(_zoomLabel);
        zoomPanel.Children.Add(Small("+", "Zoom in (Ctrl++)", () => StepZoom(1)));
        zoomPanel.Children.Add(Small("100%", "Actual size (Ctrl+1)", () => SetZoom(1)));
        DockPanel.SetDock(zoomPanel, Dock.Right);
        statusBar.Children.Add(zoomPanel);
        statusBar.Children.Add(_status);

        var root = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(statusBar, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(statusBar);
        root.Children.Add(_scroll);
        Content = root;

        // --- wiring --------------------------------------------------------------------
        _stage.MouseDown += OnStageMouseDown;
        _stage.MouseMove += OnStageMouseMove;
        _stage.MouseUp += OnStageMouseUp;
        _stage.LostMouseCapture += (_, _) =>
        {
            if (_dragging)
            {
                _dragging = false;
                _session.CancelPointer();
            }
        };
        _scroll.PreviewMouseWheel += OnWheel;
        PreviewKeyDown += OnKeyDown;
        _session.Changed += (_, _) => OnDocumentChanged();
        _session.CropRequested += (_, rect) => Guard(() =>
        {
            _session.ApplyCrop(rect.ToPixelRectRounded());
            _session.SetTool(ToolKind.Select);
        });
        _session.ColorPickRequested += (_, p) =>
        {
            var image = _session.State.Base;
            int x = (int)Math.Floor(p.X), y = (int)Math.Floor(p.Y);
            if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return;
            _palette.SetColor(image.GetPixel(x, y).WithAlpha(255));
            _session.SetTool(ToolKind.Select);
        };
        DpiChanged += (_, _) => ApplyZoom();
        Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            if (_fitPending) FitToWindow(shrinkOnly: true);
        }, System.Windows.Threading.DispatcherPriority.Loaded);
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _textHost.Detach(_session);
            _palette.Detach();
        };
        AllowDrop = true;
        Drop += OnDrop;

        OnDocumentChanged();
    }

    public EditorSession Session => _session;

    private void Guard(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _app.Log.Error(ex, "Editor operation failed");
            MessageBox.Show(this, "That operation could not be completed.\n\n" + ex.Message, "ShotIt", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static DrawingBrush CheckerBrush()
    {
        // Shows through wherever the image is transparent.
        var light = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
        var dark = new SolidColorBrush(Color.FromRgb(0xD9, 0xD9, 0xD9));
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(light, null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(0, 0, 8, 8))));
        group.Children.Add(new GeometryDrawing(dark, null, new RectangleGeometry(new Rect(8, 8, 8, 8))));
        var brush = new DrawingBrush(group) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, 16, 16), ViewportUnits = BrushMappingMode.Absolute };
        brush.Freeze();
        return brush;
    }

    // ------------------------------------------------------------------ view

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void ApplyZoom()
    {
        var state = _session.State;
        // At 100 % one image pixel covers exactly one device pixel.
        double unit = _zoom / DpiScale;
        _surface.Zoom = unit;
        _stage.Width = state.Base.Width * unit;
        _stage.Height = state.Base.Height * unit;
        RenderOptions.SetBitmapScalingMode(_surface, _zoom >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);

        var canvas = state.Canvas;
        double pad = Math.Max(canvas.Padding, canvas.Shadow ? 28 : 0) * unit;
        _frame.Padding = new Thickness(pad);
        _frame.Background = canvas.Background switch
        {
            BackgroundKind.Solid => canvas.Color1.ToBrush(),
            BackgroundKind.Gradient => new LinearGradientBrush(canvas.Color1.ToWpf(), canvas.Color2.ToWpf(), new Point(0, 0), new Point(1, 1)),
            _ => Brushes.Transparent,
        };
        double radius = canvas.CornerRadius * unit;
        _clip.CornerRadius = new CornerRadius(radius);
        _clip.Clip = radius > 0 ? new RectangleGeometry(new Rect(0, 0, _stage.Width, _stage.Height), radius, radius) : null;
        _clip.Effect = canvas.Shadow ? new DropShadowEffect { BlurRadius = Math.Max(10, pad * 0.55), ShadowDepth = Math.Max(3, pad * 0.18), Direction = 270, Opacity = 0.45 } : null;

        _zoomLabel.Text = string.Create(CultureInfo.CurrentCulture, $"{_zoom * 100:0}%");
        _surface.InvalidateVisual();
    }

    private void SetZoom(double zoom)
    {
        _fitPending = false;
        _zoom = Math.Clamp(zoom, ZoomSteps[0], ZoomSteps[^1]);
        ApplyZoom();
    }

    private void StepZoom(int direction)
    {
        double next = direction > 0
            ? ZoomSteps.FirstOrDefault(z => z > _zoom + 0.001, ZoomSteps[^1])
            : ZoomSteps.LastOrDefault(z => z < _zoom - 0.001, ZoomSteps[0]);
        SetZoom(next);
    }

    private void FitToWindow() => FitToWindow(shrinkOnly: false);

    private void FitToWindow(bool shrinkOnly)
    {
        var state = _session.State;
        double availableW = Math.Max(50, _scroll.ActualWidth - 60), availableH = Math.Max(50, _scroll.ActualHeight - 60);
        double pad = Math.Max(state.Canvas.Padding, state.Canvas.Shadow ? 28 : 0) * 2;
        double fit = Math.Min(availableW * DpiScale / (state.Base.Width + pad), availableH * DpiScale / (state.Base.Height + pad));
        if (shrinkOnly) fit = Math.Min(1, fit);
        _zoom = Math.Clamp(fit, ZoomSteps[0], ZoomSteps[^1]);
        _fitPending = false;
        ApplyZoom();
    }

    private void OnDocumentChanged()
    {
        var state = _session.State;
        ApplyZoom();
        int count = state.Items.Count;
        _status.Text = string.Create(CultureInfo.CurrentCulture,
            $"{state.Base.Width} × {state.Base.Height} px   ·   {count} annotation{(count == 1 ? string.Empty : "s")}{(_session.IsDirty ? "   ·   unsaved changes" : string.Empty)}");
        Title = (_identity.SavedPath is { } path ? Path.GetFileName(path) + " – " : string.Empty) + "ShotIt Editor" + (_session.IsDirty ? " *" : string.Empty);
    }

    // ------------------------------------------------------------------ input

    private void OnStageMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (_textHost.IsEditing)
        {
            _textHost.Close(commit: true);
            e.Handled = true;
            return;
        }

        Focus();
        var doc = _surface.ToDoc(e.GetPosition(_surface));
        if (e.ClickCount == 2 && _session.Tool == ToolKind.Select && _session.PointerDoubleClick(doc, _surface.Tolerance))
        {
            e.Handled = true;
            return;
        }

        if (_session.PointerDown(doc, EditorSurface.CurrentModifiers(), _surface.Tolerance))
        {
            _dragging = true;
            _stage.CaptureMouse();
        }

        e.Handled = true;
    }

    private void OnStageMouseMove(object sender, MouseEventArgs e)
    {
        var view = e.GetPosition(_surface);
        if (_dragging) _session.PointerMove(_surface.ToDoc(view), EditorSurface.CurrentModifiers());
        else _stage.Cursor = _surface.CursorAt(view) ?? Cursors.Arrow;
    }

    private void OnStageMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !_dragging) return;
        _dragging = false;
        _session.PointerUp(_surface.ToDoc(e.GetPosition(_surface)), EditorSurface.CurrentModifiers());
        if (_stage.IsMouseCaptured) _stage.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        StepZoom(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_textHost.IsEditing || e.OriginalSource is TextBoxBase) return;
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control), shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool handled = true;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z:
                    if (shift) _session.Redo(); else _session.Undo();
                    break;
                case Key.Y: _session.Redo(); break;
                case Key.C:
                    if (shift || _session.Selection.IsEmpty) OnAction(OverlayAction.Copy);
                    else _session.CopySelected();
                    break;
                case Key.X:
                    _session.CopySelected();
                    _session.DeleteSelected();
                    break;
                case Key.V: Guard(Paste); break;
                case Key.D: _session.DuplicateSelected(); break;
                case Key.A:
                    _session.SetTool(ToolKind.Select);
                    _session.SelectAll();
                    break;
                case Key.S: OnAction(shift ? OverlayAction.SaveAs : OverlayAction.Save); break;
                case Key.P: OnAction(OverlayAction.Print); break;
                case Key.U: OnAction(OverlayAction.Upload); break;
                case Key.T: OnAction(OverlayAction.Pin); break;
                case Key.D0 or Key.NumPad0: FitToWindow(); break;
                case Key.D1 or Key.NumPad1: SetZoom(1); break;
                case Key.OemPlus or Key.Add: StepZoom(1); break;
                case Key.OemMinus or Key.Subtract: StepZoom(-1); break;
                case Key.OemCloseBrackets:
                    if (shift) _session.BringToFront(); else _session.BringForward();
                    break;
                case Key.OemOpenBrackets:
                    if (shift) _session.SendToBack(); else _session.SendBackward();
                    break;
                default: handled = false; break;
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.Delete or Key.Back: _session.DeleteSelected(); break;
                case Key.Escape:
                    if (_session.IsGestureActive || _session.Draft is not null || _session.CropDraft is not null) _session.CancelPointer();
                    else if (!_session.Selection.IsEmpty) _session.ClearSelection();
                    else if (_session.Tool != ToolKind.Select) _session.SetTool(ToolKind.Select);
                    else Close(); // nothing left to cancel: Esc closes the editor (it asks first when there are unsaved changes)
                    break;
                case Key.Left or Key.Right or Key.Up or Key.Down:
                {
                    int step = shift ? 10 : 1;
                    _session.NudgeSelected(new Vec2(e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0, e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0));
                    break;
                }
                default:
                    if (Keyboard.Modifiers == ModifierKeys.None && ToolPalette.TryGetToolForKey(e.Key, out var tool, allowCrop: true)) _session.SetTool(tool);
                    else handled = false;
                    break;
            }
        }

        e.Handled = handled;
    }

    private void Paste()
    {
        if (_session.HasCopiedItems)
        {
            _session.PasteCopied();
            return;
        }

        if (ClipboardService.GetImage() is { } image) AddImage(image);
    }

    private void AddImage(PixelBuffer image)
    {
        var state = _session.State;
        // Place pasted pictures at their own size, shrunk only when larger than the document.
        double scale = Math.Min(1, Math.Min((double)state.Base.Width / image.Width, (double)state.Base.Height / image.Height));
        double w = image.Width * scale, h = image.Height * scale;
        _session.AddAnnotation(new ImageAnnotation
        {
            Rect = new RectD((state.Base.Width - w) / 2, (state.Base.Height - h) / 2, w, h),
            EncodedImage = ImageCodec.Encode(image, ImageFormat.Png),
        });
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        Guard(() =>
        {
            foreach (string file in files)
            {
                try
                {
                    AddImage(ImageCodec.DecodeFile(file));
                }
                catch (NotSupportedException)
                {
                    _app.Notifier.Warn("Not an image", Path.GetFileName(file) + " could not be read as an image.");
                }
            }
        });
    }

    // ------------------------------------------------------------------ image operations

    /// <summary>Burns annotations into the image, then applies a whole-image transform. One undo step.</summary>
    private void TransformFlattened(Func<PixelBuffer, PixelBuffer> transform)
    {
        _textHost.Close(commit: true);
        _session.ReplaceBase(transform(DocumentRenderer.Flatten(_session.State)), []);
        _session.SetTool(ToolKind.Select);
    }

    /// <summary>Changes only the base image and keeps annotations editable.</summary>
    private void AdjustBase(Func<PixelBuffer, PixelBuffer> adjust) => _session.ReplaceBase(adjust(_session.State.Base));

    private void ShowCropMenu(UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        foreach (var (name, aspect) in new (string, double?)[] { ("Free", null), ("Square 1:1", 1), ("4:3", 4.0 / 3), ("3:2", 1.5), ("16:9", 16.0 / 9), ("9:16 (portrait)", 9.0 / 16) })
        {
            var item = new MenuItem { Header = "Crop: " + name, IsChecked = _session.Tool == ToolKind.Crop && _session.CropAspect == aspect };
            item.Click += (_, _) =>
            {
                _session.CropAspect = aspect;
                _session.SetTool(ToolKind.Crop);
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void ShowImageMenu(UIElement target)
    {
        var menu = new ContextMenu { PlacementTarget = target, Placement = PlacementMode.Bottom };
        void Add(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => Guard(action);
            menu.Items.Add(item);
        }

        Add("Rotate 90° counter-clockwise", () => TransformFlattened(b => ImageEffects.Rotate90(ImageEffects.Rotate90(ImageEffects.Rotate90(b)))));
        Add("Rotate 180°", () => TransformFlattened(b => ImageEffects.Rotate90(ImageEffects.Rotate90(b))));
        Add("Flip vertically", () => TransformFlattened(ImageEffects.FlipVertical));
        menu.Items.Add(new Separator());
        foreach (int percent in new[] { 25, 50, 75, 150, 200 })
        {
            int p = percent;
            Add($"Resize to {p} %", () => TransformFlattened(b => ImageEffects.Resize(b, Math.Max(1, b.Width * p / 100), Math.Max(1, b.Height * p / 100))));
        }

        menu.Items.Add(new Separator());
        Add("Grayscale", () => AdjustBase(ImageEffects.Grayscale));
        Add("Invert colours", () => AdjustBase(ImageEffects.Invert));
        Add("Brighter", () => AdjustBase(b => ImageEffects.BrightnessContrast(b, 12, 0)));
        Add("Darker", () => AdjustBase(b => ImageEffects.BrightnessContrast(b, -12, 0)));
        Add("More contrast", () => AdjustBase(b => ImageEffects.BrightnessContrast(b, 0, 15)));
        Add("Less contrast", () => AdjustBase(b => ImageEffects.BrightnessContrast(b, 0, -15)));
        menu.Items.Add(new Separator());
        Add("Flatten annotations into the image", () => TransformFlattened(b => b));
        Add("Paste image as object\tCtrl+V", Paste);
        menu.IsOpen = true;
    }

    private void ShowFramePopup(UIElement target)
    {
        var popup = new Popup { StaysOpen = false, AllowsTransparency = true, PlacementTarget = target, Placement = PlacementMode.Bottom, VerticalOffset = 6 };
        var stack = new StackPanel { Width = 250 };
        var label = (Style)FindResource("BarLabel");

        void Update(Func<CanvasStyle, CanvasStyle> change) => _session.Apply(_session.State with { Canvas = change(_session.State.Canvas) });

        (Slider Slider, TextBlock Caption) Row(string name, double min, double max, double value, Action<double> changed)
        {
            var caption = new TextBlock { Style = label, Margin = new Thickness(0, 6, 0, 2), Text = string.Create(CultureInfo.CurrentCulture, $"{name}: {value:0}") };
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, IsSnapToTickEnabled = true, TickFrequency = 1, Focusable = false };
            slider.ValueChanged += (_, e) =>
            {
                caption.Text = string.Create(CultureInfo.CurrentCulture, $"{name}: {e.NewValue:0}");
                changed(e.NewValue);
            };
            stack.Children.Add(caption);
            stack.Children.Add(slider);
            return (slider, caption);
        }

        var canvas = _session.State.Canvas;
        Row("Padding", 0, 160, canvas.Padding, v => Update(c => c with { Padding = v }));
        Row("Rounded corners", 0, 48, canvas.CornerRadius, v => Update(c => c with { CornerRadius = v }));

        var shadow = new CheckBox { Content = "Drop shadow", IsChecked = canvas.Shadow, Foreground = (Brush)FindResource("BarForeground"), Margin = new Thickness(0, 8, 0, 4), Focusable = false };
        shadow.Click += (_, _) => Update(c => c with { Shadow = shadow.IsChecked == true });
        stack.Children.Add(shadow);

        stack.Children.Add(new TextBlock { Style = label, Text = "Background", Margin = new Thickness(0, 6, 0, 2) });
        var backgrounds = new WrapPanel();
        Button Preset(Brush brush, string tip, Func<CanvasStyle, CanvasStyle> apply)
        {
            var b = new Button { Style = (Style)FindResource("SwatchButton"), Background = brush, ToolTip = tip };
            b.Click += (_, _) => Update(c =>
            {
                var next = apply(c);
                // A backdrop without padding would be invisible; give it a sensible default.
                return next.Background != BackgroundKind.None && next.Padding < 1 ? next with { Padding = 48 } : next;
            });
            return b;
        }

        backgrounds.Children.Add(Preset(Brushes.Transparent, "None", c => c with { Background = BackgroundKind.None }));
        (ColorRgba A, ColorRgba B)[] gradients =
        [
            (new(0x4F, 0x6B, 0xFF), new(0xB1, 0x4F, 0xFF)), (new(0xFF, 0x7E, 0x5F), new(0xFE, 0xB4, 0x7B)),
            (new(0x11, 0x99, 0x8E), new(0x38, 0xEF, 0x7D)), (new(0xFC, 0x46, 0x6B), new(0x3F, 0x5E, 0xFB)),
            (new(0x23, 0x25, 0x26), new(0x41, 0x43, 0x45)),
        ];
        foreach (var (a, b) in gradients)
            backgrounds.Children.Add(Preset(new LinearGradientBrush(a.ToWpf(), b.ToWpf(), 45), "Gradient", c => c with { Background = BackgroundKind.Gradient, Color1 = a, Color2 = b }));
        foreach (var solid in new ColorRgba[] { ColorRgba.White, new(0xF1, 0xF3, 0xF5), new(0x1F, 0x21, 0x25), ColorRgba.Black })
            backgrounds.Children.Add(Preset(solid.ToBrush(), solid.ToHex(), c => c with { Background = BackgroundKind.Solid, Color1 = solid }));
        stack.Children.Add(backgrounds);

        stack.Children.Add(new TextBlock { Style = label, Text = "Watermark text", Margin = new Thickness(0, 8, 0, 2) });
        var watermark = new TextBox { Text = _session.State.Watermark?.Text ?? _app.Settings.Editor.WatermarkText, MaxLength = 80 };
        watermark.LostFocus += (_, _) => ApplyWatermark(watermark.Text);
        watermark.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) ApplyWatermark(watermark.Text);
        };
        stack.Children.Add(watermark);

        popup.Child = new Border { Style = (Style)FindResource("BarPanel"), Padding = new Thickness(12), Margin = new Thickness(8), Child = stack };
        popup.IsOpen = true;
    }

    private void ApplyWatermark(string text)
    {
        text = text.Trim();
        var current = _session.State.Watermark;
        if ((current?.Text ?? string.Empty) == text) return;
        _session.Apply(_session.State with { Watermark = text.Length == 0 ? null : (current ?? new Watermark()) with { Text = text } });
    }

    // ------------------------------------------------------------------ output

    private CaptureResult Snapshot()
    {
        _textHost.Close(commit: true);
        var result = new CaptureResult(_session.State, _sourceApp, _sourceTitle)
        {
            HistoryId = _identity.HistoryId,
            LibraryPath = _identity.LibraryPath,
            SavedPath = _identity.SavedPath,
        };
        _identity = result;
        return result;
    }

    private void OnAction(OverlayAction action)
    {
        if (action == OverlayAction.Cancel)
        {
            Close();
            return;
        }

        Guard(() =>
        {
            var result = Snapshot();
            if (action == OverlayAction.Save && result.SavedPath is { } existing && File.Exists(existing))
            {
                // Saving again writes back to the same file.
                if (!_app.Output.Overwrite(result, existing, out string? error)) _app.Notifier.Warn("Could not save", error ?? "The file could not be written.");
                else _session.MarkSaved();
            }
            else
            {
                _app.Actions.Run(result, action, this);
                if (action is OverlayAction.Save or OverlayAction.SaveAs && result.SavedPath is not null) _session.MarkSaved();
                if (action == OverlayAction.Copy) _session.MarkSaved();
            }

            OnDocumentChanged();
        });
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_closeConfirmed || !_session.IsDirty) return;
        var answer = MessageBox.Show(this, "Save your changes before closing?", "ShotIt Editor", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (answer == MessageBoxResult.Yes)
        {
            OnAction(OverlayAction.Save);
            if (_session.IsDirty)
            {
                e.Cancel = true; // saving failed; keep the window so nothing is lost
                return;
            }
        }

        _closeConfirmed = true;
    }
}
