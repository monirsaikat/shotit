using System.Windows;
using System.Windows.Threading;
using ShotIt.App.Infrastructure;
using ShotIt.App.Overlay;
using ShotIt.App.Services;
using ShotIt.App.Views;
using ShotIt.Core.Settings;
using ShotIt.Native;
using ShotIt.Rendering;

namespace ShotIt.App;

public partial class App : Application
{
    private SingleInstance? _instance;
    private AppServices? _services;
    private TrayController? _tray;
    private DateTime _lastErrorNotice = DateTime.MinValue;
    private bool _exiting;

    public AppServices? Services => _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var command = AppCommandLine.Parse(e.Args);

        if (command.Command == AppCommand.SelfTest)
        {
            RunSelfTest(command);
            return;
        }

        _instance = new SingleInstance();
        if (!_instance.IsFirst)
        {
            // Another ShotIt is already running in this session: hand over the request and leave.
            _instance.SendToFirst(e.Args);
            _instance.Dispose();
            _instance = null;
            Shutdown(0);
            return;
        }

        if (command.Command == AppCommand.Quit)
        {
            Shutdown(0);
            return;
        }

        try
        {
            Initialize(new AppPaths());
        }
        catch (Exception ex)
        {
            MessageBox.Show("ShotIt could not start.\n\n" + ex.Message, "ShotIt", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var services = _services!;
        _tray = new TrayController(services, OpenSettings, OpenHistory, ExitApplication);
        _tray.Show();
        services.Hotkeys.Apply(services.Settings.Hotkeys);
        _tray.ReportHotkeyFailures();
        services.SettingsChanged += () =>
        {
            _tray?.RefreshTooltip();
            _tray?.ReportHotkeyFailures();
        };

        _instance.Listen(args => Dispatcher.BeginInvoke(() => Execute(AppCommandLine.Parse(args))));

        if (services.SettingsOutcome.Outcome == SettingsLoadOutcome.RecoveredFromCorruption)
            services.Notifier.Warn("Settings were reset", "The settings file was damaged. Defaults are in use and a copy of the old file was kept.");
        if (services.Paths.PortableFallback)
            services.Notifier.Warn("Portable folder is read-only", "Settings and history are stored in your user profile instead.");

        if (command.Command != AppCommand.None) Execute(command);
        else if (!services.Settings.General.OnboardingDone && !command.Silent) ShowOnboarding();

        Dispatcher.BeginInvoke(services.Controller.Prewarm, DispatcherPriority.ApplicationIdle);
        services.RunRetentionSoon();
        StartUpdateChecks(services);
    }

    /// <summary>Creates the services and installs the global safety nets. Shared with the self-test.</summary>
    internal AppServices Initialize(AppPaths paths, Microsoft.Win32.RegistryKey? shellRoot = null, string? sendToFolder = null)
    {
        AppIdentity.Apply();
        AppIdentity.AllowDarkMenus();
        WpfTextMeasurer.Install();
        var services = new AppServices(paths, Dispatcher, shellRoot, sendToFolder);
        _services = services;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            services.Log.Error(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                services.Log.Fatal(ex, "Unhandled exception");
                Logging.WriteCrashReport(paths, ex, "AppDomain");
            }
        };
        SessionEnding += (_, _) => services.SaveSettingsNow();

        services.ApplyTheme();
        return services;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // The application lives in the tray; one failed operation must not take it down.
        e.Handled = true;
        var services = _services;
        if (services is null) return;
        services.Log.Error(e.Exception, "Unhandled UI exception");
        Logging.WriteCrashReport(services.Paths, e.Exception, "Dispatcher");
        try
        {
            // Whatever happened, never leave the screen covered by a dead overlay.
            services.Controller.CancelSession("unhandled exception");
        }
        catch (Exception ex)
        {
            services.Log.Error(ex, "Cancelling the session after an error failed");
        }

        if (DateTime.UtcNow - _lastErrorNotice > TimeSpan.FromMinutes(1))
        {
            _lastErrorNotice = DateTime.UtcNow;
            services.Notifier.Warn("ShotIt hit a problem", "The last action failed, but ShotIt is still running. A report was saved in the crash folder.");
        }
    }

    /// <summary>Runs a command from this process's command line or forwarded by a second instance.</summary>
    private void Execute(AppCommandLine command)
    {
        var services = _services;
        if (services is null || _exiting) return;
        try
        {
            switch (command.Command)
            {
                case AppCommand.CaptureRegion: services.Controller.StartRegion(); break;
                case AppCommand.CaptureWindow: services.Controller.StartRegion(OverlayMode.Window); break;
                case AppCommand.CaptureFreeform: services.Controller.StartRegion(OverlayMode.Freeform); break;
                case AppCommand.CaptureFullScreen: services.Controller.CaptureFullScreen(allMonitors: false); break;
                case AppCommand.CaptureActiveWindow: services.Controller.CaptureActiveWindow(); break;
                case AppCommand.CaptureDelayed: services.Controller.StartDelayed(); break;
                case AppCommand.EditFile:
                    if (command.Argument is { Length: > 0 } file) services.OpenEditorForFile(file);
                    else services.BrowseAndEdit();
                    break;
                case AppCommand.PinFile:
                    if (command.Argument is { Length: > 0 } pinFile)
                    {
                        try { services.Pins.Pin(ImageCodec.DecodeFile(pinFile)); }
                        catch (Exception ex) when (ex is NotSupportedException or System.IO.IOException or UnauthorizedAccessException)
                        {
                            services.Notifier.Warn("Could not open the file", "That file is not an image ShotIt can read.");
                        }
                    }

                    break;
                case AppCommand.OpenSettings: OpenSettings(); break;
                case AppCommand.OpenHistory: OpenHistory(); break;
                case AppCommand.Quit: ExitApplication(); break;
                case AppCommand.None:
                    // Launched again without arguments: show that ShotIt is alive.
                    if (!command.Silent) OpenSettings();
                    break;
            }
        }
        catch (Exception ex)
        {
            services.Log.Error(ex, "Command {Command} failed", command.Command);
        }
    }

    /// <summary>First check a minute after start-up (never during it), then once a day while ShotIt keeps running.</summary>
    private void StartUpdateChecks(AppServices services)
    {
        var timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, Dispatcher) { Interval = TimeSpan.FromMinutes(1) };
        timer.Tick += async (_, _) =>
        {
            timer.Interval = TimeSpan.FromHours(24);
            if (_exiting || !services.Updates.AutoCheckEnabled || services.Updates.UpdateReady) return;
            await services.Updates.CheckAndDownloadAsync(userInitiated: false);
            if (services.Updates.UpdateReady) services.Notifier.Info("ShotIt update ready", "It installs the next time ShotIt closes.");
        };
        timer.Start();
    }

    private void OpenSettings() => WindowsHost.OpenSettings(_services!);

    private void OpenHistory() => WindowsHost.OpenHistory(_services!);

    private void ShowOnboarding() => WindowsHost.OpenOnboarding(_services!);

    private void RunSelfTest(AppCommandLine command) => SelfTest.Run(this, command);

    public void ExitApplication()
    {
        if (_exiting) return;
        _exiting = true;
        _services?.CloseAllWindows();
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _services?.Updates.ApplyOnExit();
        _services?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
