using System.Globalization;
using System.Windows;
using ShotIt.App.Infrastructure;
using ShotIt.App.Overlay;
using ShotIt.Core.Geometry;
using ShotIt.Core.Settings;
using ShotIt.Native;
using ShotIt.Rendering;

namespace ShotIt.App.Services;

/// <summary>The notification-area icon, its menu, and the routing of hotkeys and system events.</summary>
public sealed class TrayController : IDisposable
{
    private enum Command
    {
        None,
        Region,
        Window,
        FullScreen,
        AllMonitors,
        ActiveWindow,
        Freeform,
        Delayed,
        LastRegion,
        FixedSize,
        OpenFile,
        EditClipboard,
        PinClipboard,
        TogglePins,
        RestorePins,
        ClosePins,
        History,
        OpenFolder,
        Settings,
        Exit,
    }

    private readonly AppServices _app;
    private readonly Action _exit;
    private readonly Action _openSettings;
    private readonly Action _openHistory;
    private TrayIcon? _icon;

    public TrayController(AppServices app, Action openSettings, Action openHistory, Action exit)
    {
        _app = app;
        _openSettings = openSettings;
        _openHistory = openHistory;
        _exit = exit;

        var window = app.MessageWindow;
        window.TrayEvent += OnTrayEvent;
        window.TaskbarCreated += OnTaskbarCreated;
        window.DisplayChanged += OnDisplayChanged;
        window.Suspending += () => app.Controller.OnSuspending();
        window.Resumed += () => app.Hotkeys.Reapply();
        app.Hotkeys.Pressed += OnHotkey;
    }

    public void Show()
    {
        int size = Math.Clamp((int)Math.Round(16 * Monitors.GetTopology().Primary.Scale), 16, 64);
        _icon = new TrayIcon(_app.MessageWindow.Handle, MessageWindow.TrayCallback, AppIcon.RenderPng(size), Tooltip());
        _app.Notifier.Attach(_icon);
        if (!_icon.IsVisible) _app.Log.Warning("The tray icon could not be added (Explorer may not be running yet)");
    }

    private string Tooltip()
    {
        var region = _app.Settings.Hotkeys.Region;
        return region.IsEmpty ? "ShotIt" : $"ShotIt – press {region} to capture";
    }

    public void RefreshTooltip() => _icon?.SetTooltip(Tooltip());

    private void OnTaskbarCreated()
    {
        // Explorer restarted: the icon is gone and hotkeys may need re-registering.
        _icon?.Recreate();
        _app.Hotkeys.Reapply();
    }

    private void OnDisplayChanged()
    {
        _app.Controller.OnDisplayChanged();
        _app.Pins.OnDisplayChanged();
    }

    private void OnHotkey(HotkeyAction action)
    {
        try
        {
            switch (action)
            {
                case HotkeyAction.SessionEscape: _app.Controller.HandleEscape(); break;
                case HotkeyAction.Region: _app.Controller.StartRegion(); break;
                case HotkeyAction.Window: _app.Controller.StartRegion(OverlayMode.Window); break;
                case HotkeyAction.Freeform: _app.Controller.StartRegion(OverlayMode.Freeform); break;
                case HotkeyAction.FullScreen: _app.Controller.CaptureFullScreen(allMonitors: false); break;
                case HotkeyAction.ActiveWindow: _app.Controller.CaptureActiveWindow(); break;
                case HotkeyAction.LastRegion: _app.Controller.RepeatLastRegion(); break;
                case HotkeyAction.DelayedRegion: _app.Controller.StartDelayed(); break;
                case HotkeyAction.TogglePins: _app.Pins.ToggleAll(); break;
                case HotkeyAction.OpenHistory: _openHistory(); break;
                case HotkeyAction.PinClipboard: _app.PinClipboard(); break;
            }
        }
        catch (Exception ex)
        {
            _app.Log.Error(ex, "Hotkey {Action} failed", action);
        }
    }

    private void OnTrayEvent(TrayEvent kind, PixelPoint anchor)
    {
        switch (kind)
        {
            case TrayEvent.LeftClick:
                // Give the click a moment to finish so the taskbar is not captured mid-animation.
                _app.Dispatcher.BeginInvoke(() => _app.Controller.StartRegion(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                break;
            case TrayEvent.ContextMenu:
                ShowMenu(anchor);
                break;
            case TrayEvent.BalloonClick:
                _app.Notifier.OnClicked();
                break;
        }
    }

    private static string Label(string text, Hotkey hotkey) => hotkey.IsEmpty ? text : text + "\t" + hotkey;

    private void ShowMenu(PixelPoint anchor)
    {
        if (_app.Controller.IsSessionActive) return;
        var keys = _app.Settings.Hotkeys;
        var capture = _app.Settings.Capture;
        string fixedSize = string.Create(CultureInfo.InvariantCulture, $"Fixed size region ({capture.FixedWidth} × {capture.FixedHeight})");
        string delayed = string.Create(CultureInfo.InvariantCulture, $"Capture after {capture.DelaySeconds} s delay");

        var entries = new List<MenuEntry>
        {
            new((int)Command.Region, Label("Capture region", keys.Region)) { Default = true },
            new((int)Command.Window, Label("Capture window or control", keys.Window)),
            new((int)Command.FullScreen, Label("Capture full screen", keys.FullScreen)),
            new((int)Command.AllMonitors, "Capture all monitors"),
            new((int)Command.ActiveWindow, Label("Capture active window", keys.ActiveWindow)),
            new((int)Command.Freeform, Label("Capture freeform shape", keys.Freeform)),
            new((int)Command.Delayed, Label(delayed, keys.DelayedRegion)),
            new((int)Command.LastRegion, Label("Repeat last region", keys.LastRegion)) { Enabled = capture.LastRegion is not null },
            new((int)Command.FixedSize, fixedSize),
            MenuEntry.Separator,
            new((int)Command.OpenFile, "Open image in editor…"),
            new((int)Command.EditClipboard, "Edit clipboard image"),
            new((int)Command.PinClipboard, Label("Pin clipboard image", keys.PinClipboard)),
        };

        if (_app.Pins.Count > 0)
        {
            entries.Add(new MenuEntry(0, string.Create(CultureInfo.InvariantCulture, $"Pins ({_app.Pins.Count})"))
            {
                Children =
                [
                    new((int)Command.TogglePins, Label("Show or hide all", keys.TogglePins)),
                    new((int)Command.RestorePins, "Turn off click-through"),
                    new((int)Command.ClosePins, "Close all"),
                ],
            });
        }

        entries.Add(MenuEntry.Separator);
        entries.Add(new MenuEntry((int)Command.History, Label("History…", keys.OpenHistory)));
        entries.Add(new MenuEntry((int)Command.OpenFolder, "Open screenshots folder"));
        entries.Add(new MenuEntry((int)Command.Settings, "Settings…"));
        entries.Add(MenuEntry.Separator);
        entries.Add(new MenuEntry((int)Command.Exit, "Exit ShotIt"));

        var chosen = (Command)PopupMenu.Show(_app.MessageWindow.Handle, entries, anchor);
        if (chosen == Command.None) return;

        // Run after the menu has fully closed so it is never part of a capture.
        _app.Dispatcher.BeginInvoke(() => Execute(chosen), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private void Execute(Command command)
    {
        try
        {
            switch (command)
            {
                case Command.Region: _app.Controller.StartRegion(); break;
                case Command.Window: _app.Controller.StartRegion(OverlayMode.Window); break;
                case Command.FullScreen: _app.Controller.CaptureFullScreen(allMonitors: false); break;
                case Command.AllMonitors: _app.Controller.CaptureFullScreen(allMonitors: true); break;
                case Command.ActiveWindow: _app.Controller.CaptureActiveWindow(); break;
                case Command.Freeform: _app.Controller.StartRegion(OverlayMode.Freeform); break;
                case Command.Delayed: _app.Controller.StartDelayed(); break;
                case Command.LastRegion: _app.Controller.RepeatLastRegion(); break;
                case Command.FixedSize: _app.Controller.StartRegion(OverlayMode.FixedSize); break;
                case Command.OpenFile: _app.BrowseAndEdit(); break;
                case Command.EditClipboard: _app.EditClipboard(); break;
                case Command.PinClipboard: _app.PinClipboard(); break;
                case Command.TogglePins: _app.Pins.ToggleAll(); break;
                case Command.RestorePins: _app.Pins.RestoreInteraction(); break;
                case Command.ClosePins: _app.Pins.CloseAll(); break;
                case Command.History: _openHistory(); break;
                case Command.OpenFolder: CaptureActions.OpenFolder(_app.Output.SaveFolder); break;
                case Command.Settings: _openSettings(); break;
                case Command.Exit: _exit(); break;
            }
        }
        catch (Exception ex)
        {
            _app.Log.Error(ex, "Tray command {Command} failed", command);
            _app.Notifier.Warn("Something went wrong", "The command could not be completed. Details are in the log file.");
        }
    }

    /// <summary>Tells the user which hotkeys could not be registered, naming the keys.</summary>
    public void ReportHotkeyFailures()
    {
        var failures = _app.Hotkeys.Failures;
        if (failures.Count == 0) return;
        string keys = string.Join(", ", failures.Select(f => f.Hotkey.ToString()).Distinct());
        _app.Log.Warning("Hotkeys in use by another program: {Keys}", keys);
        _app.Notifier.Warn("Some hotkeys are taken",
            $"{keys} {(failures.Count == 1 ? "is" : "are")} already used by another program. Choose different keys in Settings › Hotkeys.",
            _openSettings);
    }

    public void Dispose()
    {
        _icon?.Dispose();
        _icon = null;
    }
}
