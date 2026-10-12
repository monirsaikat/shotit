using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShotIt.App.Infrastructure;
using ShotIt.Core.Settings;

namespace ShotIt.App.Views;

/// <summary>Small builders so the windows read as a list of settings rather than as layout code.</summary>
internal static class Ui
{
    public const double LabelWidth = 210;

    public static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 20,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 14),
    };

    public static TextBlock SubHeading(string text) => new()
    {
        Text = text,
        FontSize = 14,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 18, 0, 8),
    };

    public static TextBlock Hint(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Opacity = 0.72,
        Margin = new Thickness(0, 2, 0, 8),
    };

    /// <summary>A label on the left and a control on the right.</summary>
    public static FrameworkElement Row(string label, FrameworkElement control, string? hint = null)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 12, 0) };
        grid.Children.Add(text);
        control.VerticalAlignment = VerticalAlignment.Center;
        if (control.HorizontalAlignment == HorizontalAlignment.Stretch && control is not TextBox && control is not Grid && control is not DockPanel)
            control.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        System.Windows.Automation.AutomationProperties.SetName(control, label);
        if (hint is null) return grid;

        var stack = new StackPanel();
        stack.Children.Add(grid);
        var hintBlock = Hint(hint);
        hintBlock.Margin = new Thickness(LabelWidth, 0, 0, 6);
        stack.Children.Add(hintBlock);
        return stack;
    }

    public static CheckBox Check(string label, bool value, Action<bool> changed, string? tooltip = null)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 5, 0, 5), ToolTip = tooltip };
        box.Checked += (_, _) => changed(true);
        box.Unchecked += (_, _) => changed(false);
        return box;
    }

    public static ComboBox Combo<T>(IEnumerable<(T Value, string Label)> options, T value, Action<T> changed)
    {
        var combo = new ComboBox { MinWidth = 220 };
        foreach (var (optionValue, label) in options)
        {
            var item = new ComboBoxItem { Content = label, Tag = optionValue };
            combo.Items.Add(item);
            if (EqualityComparer<T>.Default.Equals(optionValue, value)) combo.SelectedItem = item;
        }

        if (combo.SelectedItem is null && combo.Items.Count > 0) combo.SelectedIndex = 0;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: T chosen }) changed(chosen);
        };
        return combo;
    }

    public static TextBox Text(string value, Action<string> changed, double minWidth = 260, bool multiline = false)
    {
        var box = new TextBox { Text = value, MinWidth = minWidth };
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.NoWrap;
            box.MinLines = 3;
            box.MaxLines = 8;
            box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }

        box.TextChanged += (_, _) => changed(box.Text);
        return box;
    }

    /// <summary>A password field that never shows a stored secret: it starts empty and only reports what the user types.</summary>
    public static FrameworkElement Secret(bool hasValue, Action<string> changed)
    {
        var box = new PasswordBox { MinWidth = 260 };
        var state = new TextBlock { Text = hasValue ? "A value is stored. Type to replace it." : "Not set.", Opacity = 0.72, Margin = new Thickness(0, 2, 0, 0) };
        box.PasswordChanged += (_, _) =>
        {
            if (box.Password.Length == 0) return;
            changed(box.Password);
            state.Text = "Saved (encrypted for your Windows account).";
        };
        var stack = new StackPanel();
        stack.Children.Add(box);
        stack.Children.Add(state);
        return stack;
    }

    /// <summary>Whole-number entry that ignores anything else and clamps on commit.</summary>
    public static TextBox Number(int value, int min, int max, Action<int> changed)
    {
        var box = new TextBox { Text = value.ToString(CultureInfo.CurrentCulture), Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
        box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit);
        DataObject.AddPastingHandler(box, (_, e) =>
        {
            if (e.DataObject.GetData(DataFormats.UnicodeText) is not string pasted || !pasted.All(char.IsDigit)) e.CancelCommand();
        });
        box.TextChanged += (_, _) =>
        {
            if (int.TryParse(box.Text, NumberStyles.None, CultureInfo.CurrentCulture, out int parsed) && parsed >= min && parsed <= max) changed(parsed);
        };
        box.LostFocus += (_, _) =>
        {
            int parsed = int.TryParse(box.Text, NumberStyles.None, CultureInfo.CurrentCulture, out int v) ? Math.Clamp(v, min, max) : value;
            box.Text = parsed.ToString(CultureInfo.CurrentCulture);
            changed(parsed);
        };
        return box;
    }

    public static Button Button(string text, Action click, bool accent = false)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(0, 4, 8, 4), HorizontalAlignment = HorizontalAlignment.Left };
        if (accent && Application.Current.TryFindResource("AccentButtonStyle") is Style style) button.Style = style;
        button.Click += (_, _) => click();
        return button;
    }

    public static StackPanel Horizontal(params UIElement[] children)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    public static string Describe(AfterCaptureAction action) => action switch
    {
        AfterCaptureAction.ShowToolbar => "Show the toolbar",
        AfterCaptureAction.Copy => "Copy to clipboard",
        AfterCaptureAction.Save => "Save to the screenshots folder",
        AfterCaptureAction.OpenEditor => "Open in the editor",
        AfterCaptureAction.Upload => "Upload and copy the link",
        AfterCaptureAction.Pin => "Pin to the screen",
        _ => action.ToString(),
    };
}

/// <summary>
/// A field that records a key combination. While it has focus the application's global hotkeys are
/// released, so pressing the current combination records it instead of starting a capture.
/// </summary>
internal sealed class HotkeyBox : TextBox
{
    private readonly HotkeyManager _manager;
    private Hotkey _value;

    public HotkeyBox(HotkeyManager manager, Hotkey value)
    {
        _manager = manager;
        _value = value;
        // A subclass does not pick up the themed TextBox style by itself.
        SetResourceReference(StyleProperty, typeof(TextBox));
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Width = 210;
        Cursor = Cursors.Hand;
        HorizontalAlignment = HorizontalAlignment.Left;
        ToolTip = "Click, then press the keys. Backspace clears, Esc cancels.";
        ContextMenu = null;
        InputMethod.SetIsInputMethodEnabled(this, false);
        Show();
    }

    public event Action<Hotkey>? ValueChanged;

    /// <summary>Raised when recording ends, so the owner can register the hotkeys again.</summary>
    public event Action? RecordingEnded;

    public Hotkey Value
    {
        get => _value;
        set
        {
            _value = value;
            Show();
        }
    }

    private void Show() => Text = IsKeyboardFocused ? "Press the keys…" : _value.IsEmpty ? "None" : _value.ToString();

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _manager.Suspend();
        Show();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Show();
        RecordingEnded?.Invoke();
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        var mods = HotkeyModifiers.None;
        var keys = Keyboard.Modifiers;
        if (keys.HasFlag(ModifierKeys.Control)) mods |= HotkeyModifiers.Ctrl;
        if (keys.HasFlag(ModifierKeys.Alt)) mods |= HotkeyModifiers.Alt;
        if (keys.HasFlag(ModifierKeys.Shift)) mods |= HotkeyModifiers.Shift;
        if (keys.HasFlag(ModifierKeys.Windows)) mods |= HotkeyModifiers.Win;
        return mods;
    }

    private static bool IsModifier(Key key) =>
        key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System or Key.None
            or Key.ImeProcessed or Key.DeadCharProcessed or Key.CapsLock or Key.NumLock;

    /// <summary>Keys that are safe as a global hotkey without any modifier.</summary>
    internal static bool AllowedAlone(Key key) => key is Key.Snapshot or Key.Pause or Key.Scroll or (>= Key.F1 and <= Key.F24);

    /// <summary>Decides what a key press means for the recorder. Null means "keep waiting".</summary>
    internal static Hotkey? Interpret(Key key, HotkeyModifiers modifiers, out bool clear, out bool cancel)
    {
        clear = cancel = false;
        if (IsModifier(key)) return null;
        if (modifiers == HotkeyModifiers.None)
        {
            if (key == Key.Escape) { cancel = true; return null; }
            if (key is Key.Back or Key.Delete) { clear = true; return null; }
            if (key == Key.Tab) return null;
            if (!AllowedAlone(key)) return null;
        }
        else if (modifiers == HotkeyModifiers.Shift && !AllowedAlone(key))
        {
            // Shift plus a letter is ordinary typing; as a global hotkey it would break every text box.
            return null;
        }

        int vk = KeyInterop.VirtualKeyFromKey(key);
        return vk is > 0 and < 256 ? new Hotkey(modifiers, vk) : null;
    }

    private void Handle(Key key, KeyEventArgs e)
    {
        var modifiers = CurrentModifiers();
        if (key == Key.Tab && modifiers is HotkeyModifiers.None or HotkeyModifiers.Shift) return; // keep keyboard navigation working
        e.Handled = true;
        var hotkey = Interpret(key, modifiers, out bool clear, out bool cancel);
        if (cancel)
        {
            Keyboard.ClearFocus();
            return;
        }

        if (clear) hotkey = Hotkey.None;
        if (hotkey is not { } recorded) return;
        _value = recorded;
        ValueChanged?.Invoke(recorded);
        Keyboard.ClearFocus();
        Show();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        // Print Screen never produces a key-down message; it is handled on key-up below.
        if (key != Key.Snapshot) Handle(key, e);
        base.OnPreviewKeyDown(e);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Snapshot && IsKeyboardFocused) Handle(key, e);
        base.OnPreviewKeyUp(e);
    }
}

internal static class Brushes2
{
    public static readonly Brush Warning = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0x7A, 0x00)));
    public static readonly Brush Success = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x4F)));

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
