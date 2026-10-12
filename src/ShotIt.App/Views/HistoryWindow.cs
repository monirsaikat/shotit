using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShotIt.App.Infrastructure;
using ShotIt.App.Services;
using ShotIt.Core.History;
using ShotIt.Rendering;

namespace ShotIt.App.Views;

/// <summary>The library of past captures: search, re-open in the editor, copy, pin, reveal, delete.</summary>
public sealed class HistoryWindow : Window
{
    private const int PageSize = 120;

    private readonly AppServices _app;
    private readonly TextBox _search = new() { Width = 260, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0), Opacity = 0.75 };
    private readonly ListBox _list = new();
    private readonly TextBlock _empty = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.7, FontSize = 15, TextAlignment = TextAlignment.Center };
    private readonly Button _more;
    private readonly DispatcherTimer _searchTimer;
    private readonly DispatcherTimer _refreshTimer;
    private int _loaded;
    private bool _closed;

    public HistoryWindow(AppServices app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        Title = "ShotIt History";
        Width = 980;
        Height = 680;
        MinWidth = 520;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = AppIcon.Render(64);
        UseLayoutRounding = true;

        var root = new DockPanel();

        var top = new DockPanel { Margin = new Thickness(16, 14, 16, 10) };
        var searchLabel = new TextBlock { Text = "Search", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        top.Children.Add(searchLabel);
        _search.ToolTip = "Search window titles, apps, file names, links and recognised text (Ctrl+F)";
        System.Windows.Automation.AutomationProperties.SetName(_search, "Search history");
        top.Children.Add(_search);
        var folder = Ui.Button("Open screenshots folder", () => CaptureActions.OpenFolder(_app.Output.SaveFolder));
        folder.Margin = new Thickness(0);
        DockPanel.SetDock(folder, Dock.Right);
        top.Children.Add(folder);
        top.Children.Add(_summary);
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);

        _more = Ui.Button("Show more", () => Load(append: true));
        _more.HorizontalAlignment = HorizontalAlignment.Center;
        _more.Margin = new Thickness(0, 6, 0, 12);
        _more.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(_more, Dock.Bottom);
        root.Children.Add(_more);

        _list.SelectionMode = SelectionMode.Extended;
        _list.BorderThickness = new Thickness(0);
        _list.Background = Brushes.Transparent;
        _list.Padding = new Thickness(10, 0, 10, 0);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        var panelFactory = new FrameworkElementFactory(typeof(WrapPanel));
        _list.ItemsPanel = new ItemsPanelTemplate(panelFactory);
        System.Windows.Automation.AutomationProperties.SetName(_list, "Captures");
        _list.MouseDoubleClick += (_, e) =>
        {
            if (ItemAt(e.OriginalSource) is not null) OpenSelected();
        };
        _list.KeyDown += OnListKey;
        _list.ContextMenuOpening += (_, e) =>
        {
            var menu = BuildMenu();
            if (menu is null) e.Handled = true;
            else _list.ContextMenu = menu;
        };
        _list.ContextMenu = new ContextMenu();

        var body = new Grid();
        body.Children.Add(_list);
        body.Children.Add(_empty);
        root.Children.Add(body);
        Content = root;

        _searchTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            Load(append: false);
        };
        _search.TextChanged += (_, _) =>
        {
            _searchTimer.Stop();
            _searchTimer.Start();
        };

        // Captures made while the window is open appear without a manual refresh.
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            Load(append: false, keepCount: true);
        };
        app.Output.HistoryChanged += OnHistoryChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            app.Output.HistoryChanged -= OnHistoryChanged;
            _searchTimer.Stop();
            _refreshTimer.Stop();
        };

        InputBindings.Add(new KeyBinding(new Relay(() => { _search.Focus(); _search.SelectAll(); }), Key.F, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new Relay(() =>
        {
            if (_search.Text.Length > 0) _search.Clear();
            else Close();
        }), Key.Escape, ModifierKeys.None));

        Load(append: false);
        Loaded += (_, _) => _search.Focus();
    }

    /// <summary>The entries currently shown, for tests.</summary>
    public IReadOnlyList<HistoryItem> ShownItems => [.. _list.Items.OfType<ListBoxItem>().Select(i => (HistoryItem)i.Tag)];

    public string SearchText
    {
        get => _search.Text;
        set
        {
            _search.Text = value;
            _searchTimer.Stop();
            Load(append: false);
        }
    }

    private void OnHistoryChanged()
    {
        // Raised from whichever thread finished the work.
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;
            _refreshTimer.Stop();
            _refreshTimer.Start();
        });
    }

    // ------------------------------------------------------------------ loading

    private void Load(bool append, bool keepCount = false)
    {
        if (_closed) return;
        string search = _search.Text.Trim();
        int offset = append ? _loaded : 0;
        int take = keepCount ? Math.Max(PageSize, _loaded) : PageSize;
        var selected = keepCount ? SelectedItems().Select(i => i.Id).ToHashSet() : [];

        IReadOnlyList<HistoryItem> items;
        try
        {
            // One extra row tells us whether there is another page.
            items = _app.History.Query(search.Length > 0 ? search : null, take + 1, offset);
        }
        catch (Exception ex)
        {
            _app.Log.Warning(ex, "Reading history failed");
            _empty.Text = "History could not be read. Details are in the log file.";
            _empty.Visibility = Visibility.Visible;
            return;
        }

        bool hasMore = items.Count > take;
        if (!append) _list.Items.Clear();
        foreach (var item in items.Take(take))
        {
            var container = BuildItem(item);
            _list.Items.Add(container);
            if (selected.Contains(item.Id)) container.IsSelected = true;
        }

        _loaded = _list.Items.Count;
        _more.Visibility = hasMore ? Visibility.Visible : Visibility.Collapsed;

        int total = SafeCount();
        _summary.Text = search.Length > 0
            ? string.Create(CultureInfo.CurrentCulture, $"{_loaded}{(hasMore ? "+" : string.Empty)} match(es) of {total}")
            : string.Create(CultureInfo.CurrentCulture, $"{total} capture(s)");
        _empty.Visibility = _loaded == 0 ? Visibility.Visible : Visibility.Collapsed;
        _empty.Text = search.Length > 0
            ? "Nothing matches “" + search + "”."
            : _app.Settings.History.Mode == Core.Settings.HistoryMode.Off
                ? "History is turned off.\nTurn it on in Settings › History."
                : "No captures yet.\nPress " + (_app.Settings.Hotkeys.Region.IsEmpty ? "the tray icon" : _app.Settings.Hotkeys.Region.ToString()) + " to take one.";
    }

    private int SafeCount()
    {
        try { return _app.History.Count(); }
        catch (Exception) { return _loaded; }
    }

    private static BitmapImage? DecodeThumbnail(byte[]? data)
    {
        if (data is null || data.Length == 0) return null;
        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(data);
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    internal static string Describe(HistoryItem item)
    {
        var local = item.CreatedUtc.ToLocalTime();
        string when = local.Date == DateTime.Today
            ? "Today " + local.ToString("t", CultureInfo.CurrentCulture)
            : local.Date == DateTime.Today.AddDays(-1)
                ? "Yesterday " + local.ToString("t", CultureInfo.CurrentCulture)
                : local.ToString("g", CultureInfo.CurrentCulture);
        return string.Create(CultureInfo.CurrentCulture, $"{when} · {item.Width} × {item.Height}");
    }

    private ListBoxItem BuildItem(HistoryItem item)
    {
        var stack = new StackPanel { Width = 208 };
        var frame = new Border { Height = 132, Background = new SolidColorBrush(Color.FromArgb(0x22, 0x80, 0x80, 0x80)), CornerRadius = new CornerRadius(4) };
        var grid = new Grid();
        if (DecodeThumbnail(item.Thumbnail) is { } thumbnail)
        {
            grid.Children.Add(new Image { Source = thumbnail, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Margin = new Thickness(3) });
        }

        var badges = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4) };
        void Badge(string text, string tip) => badges.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x22, 0x26)),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(5, 1, 5, 2),
            Margin = new Thickness(3, 0, 0, 0),
            ToolTip = tip,
            Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 11 },
        });
        if (item.Keep) Badge("Keep", "Never removed automatically");
        if (item.UploadUrl.Length > 0) Badge("Link", item.UploadUrl);
        if (File.Exists(_app.Output.ProjectPath(item.Id))) Badge("Editable", "Annotations can still be changed");
        grid.Children.Add(badges);
        frame.Child = grid;
        stack.Children.Add(frame);

        string title = item.SourceTitle.Length > 0 ? item.SourceTitle : item.SourceApp.Length > 0 ? item.SourceApp : Path.GetFileName(item.FilePath);
        stack.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 6, 2, 0) });
        stack.Children.Add(new TextBlock { Text = Describe(item), Opacity = 0.7, FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 1, 2, 0) });

        var container = new ListBoxItem { Content = stack, Tag = item, Padding = new Thickness(8), Margin = new Thickness(3), ToolTip = item.FilePath };
        System.Windows.Automation.AutomationProperties.SetName(container, title + ", " + Describe(item));
        return container;
    }

    // ------------------------------------------------------------------ actions

    private static HistoryItem? ItemAt(object source)
    {
        for (var d = source as DependencyObject; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ListBoxItem { Tag: HistoryItem item }) return item;
        return null;
    }

    private List<HistoryItem> SelectedItems() => [.. _list.SelectedItems.OfType<ListBoxItem>().Select(i => (HistoryItem)i.Tag)];

    private void OnListKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OpenSelected();
        else if (e.Key == Key.Delete) DeleteSelected();
        else if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) CopySelected();
        else return;
        e.Handled = true;
    }

    private ContextMenu? BuildMenu()
    {
        var items = SelectedItems();
        if (items.Count == 0) return null;
        var first = items[0];
        bool single = items.Count == 1;
        var menu = new ContextMenu();
        void Add(string header, Action action, bool enabled = true)
        {
            var entry = new MenuItem { Header = header, IsEnabled = enabled };
            entry.Click += (_, _) => action();
            menu.Items.Add(entry);
        }

        Add(single ? "Open in editor" : string.Create(CultureInfo.CurrentCulture, $"Open {items.Count} in editor"), OpenSelected);
        Add("Copy image", CopySelected, single);
        Add("Pin to screen", PinSelected);
        menu.Items.Add(new Separator());
        Add("Copy link", () => ClipboardService.SetText(first.UploadUrl), single && first.UploadUrl.Length > 0);
        Add("Copy recognised text", () => ClipboardService.SetText(first.OcrText), single && first.OcrText.Length > 0);
        Add("Copy file path", () => ClipboardService.SetText(first.FilePath), single);
        Add("Show in folder", () => CaptureActions.RevealInExplorer(first.FilePath), single);
        menu.Items.Add(new Separator());
        bool allKept = items.All(i => i.Keep);
        Add(allKept ? "Allow automatic removal" : "Keep (never remove automatically)", () =>
        {
            foreach (var item in items) _app.Output.SetKeep(item, !allKept);
        });
        Add(single ? "Delete" : string.Create(CultureInfo.CurrentCulture, $"Delete {items.Count} captures"), DeleteSelected);
        return menu;
    }

    private void OpenSelected()
    {
        var items = SelectedItems();
        if (items.Count > 8 &&
            MessageBox.Show(this, string.Create(CultureInfo.CurrentCulture, $"Open {items.Count} editor windows?"), "ShotIt", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        foreach (var item in items)
        {
            var result = _app.Output.OpenHistoryItem(item, out string? error);
            if (result is null) ReportMissing(item, error);
            else _app.OpenEditor(result);
        }
    }

    private void ReportMissing(HistoryItem item, string? error)
    {
        var answer = MessageBox.Show(this, (error ?? "The capture could not be opened.") + "\n\n" + item.FilePath + "\n\nRemove it from history?",
            "ShotIt", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) _app.Output.DeleteHistoryItems([item]);
    }

    private void CopySelected()
    {
        if (SelectedItems() is not [var item]) return;
        var result = _app.Output.OpenHistoryItem(item, out string? error);
        if (result is null) ReportMissing(item, error);
        else if (!ClipboardService.SetImage(result.Image, File.Exists(item.FilePath) ? item.FilePath : null))
            _app.Notifier.Warn("Could not copy", "Another program is holding the clipboard. Try again in a moment.");
    }

    private void PinSelected()
    {
        foreach (var item in SelectedItems().Take(12))
        {
            var result = _app.Output.OpenHistoryItem(item, out string? error);
            if (result is null) ReportMissing(item, error);
            else _app.Pins.Pin(result.Image);
        }
    }

    /// <summary>Tests replace this to answer the delete prompt without a dialog.</summary>
    internal Func<int, bool>? ConfirmDelete { get; set; }

    internal void DeleteSelected()
    {
        var items = SelectedItems();
        if (items.Count == 0) return;
        bool confirmed = ConfirmDelete?.Invoke(items.Count) ?? MessageBox.Show(this,
            (items.Count == 1 ? "Delete this capture from history?" : string.Create(CultureInfo.CurrentCulture, $"Delete {items.Count} captures from history?")) +
            "\n\nFiles you saved to your own folders stay where they are.",
            "Delete", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.OK) == MessageBoxResult.OK;
        if (!confirmed) return;
        _app.Output.DeleteHistoryItems(items);
        _refreshTimer.Stop();
        Load(append: false, keepCount: true);
    }

    internal void SelectAll() => _list.SelectAll();

    private sealed class Relay(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => action();
    }
}
