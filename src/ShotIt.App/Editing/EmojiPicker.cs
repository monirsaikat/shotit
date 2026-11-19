using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ShotIt.Rendering;

namespace ShotIt.App.Editing;

/// <summary>
/// A searchable grid of colour emoji, shown in the tool palette's popup. Picking one makes it the sticker
/// to place next. Recently used emoji are listed first for the rest of the session.
/// </summary>
public sealed class EmojiPicker : StackPanel
{
    private const int MaxRecent = 16;
    private static readonly List<string> Recent = [];

    private readonly Action<string> _picked;
    private readonly TextBox _search;
    private readonly WrapPanel _grid = new() { Width = 9 * 34 };
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 6) };
    private int _category = -1;

    public EmojiPicker(Action<string> picked)
    {
        _picked = picked;
        Width = 9 * 34 + 20;

        _search = new TextBox { Margin = new Thickness(0, 0, 0, 2), VerticalContentAlignment = VerticalAlignment.Center, Height = 28, ToolTip = "Search emoji, for example “thumbs” or “check”" };
        var hint = new TextBlock
        {
            Text = "Search emoji…",
            IsHitTestVisible = false,
            Margin = new Thickness(8, 0, 0, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Style = (Style)Application.Current.FindResource("BarLabel"),
            Opacity = 0.6,
        };
        _search.TextChanged += (_, _) =>
        {
            hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Show(_search.Text.Length == 0 ? _category : -2);
        };
        var searchHost = new Grid();
        searchHost.Children.Add(_search);
        searchHost.Children.Add(hint);
        Children.Add(searchHost);

        if (Recent.Count > 0) _tabs.Children.Add(Tab("🕘", "Recently used", -1));
        for (int i = 0; i < EmojiCatalog.Categories.Count; i++) _tabs.Children.Add(Tab(EmojiCatalog.Categories[i].Icon, EmojiCatalog.Categories[i].Name, i));
        Children.Add(_tabs);

        _scroll = new ScrollViewer { Height = 6 * 34 + 4, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = _grid };
        Children.Add(_scroll);

        // Keys typed here must not reach the overlay or editor, where letters choose tools and Esc cancels the capture.
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && _grid.Children.Count > 0 && _grid.Children[0] is Button { Tag: string first })
            {
                Pick(first);
                e.Handled = true;
            }
        };
        KeyDown += (_, e) => e.Handled = e.Key != Key.Escape;
        Loaded += (_, _) => Dispatcher.BeginInvoke(() => Keyboard.Focus(_search), System.Windows.Threading.DispatcherPriority.Input);

        Show(Recent.Count > 0 ? -1 : 0);
    }

    private Button Tab(string emoji, string name, int index)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.FindResource("BarButton"),
            Width = 32,
            Height = 30,
            ToolTip = name,
            Content = Glyph(emoji, 20),
            Focusable = false,
        };
        button.Click += (_, _) =>
        {
            _search.Text = string.Empty;
            Show(index);
        };
        return button;
    }

    /// <summary>Fills the grid: -2 shows search results, -1 recently used, otherwise a category.</summary>
    private void Show(int category)
    {
        if (category != -2) _category = category;
        IEnumerable<string> items = category switch
        {
            -2 => EmojiCatalog.Search(_search.Text).Select(e => e.Emoji),
            -1 => Recent,
            _ => EmojiCatalog.Categories[Math.Clamp(category, 0, EmojiCatalog.Categories.Count - 1)].Items.Select(e => e.Emoji),
        };

        _grid.Children.Clear();
        foreach (string emoji in items.Take(400))
        {
            var button = new Button
            {
                Style = (Style)Application.Current.FindResource("BarButton"),
                Width = 34,
                Height = 34,
                Tag = emoji,
                Content = Glyph(emoji, 24),
                Focusable = false,
                ToolTip = EmojiCatalog.All.FirstOrDefault(e => e.Emoji == emoji)?.Keywords,
            };
            button.Click += (_, _) => Pick(emoji);
            _grid.Children.Add(button);
        }

        if (_grid.Children.Count == 0)
            _grid.Children.Add(new TextBlock { Text = "No emoji found", Margin = new Thickness(6), Style = (Style)Application.Current.FindResource("BarLabel") });
        _scroll.ScrollToTop();

        for (int i = 0; i < _tabs.Children.Count; i++)
        {
            int index = Recent.Count > 0 ? i - 1 : i;
            if (_tabs.Children[i] is Button tab) tab.Opacity = category == -2 || index == _category ? 1 : 0.55;
        }
    }

    private void Pick(string emoji)
    {
        Recent.Remove(emoji);
        Recent.Insert(0, emoji);
        if (Recent.Count > MaxRecent) Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
        _picked(emoji);
    }

    /// <summary>A colour emoji image, falling back to WPF text when DirectWrite is unavailable.</summary>
    internal static FrameworkElement Glyph(string emoji, double size)
    {
        if (EmojiRenderer.Get(emoji, size * 2) is { } bitmap)
        {
            var image = new Image { Source = bitmap, Width = size, Height = size };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }

        return new TextBlock { Text = emoji, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = size * 0.8, Foreground = Brushes.White };
    }
}
