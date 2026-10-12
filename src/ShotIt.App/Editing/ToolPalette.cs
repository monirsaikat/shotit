using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ShotIt.App.Themes;
using ShotIt.Core.Editing;
using ShotIt.Core.Imaging;
using ShotIt.Rendering;

namespace ShotIt.App.Editing;

/// <summary>
/// The drawing toolbar: tool buttons, colour, size, a per-tool style toggle, undo and redo.
/// Used on the capture overlay and in the editor window; everything it does goes through the session.
/// </summary>
public sealed class ToolPalette : Border
{
    private static readonly (ToolKind Tool, string Icon, string Tip)[] PrimaryTools =
    [
        (ToolKind.Select, Icons.Select, "Select and move (V)"),
        (ToolKind.Arrow, Icons.Arrow, "Arrow (A)"),
        (ToolKind.Line, Icons.Line, "Line (L)"),
        (ToolKind.Rectangle, Icons.Rectangle, "Rectangle (R)"),
        (ToolKind.Ellipse, Icons.Ellipse, "Ellipse (E)"),
        (ToolKind.Pen, Icons.Pen, "Pen (P)"),
        (ToolKind.Highlighter, Icons.Highlighter, "Highlighter (H)"),
        (ToolKind.Text, Icons.Text, "Text (T)"),
        (ToolKind.Steps, Icons.Steps, "Numbered steps (N)"),
        (ToolKind.Blur, Icons.Blur, "Blur (B)"),
    ];

    private static readonly (ToolKind Tool, string Icon, string Name, string Key)[] MoreTools =
    [
        (ToolKind.AreaHighlight, Icons.AreaHighlight, "Highlight area", "G"),
        (ToolKind.Spotlight, Icons.Spotlight, "Spotlight (dim the rest)", "S"),
        (ToolKind.Callout, Icons.Callout, "Callout", "C"),
        (ToolKind.Magnifier, Icons.Magnifier, "Magnifier inset", "M"),
        (ToolKind.Pixelate, Icons.Pixelate, "Pixelate", "X"),
        (ToolKind.Redact, Icons.Redact, "Redact (solid, cannot be undone after saving)", "D"),
        (ToolKind.Sticker, Icons.Sticker, "Sticker", "K"),
        (ToolKind.Measure, Icons.Measure, "Measure distance", "U"),
        (ToolKind.Eyedropper, Icons.Eyedropper, "Pick colour from image", "I"),
    ];

    public static readonly ColorRgba[] Palette =
    [
        new(0xE8, 0x1C, 0x2E), new(0xFF, 0x6D, 0x00), new(0xFF, 0xC1, 0x07), new(0xFF, 0xE6, 0x00),
        new(0x2E, 0xB8, 0x5C), new(0x00, 0xBF, 0xA5), new(0x1E, 0x88, 0xE5), new(0x3D, 0x5A, 0xFE),
        new(0x7E, 0x57, 0xC2), new(0xD8, 0x1B, 0x60), new(0xFF, 0x8A, 0xD8), new(0x7C, 0xFF, 0x6B),
        new(0xFF, 0xFF, 0xFF), new(0xB0, 0xB8, 0xC1), new(0x5F, 0x67, 0x70), new(0x00, 0x00, 0x00),
    ];

    private readonly EditorSession _session;
    private readonly Dictionary<ToolKind, ToggleButton> _toolButtons = [];
    private readonly ToggleButton _moreButton;
    private readonly Button _colorButton;
    private readonly Ellipse _colorDot;
    private readonly Button _sizeButton;
    private readonly Ellipse _sizeDot;
    private readonly ToggleButton _styleToggle;
    private readonly Button _undo;
    private readonly Button _redo;
    private readonly Popup _popup;
    private bool _updating;

    public ToolPalette(EditorSession session, bool includeCrop = false)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        Style = (Style)Application.Current.FindResource("BarPanel");
        // Clicks on the bar itself must not reach the canvas underneath.
        MouseDown += (_, e) => e.Handled = true;
        MouseUp += (_, e) => e.Handled = true;

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (tool, icon, tip) in PrimaryTools)
        {
            var button = MakeToggle(icon, tip);
            button.Click += (_, _) =>
            {
                _session.SetTool(tool);
                Refresh(); // clicking the active tool must not leave its button unchecked
            };
            _toolButtons[tool] = button;
            panel.Children.Add(button);
        }

        _moreButton = MakeToggle(Icons.More, "More tools", thickDots: true);
        _moreButton.Click += (_, _) => ShowMoreMenu(includeCrop);
        panel.Children.Add(_moreButton);
        panel.Children.Add(Separator());

        _colorDot = new Ellipse { Width = 16, Height = 16, Stroke = new SolidColorBrush(Color.FromArgb(0x99, 255, 255, 255)), StrokeThickness = 1 };
        _colorButton = MakeButton(_colorDot, "Colour");
        _colorButton.Click += (_, _) => ShowColorPopup();
        panel.Children.Add(_colorButton);

        _sizeDot = new Ellipse { Fill = (Brush)Application.Current.FindResource("BarForeground") };
        _sizeButton = MakeButton(_sizeDot, "Size");
        _sizeButton.Click += (_, _) => ShowSizePopup();
        panel.Children.Add(_sizeButton);

        _styleToggle = MakeToggle(Icons.Fill, "Fill");
        _styleToggle.Click += (_, _) => ToggleStyle();
        panel.Children.Add(_styleToggle);
        panel.Children.Add(Separator());

        _undo = MakeButton(Icons.Create(Icons.Undo), "Undo (Ctrl+Z)");
        _undo.Click += (_, _) => _session.Undo();
        _redo = MakeButton(Icons.Create(Icons.Redo), "Redo (Ctrl+Y)");
        _redo.Click += (_, _) => _session.Redo();
        panel.Children.Add(_undo);
        panel.Children.Add(_redo);

        Child = panel;
        _popup = new Popup { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom, VerticalOffset = 6, PopupAnimation = PopupAnimation.Fade };

        _session.Changed += OnSessionChanged;
        _session.ToolChanged += OnSessionChanged;
        Unloaded += (_, _) => _popup.IsOpen = false;
        Refresh();
    }

    /// <summary>Raised after the user changes colour, size or style so the host can persist the new defaults.</summary>
    public event Action<ToolOptions>? OptionsChanged;

    /// <summary>Stops listening to the session. Call when the hosting window goes away.</summary>
    public void Detach()
    {
        _session.Changed -= OnSessionChanged;
        _session.ToolChanged -= OnSessionChanged;
        _popup.IsOpen = false;
    }

    private void OnSessionChanged(object? sender, EventArgs e) => Refresh();

    private static ToggleButton MakeToggle(string icon, string tip, bool thickDots = false) => new()
    {
        Style = (Style)Application.Current.FindResource("BarToggle"),
        Content = Icons.Create(icon, thickDots),
        ToolTip = tip,
    };

    public static Button MakeButton(object content, string tip) => new()
    {
        Style = (Style)Application.Current.FindResource("BarButton"),
        Content = content,
        ToolTip = tip,
    };

    public static Rectangle Separator() => new() { Style = (Style)Application.Current.FindResource("BarSeparatorStyle") };

    // ---------------------------------------------------------------- state -> UI

    private bool UsesHighlightColor => _session.Tool is ToolKind.Highlighter or ToolKind.AreaHighlight;

    private void Refresh()
    {
        _updating = true;
        try
        {
            foreach (var (tool, button) in _toolButtons) button.IsChecked = _session.Tool == tool;
            _moreButton.IsChecked = !_toolButtons.ContainsKey(_session.Tool);
            var more = MoreTools.FirstOrDefault(m => m.Tool == _session.Tool);
            _moreButton.ToolTip = more.Name is null ? "More tools" : "More tools · " + more.Name;

            var options = _session.Options;
            _colorDot.Fill = (UsesHighlightColor ? options.HighlightColor : options.Color).ToBrush();

            var (value, min, max, _) = SizeBinding();
            double shown = 4 + 12 * Math.Clamp((value - min) / Math.Max(1e-9, max - min), 0, 1);
            _sizeDot.Width = shown;
            _sizeDot.Height = shown;

            var style = StyleBinding();
            _styleToggle.Visibility = style is null ? Visibility.Collapsed : Visibility.Visible;
            if (style is { } s)
            {
                _styleToggle.Content = Icons.Create(s.Icon);
                _styleToggle.ToolTip = s.Tip;
                _styleToggle.IsChecked = s.Value;
            }

            _undo.IsEnabled = _session.CanUndo;
            _redo.IsEnabled = _session.CanRedo;
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>The meaning of the size control depends on the active tool.</summary>
    private (double Value, double Min, double Max, string Label) SizeBinding()
    {
        var o = _session.Options;
        return _session.Tool switch
        {
            ToolKind.Highlighter => (o.HighlightWidth, 8, 48, "Marker width"),
            ToolKind.Text or ToolKind.Callout => (o.FontSize, 10, 72, "Font size"),
            ToolKind.Blur => (o.BlurRadius, 4, 40, "Blur strength"),
            ToolKind.Pixelate => (o.PixelSize, 4, 40, "Block size"),
            ToolKind.Steps => (o.StepRadius, 10, 40, "Badge size"),
            ToolKind.Spotlight => (o.SpotlightDim * 100, 20, 90, "Dim amount"),
            _ => (o.StrokeWidth, 1, 24, "Line width"),
        };
    }

    private void ApplySize(double value)
    {
        var o = _session.Options;
        Commit(_session.Tool switch
        {
            ToolKind.Highlighter => o with { HighlightWidth = value },
            ToolKind.Text or ToolKind.Callout => o with { FontSize = value },
            ToolKind.Blur => o with { BlurRadius = value },
            ToolKind.Pixelate => o with { PixelSize = (int)Math.Round(value) },
            ToolKind.Steps => o with { StepRadius = value },
            ToolKind.Spotlight => o with { SpotlightDim = value / 100 },
            _ => o with { StrokeWidth = value },
        });
    }

    private (string Icon, string Tip, bool Value)? StyleBinding()
    {
        var o = _session.Options;
        return _session.Tool switch
        {
            ToolKind.Rectangle or ToolKind.Ellipse => (Icons.Fill, "Filled shape", o.Filled),
            ToolKind.Arrow => (Icons.DoubleArrow, "Arrow heads on both ends", o.DoubleHeaded),
            ToolKind.Line => (Icons.Dashed, "Dashed line", o.Dashed),
            ToolKind.Text => (Icons.Frame, "Background behind text", o.TextBackground),
            _ => null,
        };
    }

    private void ToggleStyle()
    {
        if (_updating) return;
        var o = _session.Options;
        bool on = _styleToggle.IsChecked == true;
        Commit(_session.Tool switch
        {
            ToolKind.Rectangle or ToolKind.Ellipse => o with { Filled = on },
            ToolKind.Arrow => o with { DoubleHeaded = on },
            ToolKind.Line => o with { Dashed = on },
            ToolKind.Text => o with { TextBackground = on },
            _ => o,
        });
    }

    private void Commit(ToolOptions options)
    {
        _session.SetOptions(options);
        OptionsChanged?.Invoke(options);
        Refresh();
    }

    // ---------------------------------------------------------------- popups

    private void ShowMoreMenu(bool includeCrop)
    {
        Refresh(); // undo the automatic toggle; the checked state reflects the active tool
        var menu = new ContextMenu { PlacementTarget = _moreButton, Placement = PlacementMode.Bottom };
        foreach (var (tool, icon, name, key) in MoreTools)
        {
            if (tool == ToolKind.Sticker)
            {
                var stickers = new MenuItem { Header = name, InputGestureText = key };
                foreach (string sticker in Stickers.Names)
                {
                    var item = new MenuItem { Header = char.ToUpper(sticker[0], CultureInfo.InvariantCulture) + sticker[1..], Icon = StickerPreview(sticker) };
                    item.Click += (_, _) =>
                    {
                        _session.SetOptions(_session.Options with { Sticker = sticker }, applyToSelection: false);
                        _session.SetTool(ToolKind.Sticker);
                        OptionsChanged?.Invoke(_session.Options);
                    };
                    stickers.Items.Add(item);
                }

                menu.Items.Add(stickers);
                continue;
            }

            var entry = new MenuItem { Header = name, InputGestureText = key, IsChecked = _session.Tool == tool };
            entry.Click += (_, _) => _session.SetTool(tool);
            menu.Items.Add(entry);
        }

        if (includeCrop)
        {
            menu.Items.Add(new Separator());
            var crop = new MenuItem { Header = "Crop", IsChecked = _session.Tool == ToolKind.Crop };
            crop.Click += (_, _) => _session.SetTool(ToolKind.Crop);
            menu.Items.Add(crop);
        }

        menu.IsOpen = true;
    }

    private static Image StickerPreview(string name)
    {
        var drawing = new DrawingGroup();
        using (var dc = drawing.Open()) Stickers.Draw(dc, name, new Rect(0, 0, 16, 16));
        drawing.Freeze();
        return new Image { Source = new DrawingImage(drawing), Width = 16, Height = 16 };
    }

    private Border PopupShell(UIElement content) => new()
    {
        Style = (Style)Application.Current.FindResource("BarPanel"),
        Padding = new Thickness(10),
        Margin = new Thickness(8),
        Child = content,
    };

    private void ShowColorPopup()
    {
        var stack = new StackPanel { Width = 208 };
        var grid = new WrapPanel();
        foreach (var color in Palette) grid.Children.Add(Swatch(color));
        stack.Children.Add(grid);

        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 8, 0, 0) };
        hexRow.Children.Add(new TextBlock { Text = "Hex", Style = (Style)Application.Current.FindResource("BarLabel"), Margin = new Thickness(0, 0, 8, 0) });
        var current = UsesHighlightColor ? _session.Options.HighlightColor : _session.Options.Color;
        var hex = new TextBox { Text = current.ToHex(), Width = 96, MaxLength = 9, VerticalContentAlignment = VerticalAlignment.Center };
        hex.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            if (ColorRgba.TryParse(hex.Text, out var parsed)) PickColor(parsed);
            e.Handled = true;
        };
        hexRow.Children.Add(hex);
        stack.Children.Add(hexRow);

        OpenPopup(_colorButton, PopupShell(stack));
    }

    private Button Swatch(ColorRgba color)
    {
        var button = new Button { Style = (Style)Application.Current.FindResource("SwatchButton"), Background = color.ToBrush(), ToolTip = color.ToHex() };
        button.Click += (_, _) => PickColor(color);
        return button;
    }

    private void PickColor(ColorRgba color)
    {
        _popup.IsOpen = false;
        var o = _session.Options;
        Commit(UsesHighlightColor ? o with { HighlightColor = color } : o with { Color = color });
    }

    /// <summary>Sets the main colour from outside (the eyedropper).</summary>
    public void SetColor(ColorRgba color) => Commit(_session.Options with { Color = color });

    private void ShowSizePopup()
    {
        var (value, min, max, label) = SizeBinding();
        var stack = new StackPanel { Width = 200 };
        var caption = new TextBlock { Style = (Style)Application.Current.FindResource("BarLabel"), Margin = new Thickness(2, 0, 0, 6) };
        void SetCaption(double v) => caption.Text = string.Create(CultureInfo.CurrentCulture, $"{label}: {v:0}");
        SetCaption(value);
        var slider = new Slider { Minimum = min, Maximum = max, Value = Math.Clamp(value, min, max), IsSnapToTickEnabled = true, TickFrequency = 1, Focusable = false };
        slider.ValueChanged += (_, e) =>
        {
            SetCaption(e.NewValue);
            ApplySize(e.NewValue);
        };
        stack.Children.Add(caption);
        stack.Children.Add(slider);
        OpenPopup(_sizeButton, PopupShell(stack));
    }

    private void OpenPopup(UIElement target, UIElement content)
    {
        _popup.IsOpen = false;
        _popup.PlacementTarget = target;
        _popup.Child = content;
        _popup.IsOpen = true;
    }

    /// <summary>Maps a key press to a tool. Returns false when the key is not a tool shortcut.</summary>
    public static bool TryGetToolForKey(Key key, out ToolKind tool)
    {
        tool = key switch
        {
            Key.V => ToolKind.Select,
            Key.A => ToolKind.Arrow,
            Key.L => ToolKind.Line,
            Key.R => ToolKind.Rectangle,
            Key.E => ToolKind.Ellipse,
            Key.P => ToolKind.Pen,
            Key.H => ToolKind.Highlighter,
            Key.T => ToolKind.Text,
            Key.N => ToolKind.Steps,
            Key.B => ToolKind.Blur,
            Key.G => ToolKind.AreaHighlight,
            Key.S => ToolKind.Spotlight,
            Key.C => ToolKind.Callout,
            Key.M => ToolKind.Magnifier,
            Key.X => ToolKind.Pixelate,
            Key.D => ToolKind.Redact,
            Key.K => ToolKind.Sticker,
            Key.U => ToolKind.Measure,
            Key.I => ToolKind.Eyedropper,
            _ => (ToolKind)(-1),
        };
        return (int)tool >= 0;
    }
}
