using System.Windows.Interop;
using ShotIt.Core.Settings;
using ShotIt.Native;

namespace ShotIt.App.Infrastructure;

/// <summary>
/// A hidden top-level window that receives hotkeys, tray callbacks and system broadcasts.
/// It must be top-level (not message-only): message-only windows do not get broadcasts such as
/// WM_DISPLAYCHANGE or TaskbarCreated.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    public const int TrayCallback = Messages.App + 1;

    private readonly HwndSource _source;

    public MessageWindow()
    {
        var parameters = new HwndSourceParameters("ShotIt.MessageWindow")
        {
            Width = 0,
            Height = 0,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP, never made visible
            ExtendedWindowStyle = 0x00000080 | 0x08000000, // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public nint Handle => _source.Handle;

    public event Action<int>? HotkeyPressed;
    public event Action<TrayEvent, ShotIt.Core.Geometry.PixelPoint>? TrayEvent;
    public event Action? DisplayChanged;
    public event Action? TaskbarCreated;
    public event Action? ThemeChanged;
    public event Action? Suspending;
    public event Action? Resumed;

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Messages.Hotkey)
        {
            HotkeyPressed?.Invoke((int)wParam);
            handled = true;
        }
        else if (msg == TrayCallback)
        {
            var kind = TrayIcon.TranslateMessage(wParam, lParam, out var anchor);
            if (kind != Native.TrayEvent.None) TrayEvent?.Invoke(kind, anchor);
            handled = true;
        }
        else if (msg == Messages.DisplayChange)
        {
            DisplayChanged?.Invoke();
        }
        else if (msg == Messages.SettingChange)
        {
            ThemeChanged?.Invoke();
        }
        else if (msg == Messages.PowerBroadcast)
        {
            if ((int)wParam == Messages.PowerSuspend) Suspending?.Invoke();
            else if ((int)wParam == Messages.PowerResumeAutomatic) Resumed?.Invoke();
        }
        else if (msg == Messages.TaskbarCreated && Messages.TaskbarCreated != 0)
        {
            TaskbarCreated?.Invoke();
        }

        return 0;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}

public enum HotkeyAction
{
    Region = 1,
    FullScreen,
    ActiveWindow,
    Window,
    LastRegion,
    Freeform,
    DelayedRegion,
    TogglePins,
    OpenHistory,
    PinClipboard,

    /// <summary>Registered only while a capture session is open, so Esc always works even without focus.</summary>
    SessionEscape = 100,
}

/// <summary>Registers the user's global hotkeys and reports the ones Windows refused.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly MessageWindow _window;
    private readonly HashSet<HotkeyAction> _registered = [];
    private HotkeySettings? _current;

    public HotkeyManager(MessageWindow window)
    {
        _window = window;
        _window.HotkeyPressed += OnHotkey;
    }

    public event Action<HotkeyAction>? Pressed;

    /// <summary>Hotkeys that could not be registered on the last <see cref="Apply"/>, with their key text.</summary>
    public IReadOnlyList<(HotkeyAction Action, Hotkey Hotkey)> Failures { get; private set; } = [];

    private void OnHotkey(int id) => Pressed?.Invoke((HotkeyAction)id);

    private static IEnumerable<(HotkeyAction Action, Hotkey Hotkey)> Map(HotkeySettings s) =>
    [
        (HotkeyAction.Region, s.Region),
        (HotkeyAction.FullScreen, s.FullScreen),
        (HotkeyAction.ActiveWindow, s.ActiveWindow),
        (HotkeyAction.Window, s.Window),
        (HotkeyAction.LastRegion, s.LastRegion),
        (HotkeyAction.Freeform, s.Freeform),
        (HotkeyAction.DelayedRegion, s.DelayedRegion),
        (HotkeyAction.TogglePins, s.TogglePins),
        (HotkeyAction.OpenHistory, s.OpenHistory),
        (HotkeyAction.PinClipboard, s.PinClipboard),
    ];

    /// <summary>(Re)registers every hotkey. Safe to call repeatedly: after settings change, resume, Explorer restart.</summary>
    public void Apply(HotkeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _current = settings;
        UnregisterAll(keepSessionKeys: true);
        var failures = new List<(HotkeyAction, Hotkey)>();
        var seen = new HashSet<Hotkey>();
        foreach (var (action, hotkey) in Map(settings))
        {
            if (hotkey.IsEmpty) continue;
            // The same combination assigned twice can only work once; report the duplicate as failed.
            if (!seen.Add(hotkey) || !Hotkeys.Register(_window.Handle, (int)action, hotkey))
            {
                failures.Add((action, hotkey));
                continue;
            }

            _registered.Add(action);
        }

        Failures = failures;
    }

    public void Reapply()
    {
        if (_current is not null) Apply(_current);
    }

    /// <summary>Temporarily releases all hotkeys (while the user records a new one in settings).</summary>
    public void Suspend() => UnregisterAll(keepSessionKeys: true);

    public bool RegisterSessionEscape()
    {
        if (_registered.Contains(HotkeyAction.SessionEscape)) return true;
        const int VkEscape = 0x1B;
        bool ok = Hotkeys.Register(_window.Handle, (int)HotkeyAction.SessionEscape, new Hotkey(HotkeyModifiers.None, VkEscape));
        if (ok) _registered.Add(HotkeyAction.SessionEscape);
        return ok;
    }

    public void UnregisterSessionEscape()
    {
        if (!_registered.Remove(HotkeyAction.SessionEscape)) return;
        Hotkeys.Unregister(_window.Handle, (int)HotkeyAction.SessionEscape);
    }

    private void UnregisterAll(bool keepSessionKeys)
    {
        foreach (var action in _registered.ToArray())
        {
            if (keepSessionKeys && action == HotkeyAction.SessionEscape) continue;
            Hotkeys.Unregister(_window.Handle, (int)action);
            _registered.Remove(action);
        }
    }

    public void Dispose()
    {
        _window.HotkeyPressed -= OnHotkey;
        UnregisterAll(keepSessionKeys: false);
    }
}
