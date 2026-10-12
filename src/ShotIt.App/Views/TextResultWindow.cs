using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShotIt.App.Infrastructure;
using ShotIt.App.Services;
using ShotIt.Rendering;

namespace ShotIt.App.Views;

/// <summary>Shows recognised text or the content of a scanned code, ready to correct and copy.</summary>
public sealed class TextResultWindow : Window
{
    private readonly TextBox _text;
    private readonly TextBlock _status;

    public TextResultWindow(string title, string text, string status, string? link = null)
    {
        Title = title + " – ShotIt";
        Width = 560;
        Height = 420;
        MinWidth = 360;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = AppIcon.Render(64);
        ShowInTaskbar = true;

        var root = new DockPanel { Margin = new Thickness(16) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        if (link is not null)
        {
            // Only http and https links reach this point, and the address is shown before it is opened.
            buttons.Children.Add(Ui.Button("Open link", () =>
            {
                if (CaptureActions.OpenUrl(link)) Close();
            }));
        }

        buttons.Children.Add(Ui.Button("Copy", CopyAll, accent: true));
        var close = Ui.Button("Close", Close);
        close.IsCancel = true;
        close.Margin = new Thickness(0, 4, 0, 4);
        buttons.Children.Add(close);
        root.Children.Add(buttons);

        _status = new TextBlock { Text = status, Opacity = 0.72, Margin = new Thickness(0, 0, 0, 8), TextTrimming = TextTrimming.CharacterEllipsis };
        DockPanel.SetDock(_status, Dock.Top);
        root.Children.Add(_status);

        _text = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontSize = 14,
        };
        root.Children.Add(_text);
        Content = root;

        Loaded += (_, _) =>
        {
            _text.Focus();
            _text.CaretIndex = 0;
        };
        InputBindings.Add(new KeyBinding(new Relay(Close), Key.Escape, ModifierKeys.None));
    }

    /// <summary>The (possibly corrected) text, for tests.</summary>
    public string CurrentText => _text.Text;

    private void CopyAll()
    {
        string text = _text.SelectionLength > 0 ? _text.SelectedText : _text.Text;
        _status.Text = ClipboardService.SetText(text) ? "Copied to the clipboard" : "The clipboard is busy. Try again.";
    }

    private sealed class Relay(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }
}
