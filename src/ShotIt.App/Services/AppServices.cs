using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Serilog.Core;
using ShotIt.App.Editing;
using ShotIt.App.Infrastructure;
using ShotIt.App.Overlay;
using ShotIt.App.Pins;
using ShotIt.App.Views;
using ShotIt.Capture;
using ShotIt.Core.Editing;
using ShotIt.Core.History;
using ShotIt.Core.Settings;
using ShotIt.Core.Upload;
using ShotIt.Rendering;
using ShotIt.Shell;

namespace ShotIt.App.Services;

/// <summary>The composition root: creates every long-lived service once and hands them to each other.</summary>
public sealed class AppServices : IDisposable
{
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _retentionTimer;
    private readonly List<Window> _openWindows = [];
    private bool _disposed;

    /// <param name="paths">Where settings, history and logs live.</param>
    /// <param name="dispatcher">The UI thread's dispatcher.</param>
    /// <param name="shellRoot">Registry root for shell integration; the self-test passes a scratch key so the real one is never touched.</param>
    /// <param name="sendToFolder">Send-to folder override for the self-test.</param>
    public AppServices(AppPaths paths, Dispatcher dispatcher, RegistryKey? shellRoot = null, string? sendToFolder = null)
    {
        Paths = paths;
        Dispatcher = dispatcher;
        Log = Logging.Create(paths);
        Store = new SettingsStore(paths.SettingsFile);
        var loaded = Store.Load();
        Settings = loaded.Settings;
        SettingsOutcome = loaded;
        Log.Information("ShotIt {Version} starting; data in {Root} (portable: {Portable}); settings: {Outcome}",
            typeof(AppServices).Assembly.GetName().Version, paths.DataRoot, paths.IsPortable, loaded.Outcome);

        History = new HistoryRepository(paths.HistoryDatabase);
        CaptureService = new ScreenCaptureService(CaptureBackends.CreateGpuBackends(), message => Log.Information("{Message}", message));
        // The self-test reads policies from its scratch key, so a managed machine cannot change its outcome.
        Policies = shellRoot is null ? AppPolicies.Read() : AppPolicies.Read(shellRoot, shellRoot);
        if (Policies.Any) Log.Information("Administrator policies in effect: {Policies}", Policies);
        Output = new OutputService(Settings, paths, History, Log) { HistoryDisabledByPolicy = Policies.DisableHistory };
        Notifier = new Notifier(Settings);
        MessageWindow = new MessageWindow();
        Hotkeys = new HotkeyManager(MessageWindow);
        Pins = new PinManager(this);
        Actions = new CaptureActions(this);
        Controller = new CaptureController(this);
        Secrets = new DpapiSecretProtector();
        Shell = new ShellIntegration(Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "ShotIt.exe"), shellRoot, sendToFolder);
        PrintScreen = new PrintScreenBinding(shellRoot);
        Updates = new UpdateService(Settings, Log, Policies);
        Features = new FeatureActions(this);
        CaptureService.Preference = Settings.Capture.Backend;

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += (_, _) => SaveSettingsNow();
        _retentionTimer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, dispatcher) { Interval = TimeSpan.FromHours(6) };
        _retentionTimer.Tick += (_, _) => RunRetention();
        _retentionTimer.Start();
    }

    public AppPaths Paths { get; }
    public Dispatcher Dispatcher { get; }
    public Logger Log { get; }
    public SettingsStore Store { get; }
    public AppSettings Settings { get; }
    public SettingsLoadResult SettingsOutcome { get; }
    public HistoryRepository History { get; }
    public ScreenCaptureService CaptureService { get; }
    public OutputService Output { get; }
    public Notifier Notifier { get; }
    public MessageWindow MessageWindow { get; }
    public HotkeyManager Hotkeys { get; }
    public PinManager Pins { get; }
    public CaptureActions Actions { get; }
    public CaptureController Controller { get; }
    public ISecretProtector Secrets { get; }
    public ShellIntegration Shell { get; }
    public PrintScreenBinding PrintScreen { get; }
    public UpdateService Updates { get; }
    public AppPolicies Policies { get; }
    public FeatureActions Features { get; }

    /// <summary>Raised after settings were changed and saved, so open windows can refresh.</summary>
    public event Action? SettingsChanged;

    // ------------------------------------------------------------------ settings

    /// <summary>Saves shortly after the last change; many small edits become one write.</summary>
    public void SaveSettingsSoon()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveSettingsNow()
    {
        _saveTimer.Stop();
        try
        {
            Store.Save(Settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "Saving settings failed");
        }
    }

    /// <summary>Applies settings that have a live effect (theme, hotkeys, capture backend) and saves.</summary>
    public void ApplySettings()
    {
        ApplyTheme();
        Hotkeys.Apply(Settings.Hotkeys);
        CaptureService.Preference = Settings.Capture.Backend;
        SaveSettingsNow();
        SettingsChanged?.Invoke();
    }

    /// <summary>Puts every setting back to its default. History, screenshots and shell integration are left alone.</summary>
    public void ResetSettings()
    {
        var fresh = new AppSettings();
        Settings.General = fresh.General;
        Settings.General.OnboardingDone = true;
        Settings.General.StartWithWindows = Shell.GetStartupState() == StartupState.On;
        Settings.Hotkeys = fresh.Hotkeys;
        Settings.Capture = fresh.Capture;
        Settings.Output = fresh.Output;
        Settings.Editor = fresh.Editor;
        Settings.History = fresh.History;
        Settings.Upload = fresh.Upload;
        Settings.Updates = fresh.Updates;
        Settings.Privacy = fresh.Privacy;
        Settings.Shell = new ShellSettings { ContextMenu = Shell.IsContextMenuInstalled(), SendTo = Shell.IsSendToInstalled() };
        ApplySettings();
    }

    public void ApplyTheme()
    {
        var mode = Settings.General.Theme switch
        {
            AppTheme.Light => ThemeMode.Light,
            AppTheme.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
        if (Application.Current is { } application && application.ThemeMode != mode) application.ThemeMode = mode;
    }

    private void RunRetention()
    {
        // Disk work off the UI thread; the repository serialises access itself.
        Task.Run(() =>
        {
            int removed = Output.ApplyRetention();
            if (removed > 0) Log.Information("Retention removed {Count} old capture(s)", removed);
        });
    }

    public void RunRetentionSoon() => Dispatcher.BeginInvoke(RunRetention, DispatcherPriority.ApplicationIdle);

    // ------------------------------------------------------------------ windows

    private T Track<T>(T window)
        where T : Window
    {
        _openWindows.Add(window);
        window.Closed += (_, _) => _openWindows.Remove(window);
        return window;
    }

    /// <summary>Brings an existing window of the given type to the front, or creates one.</summary>
    public T ShowSingle<T>(Func<T> create)
        where T : Window
    {
        var existing = _openWindows.OfType<T>().FirstOrDefault();
        if (existing is not null)
        {
            if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }

        var window = Track(create());
        window.Show();
        window.Activate();
        return window;
    }

    /// <summary>Shows a secondary window and closes it with the application.</summary>
    public T ShowWindow<T>(T window, Window? owner = null)
        where T : Window
    {
        Track(window);
        if (owner is { IsVisible: true }) window.Owner = owner;
        window.Show();
        window.Activate();
        return window;
    }

    public EditorWindow OpenEditor(CaptureResult result)
    {
        var window = Track(new EditorWindow(this, result));
        window.Show();
        window.Activate();
        return window;
    }

    private static readonly string[] VideoExtensions = [".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm"];

    public static bool IsVideoFile(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Opens a video in the video editor. Reports unreadable files instead of throwing.</summary>
    public async Task<VideoEditorWindow?> OpenVideoEditorAsync(string path)
    {
        try
        {
            var info = await VideoEditor.ProbeAsync(path);
            var window = Track(new VideoEditorWindow(this, path, info));
            window.Show();
            window.Activate();
            return window;
        }
        catch (VideoEditException ex)
        {
            Log.Information("Could not open {Path}: {Message}", path, ex.Message);
            Notifier.Warn("Could not open the video", ex.Message);
            return null;
        }
    }

    public void BrowseAndEditVideo()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open video",
            Filter = "Videos|*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm|All files|*.*",
            InitialDirectory = Directory.Exists(Output.SaveFolder) ? Output.SaveFolder : string.Empty,
        };
        if (dialog.ShowDialog() == true) _ = OpenVideoEditorAsync(dialog.FileName);
    }

    /// <summary>Opens an image file in the editor. Reports unreadable files instead of throwing.</summary>
    public EditorWindow? OpenEditorForFile(string path)
    {
        if (IsVideoFile(path))
        {
            _ = OpenVideoEditorAsync(path);
            return null;
        }

        try
        {
            var image = ImageCodec.DecodeFile(path);
            var result = new CaptureResult(DocState.FromImage(image), sourceTitle: Path.GetFileName(path)) { SavedPath = path };
            return OpenEditor(result);
        }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException)
        {
            Log.Information("Could not open {Path}: {Message}", path, ex.Message);
            Notifier.Warn("Could not open the file", Path.GetFileName(path) + " is not an image ShotIt can read.");
            return null;
        }
    }

    public void BrowseAndEdit()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open image",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp;*.ico|All files|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true) return;
        foreach (string file in dialog.FileNames) OpenEditorForFile(file);
    }

    public void EditClipboard()
    {
        if (ClipboardService.GetImage() is { } image) OpenEditor(new CaptureResult(DocState.FromImage(image)));
        else Notifier.Warn("Nothing to paste", "The clipboard does not contain an image.");
    }

    public void PinClipboard()
    {
        if (ClipboardService.GetImage() is { } image) Pins.Pin(image);
        else Notifier.Warn("Nothing to pin", "The clipboard does not contain an image.");
    }

    public void CloseAllWindows()
    {
        foreach (var window in _openWindows.ToArray())
        {
            try { window.Close(); }
            catch (InvalidOperationException) { }
        }
    }

    public IReadOnlyList<Window> OpenWindows => _openWindows;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _retentionTimer.Stop();
        SaveSettingsNow();
        Controller.Shutdown();
        Pins.SaveSession();
        Pins.CloseAll();
        Hotkeys.Dispose();
        MessageWindow.Dispose();
        History.Dispose();
        Log.Information("ShotIt stopped");
        Log.Dispose();
    }
}
