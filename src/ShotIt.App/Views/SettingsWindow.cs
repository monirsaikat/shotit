using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ShotIt.App.Infrastructure;
using ShotIt.App.Services;
using ShotIt.Core.Imaging;
using ShotIt.Core.Naming;
using ShotIt.Core.Settings;
using ShotIt.Core.Upload;
using ShotIt.Ocr;
using ShotIt.Rendering;
using ShotIt.Shell;
using ShotIt.Upload;

namespace ShotIt.App.Views;

/// <summary>
/// All settings in one window. Every change applies and saves immediately (there is no OK button to
/// forget), and anything that cannot take effect says why next to the control.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static readonly string[] SectionNames = ["General", "Hotkeys", "Capture", "Output", "History", "Upload", "Integration", "About"];

    private readonly AppServices _app;
    private readonly AppSettings _s;
    private readonly ListBox _nav = new();
    private readonly ScrollViewer _host = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(28, 22, 28, 22) };
    private readonly DispatcherTimer _applyTimer;
    private readonly Dictionary<HotkeyAction, TextBlock> _hotkeyStatus = [];
    private bool _recording;

    public SettingsWindow(AppServices app, string? section = null)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _s = app.Settings;
        Title = "ShotIt Settings";
        Width = 860;
        Height = 640;
        MinWidth = 700;
        MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = AppIcon.Render(64);
        UseLayoutRounding = true;

        _applyTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(350) };
        _applyTimer.Tick += (_, _) => ApplyNow();

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        _nav.Margin = new Thickness(10, 16, 4, 16);
        _nav.BorderThickness = new Thickness(0);
        _nav.Background = Brushes.Transparent;
        System.Windows.Automation.AutomationProperties.SetName(_nav, "Settings sections");
        foreach (string name in SectionNames) _nav.Items.Add(new ListBoxItem { Content = name, Padding = new Thickness(12, 8, 12, 8) });
        _nav.SelectionChanged += (_, _) => ShowSection(_nav.SelectedIndex);
        grid.Children.Add(_nav);

        Grid.SetColumn(_host, 1);
        grid.Children.Add(_host);
        Content = grid;

        app.SettingsChanged += OnSettingsApplied;
        Closed += (_, _) =>
        {
            app.SettingsChanged -= OnSettingsApplied;
            if (_applyTimer.IsEnabled || _recording) ApplyNow();
        };

        Select(section);
    }

    /// <summary>Jumps to a section by name; unknown names open the first section.</summary>
    public void Select(string? section)
    {
        int index = section is null ? 0 : Array.FindIndex(SectionNames, n => string.Equals(n, section, StringComparison.OrdinalIgnoreCase));
        _nav.SelectedIndex = Math.Max(0, index);
    }

    public string CurrentSection => SectionNames[Math.Max(0, _nav.SelectedIndex)];

    public static IReadOnlyList<string> Sections => SectionNames;

    // ------------------------------------------------------------------ plumbing

    /// <summary>Applies shortly after the last change so typing does not re-register hotkeys on every key.</summary>
    private void Changed()
    {
        _applyTimer.Stop();
        _applyTimer.Start();
    }

    private void ApplyNow()
    {
        _applyTimer.Stop();
        if (_recording) return;
        _app.ApplySettings();
    }

    private void OnSettingsApplied() => RefreshHotkeyStatus();

    private void ShowSection(int index)
    {
        if (index < 0) return;
        _hotkeyStatus.Clear();
        var panel = new StackPanel { MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left };
        panel.Children.Add(Ui.Heading(SectionNames[index]));
        switch (SectionNames[index])
        {
            case "General": BuildGeneral(panel); break;
            case "Hotkeys": BuildHotkeys(panel); break;
            case "Capture": BuildCapture(panel); break;
            case "Output": BuildOutput(panel); break;
            case "History": BuildHistory(panel); break;
            case "Upload": BuildUpload(panel); break;
            case "Integration": BuildIntegration(panel); break;
            case "About": BuildAbout(panel); break;
        }

        _host.Content = panel;
        _host.ScrollToTop();
    }

    private static IEnumerable<(T, string)> Options<T>(Func<T, string> label)
        where T : struct, Enum => Enum.GetValues<T>().Select(v => (v, label(v)));

    // ------------------------------------------------------------------ General

    private void BuildGeneral(StackPanel p)
    {
        p.Children.Add(Ui.Row("Appearance", Ui.Combo(
            [(AppTheme.System, "Same as Windows"), (AppTheme.Light, "Light"), (AppTheme.Dark, "Dark")],
            _s.General.Theme, v => { _s.General.Theme = v; Changed(); })));

        var startupNote = Ui.Hint(string.Empty);
        void DescribeStartup()
        {
            startupNote.Text = _app.Shell.GetStartupState() switch
            {
                StartupState.DisabledByUser => "Windows has start-up turned off for ShotIt (Task Manager › Startup apps). Tick the box again to turn it back on.",
                StartupState.PointsElsewhere => "Start-up is registered for a different copy of ShotIt. Tick the box to use this copy.",
                StartupState.OnForAllUsers => "ShotIt starts for everyone on this computer. That was set by the installer and only an administrator can change it.",
                _ => "ShotIt waits in the notification area and uses almost no resources until you capture.",
            };
        }

        var startupState = _app.Shell.GetStartupState();
        var startupBox = Ui.Check("Start ShotIt when I sign in to Windows", startupState is StartupState.On or StartupState.OnForAllUsers, on =>
        {
            if (!_app.Shell.SetStartup(on)) _app.Notifier.Warn("Could not change start-up", "Windows refused the change. A policy may be blocking it.");
            _s.General.StartWithWindows = _app.Shell.GetStartupState() == StartupState.On;
            DescribeStartup();
            Changed();
        });
        startupBox.IsEnabled = startupState != StartupState.OnForAllUsers;
        p.Children.Add(startupBox);
        DescribeStartup();
        p.Children.Add(startupNote);

        p.Children.Add(Ui.Check("Show a notification after saving, uploading and similar actions", _s.General.ShowNotifications, v => { _s.General.ShowNotifications = v; Changed(); }));
        p.Children.Add(Ui.Check("Play a sound after a capture", _s.General.PlaySound, v => { _s.General.PlaySound = v; Changed(); }));
        p.Children.Add(Ui.Check("Bring pinned images back after ShotIt restarts", _s.General.RestorePins, v => { _s.General.RestorePins = v; Changed(); }));

        p.Children.Add(Ui.SubHeading("Text recognition"));
        var languages = new List<(string, string)> { (string.Empty, "Automatic (my Windows languages)") };
        languages.AddRange(TextRecognizer.AvailableLanguages().Select(l => (l.Tag, l.DisplayName)));
        p.Children.Add(Ui.Row("Language", Ui.Combo(languages, _s.General.OcrLanguage, v => { _s.General.OcrLanguage = v; Changed(); }),
            languages.Count == 1
                ? "No recognition language is installed. Add one in Windows Settings › Time & language › Language & region."
                : BengaliRecognizer.IsAvailable
                    ? "Text is recognised on this computer. Nothing is sent anywhere. Automatic also finds Bengali (বাংলা), which ShotIt reads itself; "
                      + "choosing one language skips that check and is a little faster. More languages can be added in Windows Settings."
                    : "Text is recognised on this computer by Windows. Nothing is sent anywhere. More languages can be added in Windows Settings."));
    }

    // ------------------------------------------------------------------ Hotkeys

    private void BuildHotkeys(StackPanel p)
    {
        p.Children.Add(Ui.Hint("Click a field and press the keys you want. Backspace removes a hotkey. These work everywhere in Windows while ShotIt is running."));
        var h = _s.Hotkeys;
        AddHotkey(p, "Capture region", HotkeyAction.Region, () => h.Region, v => h.Region = v);
        AddHotkey(p, "Capture window or control", HotkeyAction.Window, () => h.Window, v => h.Window = v);
        AddHotkey(p, "Capture full screen", HotkeyAction.FullScreen, () => h.FullScreen, v => h.FullScreen = v);
        AddHotkey(p, "Capture active window", HotkeyAction.ActiveWindow, () => h.ActiveWindow, v => h.ActiveWindow = v);
        AddHotkey(p, "Repeat last region", HotkeyAction.LastRegion, () => h.LastRegion, v => h.LastRegion = v);
        AddHotkey(p, "Capture freeform shape", HotkeyAction.Freeform, () => h.Freeform, v => h.Freeform = v);
        AddHotkey(p, "Capture after a delay", HotkeyAction.DelayedRegion, () => h.DelayedRegion, v => h.DelayedRegion = v);
        AddHotkey(p, "Scrolling capture", HotkeyAction.Scrolling, () => h.Scrolling, v => h.Scrolling = v);
        AddHotkey(p, "Record video (press again to stop)", HotkeyAction.RecordVideo, () => h.RecordVideo, v => h.RecordVideo = v);
        AddHotkey(p, "Record GIF (press again to stop)", HotkeyAction.RecordGif, () => h.RecordGif, v => h.RecordGif = v);
        AddHotkey(p, "Show or hide pinned images", HotkeyAction.TogglePins, () => h.TogglePins, v => h.TogglePins = v);
        AddHotkey(p, "Pin the clipboard image", HotkeyAction.PinClipboard, () => h.PinClipboard, v => h.PinClipboard = v);
        AddHotkey(p, "Open history", HotkeyAction.OpenHistory, () => h.OpenHistory, v => h.OpenHistory = v);

        p.Children.Add(Ui.Button("Restore default hotkeys", () =>
        {
            _s.Hotkeys = new HotkeySettings();
            ApplyNow();
            ShowSection(_nav.SelectedIndex);
        }));

        AddPrintScreenNotice(p);
        RefreshHotkeyStatus();
    }

    private void AddHotkey(StackPanel p, string label, HotkeyAction action, Func<Hotkey> get, Action<Hotkey> set)
    {
        var box = new HotkeyBox(_app.Hotkeys, get());
        var status = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = Brushes2.Warning };
        box.GotKeyboardFocus += (_, _) => _recording = true;
        box.ValueChanged += v => set(v);
        box.RecordingEnded += () =>
        {
            _recording = false;
            // Always re-register: recording released every hotkey, changed or not.
            ApplyNow();
        };
        _hotkeyStatus[action] = status;
        p.Children.Add(Ui.Row(label, Ui.Horizontal(box, status)));
    }

    private void RefreshHotkeyStatus()
    {
        var failures = _app.Hotkeys.Failures;
        var all = new[]
        {
            (HotkeyAction.Region, _s.Hotkeys.Region), (HotkeyAction.Window, _s.Hotkeys.Window), (HotkeyAction.FullScreen, _s.Hotkeys.FullScreen),
            (HotkeyAction.ActiveWindow, _s.Hotkeys.ActiveWindow), (HotkeyAction.LastRegion, _s.Hotkeys.LastRegion), (HotkeyAction.Freeform, _s.Hotkeys.Freeform),
            (HotkeyAction.DelayedRegion, _s.Hotkeys.DelayedRegion), (HotkeyAction.Scrolling, _s.Hotkeys.Scrolling), (HotkeyAction.RecordVideo, _s.Hotkeys.RecordVideo), (HotkeyAction.RecordGif, _s.Hotkeys.RecordGif), (HotkeyAction.TogglePins, _s.Hotkeys.TogglePins),
            (HotkeyAction.PinClipboard, _s.Hotkeys.PinClipboard), (HotkeyAction.OpenHistory, _s.Hotkeys.OpenHistory),
        };
        foreach (var (action, status) in _hotkeyStatus)
        {
            var failure = failures.FirstOrDefault(f => f.Action == action);
            if (failure.Action != action || failure.Hotkey.IsEmpty)
            {
                status.Text = string.Empty;
                continue;
            }

            bool duplicate = all.Count(a => a.Item2 == failure.Hotkey) > 1;
            status.Text = duplicate ? "Used twice here. Only the first one works." : "Already used by another program.";
        }
    }

    private void AddPrintScreenNotice(StackPanel p)
    {
        if (!_app.PrintScreen.IsSnippingToolBound()) return;
        bool usesPrintScreen = new[] { _s.Hotkeys.Region, _s.Hotkeys.FullScreen, _s.Hotkeys.ActiveWindow, _s.Hotkeys.Window, _s.Hotkeys.LastRegion }
            .Any(k => k.VirtualKey == Hotkey.VkPrintScreen);
        if (!usesPrintScreen) return;

        p.Children.Add(Ui.SubHeading("Print Screen opens Snipping Tool"));
        var note = Ui.Hint("Windows is set to open Snipping Tool when you press Print Screen, so both tools may react. ShotIt can turn that Windows setting off for you. You can turn it back on in Windows Settings › Accessibility › Keyboard.");
        p.Children.Add(note);
        Button? button = null;
        button = Ui.Button("Let ShotIt use Print Screen", () =>
        {
            bool ok = _app.PrintScreen.ReleaseForShotIt();
            note.Text = ok
                ? "Done. If Snipping Tool still opens, sign out of Windows and back in once."
                : "Windows refused the change. Turn off “Use the Print screen key to open screen capture” in Windows Settings › Accessibility › Keyboard.";
            if (ok && button is not null) button.IsEnabled = false;
            _app.Hotkeys.Reapply();
        });
        p.Children.Add(button);
    }

    // ------------------------------------------------------------------ Capture

    private void BuildCapture(StackPanel p)
    {
        var c = _s.Capture;
        p.Children.Add(Ui.Check("Snap the selection to windows, buttons and other controls", c.SnapToWindows, v => { c.SnapToWindows = v; Changed(); }));
        p.Children.Add(Ui.Check("Show the magnifier with pixel colour while selecting", c.ShowMagnifier, v => { c.ShowMagnifier = v; Changed(); }));
        p.Children.Add(Ui.Check("Include the mouse pointer in captures", c.IncludeCursor, v => { c.IncludeCursor = v; Changed(); }));

        p.Children.Add(Ui.SubHeading("After capturing"));
        var actions = Options<AfterCaptureAction>(Ui.Describe).ToList();
        p.Children.Add(Ui.Row("After selecting a region", Ui.Combo(actions, c.AfterRegion, v => { c.AfterRegion = v; Changed(); }),
            "“Show the toolbar” copies the selection and keeps it open for annotation. The other choices finish the capture when you release the mouse."));
        p.Children.Add(Ui.Row("After a full-screen or window capture", Ui.Combo(actions.Where(a => a.Item1 != AfterCaptureAction.ShowToolbar), c.AfterFullScreen, v => { c.AfterFullScreen = v; Changed(); })));
        p.Children.Add(Ui.Row("When I press Enter", Ui.Combo(actions.Where(a => a.Item1 != AfterCaptureAction.ShowToolbar), c.EnterAction, v => { c.EnterAction = v; Changed(); })));

        p.Children.Add(Ui.SubHeading("Sizes and timing"));
        p.Children.Add(Ui.Row("Delay before a delayed capture", Ui.Horizontal(Ui.Number(c.DelaySeconds, 1, 30, v => { c.DelaySeconds = v; Changed(); }), Unit("seconds"))));
        p.Children.Add(Ui.Row("Fixed-size region", Ui.Horizontal(
            Ui.Number(c.FixedWidth, 16, 16384, v => { c.FixedWidth = v; Changed(); }), Unit("×"),
            Ui.Number(c.FixedHeight, 16, 16384, v => { c.FixedHeight = v; Changed(); }), Unit("pixels"))));

        p.Children.Add(Ui.Row("Longest scrolling capture", Ui.Horizontal(Ui.Number(c.ScrollMaxHeight, 1000, 60000, v => { c.ScrollMaxHeight = v; Changed(); }), Unit("pixels")),
            "A scrolling capture stops by itself at the end of the page, or when the picture reaches this height."));

        p.Children.Add(Ui.SubHeading("Recording"));
        var r = _s.Recording;
        p.Children.Add(Ui.Check("Show the mouse pointer in recordings", r.ShowPointer, v => { r.ShowPointer = v; Changed(); }));
        p.Children.Add(Ui.Check("Mark mouse clicks with a ring", r.ShowClicks, v => { r.ShowClicks = v; Changed(); }));
        p.Children.Add(Ui.Check("Show keyboard shortcuts as a caption", r.ShowKeys, v => { r.ShowKeys = v; Changed(); },
            "Only shortcuts such as Ctrl+S and keys such as Enter are shown. Ordinary typing never is, so passwords stay out of the recording."));
        p.Children.Add(Ui.Check("Open the video editor when a video recording finishes", r.EditAfterRecording, v => { r.EditAfterRecording = v; Changed(); }));
        p.Children.Add(Ui.Row("Video smoothness", Ui.Horizontal(Ui.Number(r.VideoFramesPerSecond, 5, 60, v => { r.VideoFramesPerSecond = v; Changed(); }), Unit("pictures a second"))));
        p.Children.Add(Ui.Row("GIF smoothness", Ui.Horizontal(Ui.Number(r.GifFramesPerSecond, 2, 30, v => { r.GifFramesPerSecond = v; Changed(); }), Unit("pictures a second")),
            "A higher number is smoother and makes a larger file."));
        p.Children.Add(Ui.Row("Longest recording", Ui.Horizontal(Ui.Number(r.MaxSeconds, 5, 3600, v => { r.MaxSeconds = v; Changed(); }), Unit("seconds"))));

        p.Children.Add(Ui.SubHeading("Advanced"));
        p.Children.Add(Ui.Row("Capture method", Ui.Combo(
            [
                (CaptureBackendPreference.Auto, "Automatic (recommended)"),
                (CaptureBackendPreference.Gdi, "Classic (GDI)"),
                (CaptureBackendPreference.DesktopDuplication, "Desktop Duplication (games, video)"),
                (CaptureBackendPreference.WindowsGraphicsCapture, "Windows Graphics Capture"),
            ],
            c.Backend, v => { c.Backend = v; Changed(); }),
            "Change this only if captures of a game or a video come out black. If the chosen method fails, ShotIt falls back to the classic one."));
    }

    private static TextBlock Unit(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), Opacity = 0.8 };

    // ------------------------------------------------------------------ Output

    private void BuildOutput(StackPanel p)
    {
        var o = _s.Output;
        var folderBox = new TextBox { Text = _app.Output.SaveFolder, IsReadOnly = true, MinWidth = 260 };
        var folderRow = new DockPanel();
        var browse = Ui.Button("Browse…", () =>
        {
            var dialog = new OpenFolderDialog { Title = "Choose where screenshots are saved", InitialDirectory = Directory.Exists(_app.Output.SaveFolder) ? _app.Output.SaveFolder : string.Empty };
            if (dialog.ShowDialog(this) != true) return;
            o.SaveFolder = dialog.FolderName;
            folderBox.Text = _app.Output.SaveFolder;
            Changed();
        });
        browse.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(browse, Dock.Right);
        folderRow.Children.Add(browse);
        folderRow.Children.Add(folderBox);
        p.Children.Add(Ui.Row("Screenshots folder", folderRow));
        p.Children.Add(Ui.Horizontal(
            Ui.Button("Open folder", () => CaptureActions.OpenFolder(_app.Output.SaveFolder)),
            Ui.Button("Use the default folder", () =>
            {
                o.SaveFolder = string.Empty;
                folderBox.Text = _app.Output.SaveFolder;
                Changed();
            })));

        p.Children.Add(Ui.SubHeading("File names"));
        var preview = Ui.Hint(string.Empty);
        void UpdatePreview()
        {
            string name = FileNameTemplate.Expand(o.FileNameTemplate, new FileNameContext(DateTime.Now, "chrome", "Quarterly report", 1280, 720, 7));
            preview.Text = "Example: " + name + ImageCodec.Extension(o.Format) +
                "\nYou can use {date:yyyy-MM-dd}, {time:HH-mm-ss}, {app}, {title}, {width}, {height} and {n}.";
        }

        p.Children.Add(Ui.Row("Name pattern", Ui.Text(o.FileNameTemplate, v =>
        {
            o.FileNameTemplate = string.IsNullOrWhiteSpace(v) ? new OutputSettings().FileNameTemplate : v;
            UpdatePreview();
            Changed();
        })));
        p.Children.Add(preview);

        var quality = Ui.Number(o.JpegQuality, 10, 100, v => { o.JpegQuality = v; Changed(); });
        var qualityRow = Ui.Row("JPEG quality", Ui.Horizontal(quality, Unit("10 to 100")));
        qualityRow.Visibility = o.Format == ImageFormat.Jpeg ? Visibility.Visible : Visibility.Collapsed;
        p.Children.Add(Ui.Row("File type", Ui.Combo(
            [(ImageFormat.Png, "PNG (sharp, best for screens)"), (ImageFormat.Jpeg, "JPEG (smaller, for photos)"), (ImageFormat.Bmp, "BMP"), (ImageFormat.Pdf, "PDF")],
            o.Format, v =>
            {
                o.Format = v;
                qualityRow.Visibility = v == ImageFormat.Jpeg ? Visibility.Visible : Visibility.Collapsed;
                UpdatePreview();
                Changed();
            })));
        p.Children.Add(qualityRow);
        UpdatePreview();

        p.Children.Add(Ui.SubHeading("Behaviour"));
        p.Children.Add(Ui.Check("Also save every capture to the screenshots folder automatically", o.AutoSaveEveryCapture, v => { o.AutoSaveEveryCapture = v; Changed(); }));
        p.Children.Add(Ui.Check("When copying, also put the file on the clipboard (for pasting into Explorer or chat apps)", o.CopyFileAlongsideImage, v => { o.CopyFileAlongsideImage = v; Changed(); }));
        p.Children.Add(Ui.Check("Ask where to save every time", o.AskWhereToSave, v => { o.AskWhereToSave = v; Changed(); }));
    }

    // ------------------------------------------------------------------ History

    private void BuildHistory(StackPanel p)
    {
        var h = _s.History;
        if (_app.Policies.DisableHistory) p.Children.Add(Ui.Hint("History is turned off by your organisation. Captures are not recorded, whatever is chosen below."));
        p.Children.Add(Ui.Row("Keep in history", Ui.Combo(
            [(HistoryMode.All, "Every capture"), (HistoryMode.SavedOnly, "Only captures I save"), (HistoryMode.Off, "Nothing")],
            h.Mode, v => { h.Mode = v; Changed(); }),
            "History lives only on this computer, in your user profile."));
        p.Children.Add(Ui.Row("Remove captures older than", Ui.Horizontal(Ui.Number(h.MaxAgeDays, 0, 3650, v => { h.MaxAgeDays = v; Changed(); }), Unit("days (0 = never)"))));
        p.Children.Add(Ui.Row("Keep history under", Ui.Horizontal(Ui.Number(h.MaxSizeMb, 0, 1024 * 1024, v => { h.MaxSizeMb = v; Changed(); }), Unit("MB (0 = no limit)"))));
        p.Children.Add(Ui.Hint("Captures marked “Keep” are never removed automatically. Files you saved to your own folders are never deleted by ShotIt."));

        p.Children.Add(Ui.SubHeading("Never record these windows"));
        p.Children.Add(Ui.Hint("Captures of windows whose title matches one of these patterns are not added to history. One pattern per line; * matches anything."));
        p.Children.Add(Ui.Text(string.Join(Environment.NewLine, h.ExcludedTitlePatterns), v =>
        {
            h.ExcludedTitlePatterns = [.. v.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(100)];
            Changed();
        }, multiline: true));

        p.Children.Add(Ui.SubHeading("Housekeeping"));
        var status = Ui.Hint(string.Create(CultureInfo.CurrentCulture, $"{_app.History.Count()} capture(s) in history."));
        p.Children.Add(Ui.Horizontal(
            Ui.Button("Open history", () => WindowsHost.OpenHistory(_app)),
            Ui.Button("Clear history…", () =>
            {
                var answer = MessageBox.Show(this,
                    "Remove every capture from history, except the ones marked “Keep”?\n\nFiles you saved to your own folders stay where they are.",
                    "Clear history", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
                if (answer != MessageBoxResult.OK) return;
                int removed = _app.Output.ClearHistory();
                status.Text = string.Create(CultureInfo.CurrentCulture, $"Removed {removed} capture(s). {_app.History.Count()} left.");
            })));
        p.Children.Add(status);
    }

    // ------------------------------------------------------------------ Upload

    private void BuildUpload(StackPanel p)
    {
        var u = _s.Upload;
        if (_app.Policies.DisableUpload)
        {
            p.Children.Add(Ui.Hint("Uploads are turned off by your organisation. Nothing on this page can send a screenshot anywhere."));
            return;
        }

        p.Children.Add(Ui.Hint("ShotIt never uploads anything by itself. An upload happens only when you choose Upload, and it goes only to the destination you set up here."));
        var details = new StackPanel();
        p.Children.Add(Ui.Row("Upload to", Ui.Combo(
            [
                (UploadProvider.None, "Nowhere (uploads are off)"),
                (UploadProvider.Imgur, "Imgur"),
                (UploadProvider.S3, "Amazon S3 or compatible (R2, MinIO, B2)"),
                (UploadProvider.Sftp, "SFTP server"),
                (UploadProvider.Ftp, "FTP server"),
                (UploadProvider.CustomHttp, "My own web address"),
                (UploadProvider.LocalFolder, "A folder (synced or network drive)"),
            ],
            u.Provider, v =>
            {
                u.Provider = v;
                BuildUploadDetails(details);
                Changed();
            })));
        p.Children.Add(details);
        BuildUploadDetails(details);

        p.Children.Add(Ui.SubHeading("When uploading"));
        p.Children.Add(Ui.Check("Ask me before each upload", u.ConfirmBeforeUpload, v => { u.ConfirmBeforeUpload = v; Changed(); }));
        p.Children.Add(Ui.Check("Copy the link to the clipboard", u.CopyUrlAfterUpload, v => { u.CopyUrlAfterUpload = v; Changed(); }));
        p.Children.Add(Ui.Check("Open the link in my browser", u.OpenUrlAfterUpload, v => { u.OpenUrlAfterUpload = v; Changed(); }));
    }

    private void BuildUploadDetails(StackPanel p)
    {
        var u = _s.Upload;
        p.Children.Clear();
        TextBox Field(string value, Action<string> set) => Ui.Text(value, v => { set(v.Trim()); Changed(); });

        switch (u.Provider)
        {
            case UploadProvider.None:
                return;
            case UploadProvider.Imgur:
                p.Children.Add(Ui.Row("Client ID", Field(u.ImgurClientId, v => u.ImgurClientId = v),
                    "Create a free “anonymous usage” application at api.imgur.com and paste its Client ID here. Uploads are public to anyone who has the link."));
                break;
            case UploadProvider.S3:
                p.Children.Add(Ui.Row("Endpoint", Field(u.S3Endpoint, v => u.S3Endpoint = v), "Leave empty for Amazon S3. For other services paste their https endpoint."));
                p.Children.Add(Ui.Row("Region", Field(u.S3Region, v => u.S3Region = v)));
                p.Children.Add(Ui.Row("Bucket", Field(u.S3Bucket, v => u.S3Bucket = v)));
                p.Children.Add(Ui.Row("Access key", Field(u.S3AccessKey, v => u.S3AccessKey = v)));
                p.Children.Add(Ui.Row("Secret key", Ui.Secret(u.S3SecretKeyProtected.Length > 0, v => { u.S3SecretKeyProtected = _app.Secrets.Protect(v); Changed(); })));
                p.Children.Add(Ui.Row("Folder inside the bucket", Field(u.S3PathPrefix, v => u.S3PathPrefix = v)));
                p.Children.Add(Ui.Row("Public address", Field(u.S3PublicUrlBase, v => u.S3PublicUrlBase = v), "The web address that serves the bucket, for example https://img.example.com. The file name is added to it."));
                p.Children.Add(Ui.Check("Path-style addresses (needed by MinIO and most compatible services)", u.S3PathStyle, v => { u.S3PathStyle = v; Changed(); }));
                break;
            case UploadProvider.Ftp:
            case UploadProvider.Sftp:
                bool sftp = u.Provider == UploadProvider.Sftp;
                p.Children.Add(Ui.Row("Server", Field(u.FtpHost, v => u.FtpHost = v)));
                p.Children.Add(Ui.Row("Port", Ui.Horizontal(Ui.Number(u.FtpPort, 0, 65535, v => { u.FtpPort = v; Changed(); }), Unit(sftp ? "0 = default (22)" : "0 = default (21)"))));
                p.Children.Add(Ui.Row("User name", Field(u.FtpUser, v => u.FtpUser = v)));
                p.Children.Add(Ui.Row("Password", Ui.Secret(u.FtpPasswordProtected.Length > 0, v => { u.FtpPasswordProtected = _app.Secrets.Protect(v); Changed(); })));
                p.Children.Add(Ui.Row("Folder on the server", Field(u.FtpRemoteFolder, v => u.FtpRemoteFolder = v)));
                p.Children.Add(Ui.Row("Public address", Field(u.FtpPublicUrlBase, v => u.FtpPublicUrlBase = v), "The web address where that folder can be reached, for example https://example.com/shots."));
                if (!sftp) p.Children.Add(Ui.Hint("FTP is encrypted only when the server offers TLS. Prefer SFTP when you can."));
                break;
            case UploadProvider.CustomHttp:
                p.Children.Add(Ui.Row("Upload address", Field(u.CustomUrl, v => u.CustomUrl = v), "The image is sent as a multipart form POST. The address must start with https://."));
                p.Children.Add(Ui.Row("Form field name", Field(u.CustomFileField, v => u.CustomFileField = v)));
                p.Children.Add(Ui.Row("Extra headers", Ui.Text(u.CustomHeaders, v => { u.CustomHeaders = v; Changed(); }, multiline: true), "One per line, for example  Authorization: Bearer abc123"));
                p.Children.Add(Ui.Row("Where the link is in the reply", Field(u.CustomResponseUrlPath, v => u.CustomResponseUrlPath = v), "For a JSON reply, the path to the link, such as url or data.link. Leave empty when the reply is just the link."));
                p.Children.Add(Ui.Row("Where the delete link is", Field(u.CustomResponseDeletePath, v => u.CustomResponseDeletePath = v)));
                break;
            case UploadProvider.LocalFolder:
                p.Children.Add(Ui.Row("Folder", Field(u.LocalFolder, v => u.LocalFolder = v)));
                p.Children.Add(Ui.Button("Browse…", () =>
                {
                    var dialog = new OpenFolderDialog { Title = "Choose the upload folder" };
                    if (dialog.ShowDialog(this) != true) return;
                    u.LocalFolder = dialog.FolderName;
                    BuildUploadDetails(p);
                    Changed();
                }));
                p.Children.Add(Ui.Row("Public address", Field(u.LocalPublicUrlBase, v => u.LocalPublicUrlBase = v), "Optional. If the folder is published on the web, its address. Otherwise the file path is copied."));
                break;
        }

        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4) };
        Button? test = null;
        test = Ui.Button("Send a test image", async () =>
        {
            if (test is null) return;
            test.IsEnabled = false;
            result.Foreground = Foreground;
            result.Text = "Uploading a small test image…";
            var (ok, message) = await TestUploadAsync();
            result.Foreground = ok ? Brushes2.Success : Brushes2.Warning;
            result.Text = message;
            test.IsEnabled = true;
        });
        p.Children.Add(test);
        p.Children.Add(result);
    }

    /// <summary>Uploads a tiny generated picture with the current settings and reports the outcome in words.</summary>
    internal async Task<(bool Ok, string Message)> TestUploadAsync()
    {
        if (_app.Policies.DisableUpload) return (false, "Uploads are turned off by your organisation.");
        try
        {
            var uploader = UploaderFactory.Create(_s.Upload, _app.Secrets);
            var image = new PixelBuffer(64, 64);
            image.Fill(new ColorRgba(0x3D, 0x7B, 0xFF));
            var request = new UploadRequest(ImageCodec.Encode(image, ImageFormat.Png), "shotit-test.png", "image/png");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var uploaded = await Task.Run(() => uploader.UploadAsync(request, null, timeout.Token), timeout.Token);
            return (true, "It works. The test image is at " + uploaded.Url);
        }
        catch (UploadException ex)
        {
            return (false, ex.Message);
        }
        catch (OperationCanceledException)
        {
            return (false, "The server did not answer within a minute.");
        }
    }

    // ------------------------------------------------------------------ Integration

    private void BuildIntegration(StackPanel p)
    {
        p.Children.Add(Ui.Hint("These add ShotIt to Windows for your account only. No administrator rights are needed, and uninstalling ShotIt removes them."));
        p.Children.Add(Ui.Check("Add “Edit with ShotIt” and “Pin to screen” to the right-click menu of image files", _app.Shell.IsContextMenuInstalled(), on =>
        {
            if (!_app.Shell.SetContextMenu(on)) _app.Notifier.Warn("Could not change the menu", "Windows refused the change.");
            _s.Shell.ContextMenu = _app.Shell.IsContextMenuInstalled();
            Native.AppIdentity.NotifyAssociationsChanged();
            Changed();
        }));
        p.Children.Add(Ui.Hint("On Windows 11 the entries appear under “Show more options”."));
        p.Children.Add(Ui.Check("Add ShotIt to the “Send to” menu", _app.Shell.IsSendToInstalled(), on =>
        {
            if (!_app.Shell.SetSendTo(on)) _app.Notifier.Warn("Could not change “Send to”", "Windows refused the change.");
            _s.Shell.SendTo = _app.Shell.IsSendToInstalled();
            Changed();
        }));

        p.Children.Add(Ui.SubHeading("Command line"));
        p.Children.Add(Ui.Hint("ShotIt.exe --capture region | window | fullscreen | active | freeform | delayed\nShotIt.exe --edit \"file.png\"      ShotIt.exe --pin \"file.png\"\nShotIt.exe --history      ShotIt.exe --settings      ShotIt.exe --quit"));
    }

    // ------------------------------------------------------------------ About

    private void BuildAbout(StackPanel p)
    {
        var version = typeof(SettingsWindow).Assembly.GetName().Version;
        p.Children.Add(new TextBlock { Text = "ShotIt " + (version is null ? string.Empty : version.ToString(3)), FontSize = 16, Margin = new Thickness(0, 0, 0, 4) });
        p.Children.Add(Ui.Hint("Screenshots, annotation and sharing for Windows.\nNo account, no telemetry, no advertising. Nothing leaves this computer unless you upload it."));

        p.Children.Add(Ui.SubHeading("Updates"));
        var updateStatus = Ui.Hint(_app.Updates.Describe());
        p.Children.Add(Ui.Check("Check for updates automatically", _s.Updates.AutoCheck, v => { _s.Updates.AutoCheck = v; Changed(); }));
        Button? check = null;
        check = Ui.Button("Check for updates now", async () =>
        {
            if (check is null) return;
            check.IsEnabled = false;
            updateStatus.Text = "Checking…";
            updateStatus.Text = await _app.Updates.CheckAndDownloadAsync(userInitiated: true);
            check.IsEnabled = true;
        });
        check.IsEnabled = _app.Updates.IsAvailable;
        p.Children.Add(check);
        p.Children.Add(updateStatus);

        p.Children.Add(Ui.SubHeading("Where ShotIt keeps its data"));
        p.Children.Add(Ui.Hint(_app.Paths.DataRoot + (_app.Paths.IsPortable ? "\n(portable mode: everything stays next to the program)" : string.Empty)));
        p.Children.Add(Ui.Horizontal(
            Ui.Button("Open data folder", () => CaptureActions.OpenFolder(_app.Paths.DataRoot)),
            Ui.Button("Open log folder", () => CaptureActions.OpenFolder(_app.Paths.LogDirectory))));

        p.Children.Add(Ui.SubHeading("Reset"));
        p.Children.Add(Ui.Button("Restore all settings to their defaults…", () =>
        {
            var answer = MessageBox.Show(this, "Put every setting back to its default?\n\nYour history and saved screenshots are not touched. Stored upload passwords are removed.",
                "Restore defaults", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer != MessageBoxResult.OK) return;
            _app.ResetSettings();
            ShowSection(_nav.SelectedIndex);
        }));
    }
}
