using System.Runtime.InteropServices;
using ShotIt.Core.Geometry;
using ShotIt.Core.Settings;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ShotIt.Native;

/// <summary>Window message numbers the application listens for.</summary>
public static class Messages
{
    public const int Hotkey = (int)PInvoke.WM_HOTKEY;
    public const int DisplayChange = (int)PInvoke.WM_DISPLAYCHANGE;
    public const int SettingChange = (int)PInvoke.WM_SETTINGCHANGE;
    public const int DeviceChange = (int)PInvoke.WM_DEVICECHANGE;
    public const int PowerBroadcast = (int)PInvoke.WM_POWERBROADCAST;
    public const int DpiChanged = (int)PInvoke.WM_DPICHANGED;
    public const int Command = (int)PInvoke.WM_COMMAND;
    public const int App = (int)PInvoke.WM_APP;
    public const int PowerSuspend = (int)PInvoke.PBT_APMSUSPEND;
    public const int PowerResumeAutomatic = (int)PInvoke.PBT_APMRESUMEAUTOMATIC;

    /// <summary>Broadcast by Explorer when the taskbar is (re)created; tray icons must be added again.</summary>
    public static int TaskbarCreated { get; } = (int)PInvoke.RegisterWindowMessage("TaskbarCreated");
}

public static class Hotkeys
{
    /// <summary>Registers a system-wide hotkey that posts WM_HOTKEY to the window. Returns false when the key is taken.</summary>
    public static bool Register(nint hwnd, int id, Hotkey hotkey)
    {
        if (hotkey.IsEmpty) return false;
        var mods = HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) mods |= HOT_KEY_MODIFIERS.MOD_ALT;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Ctrl)) mods |= HOT_KEY_MODIFIERS.MOD_CONTROL;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) mods |= HOT_KEY_MODIFIERS.MOD_SHIFT;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Win)) mods |= HOT_KEY_MODIFIERS.MOD_WIN;
        return PInvoke.RegisterHotKey((HWND)hwnd, id, mods, (uint)hotkey.VirtualKey);
    }

    public static void Unregister(nint hwnd, int id) => PInvoke.UnregisterHotKey((HWND)hwnd, id);
}

public static unsafe class WindowInterop
{
    /// <summary>Positions a window in physical pixels, bypassing WPF's DIP rounding.</summary>
    public static void SetBounds(nint hwnd, PixelRect bounds, bool topmost, bool activate = false)
    {
        var flags = SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;
        if (!activate) flags |= SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
        PInvoke.SetWindowPos((HWND)hwnd, topmost ? HWND.HWND_TOPMOST : HWND.HWND_NOTOPMOST, bounds.X, bounds.Y, bounds.Width, bounds.Height, flags);
    }

    public static void MoveTo(nint hwnd, PixelPoint topLeft) =>
        PInvoke.SetWindowPos((HWND)hwnd, default, topLeft.X, topLeft.Y, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    /// <summary>Re-asserts topmost without moving or activating (other topmost windows may have covered us).</summary>
    public static void BringToTopmost(nint hwnd) =>
        PInvoke.SetWindowPos((HWND)hwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    private static void ChangeExStyle(nint hwnd, WINDOW_EX_STYLE add, WINDOW_EX_STYLE remove)
    {
        var window = (HWND)hwnd;
        var style = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        var updated = (style | add) & ~remove;
        if (updated != style) _ = PInvoke.SetWindowLong(window, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (int)updated);
    }

    /// <summary>Hides the window from Alt+Tab and the taskbar.</summary>
    public static void MakeToolWindow(nint hwnd) =>
        ChangeExStyle(hwnd, WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, WINDOW_EX_STYLE.WS_EX_APPWINDOW);

    /// <summary>Lets mouse input fall through to whatever is underneath (used by click-through pins).</summary>
    public static void SetClickThrough(nint hwnd, bool enabled)
    {
        if (enabled) ChangeExStyle(hwnd, WINDOW_EX_STYLE.WS_EX_TRANSPARENT | WINDOW_EX_STYLE.WS_EX_LAYERED, 0);
        else ChangeExStyle(hwnd, 0, WINDOW_EX_STYLE.WS_EX_TRANSPARENT);
    }

    public static void ShowNoActivate(nint hwnd) => PInvoke.ShowWindow((HWND)hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);

    public static void Hide(nint hwnd) => PInvoke.ShowWindow((HWND)hwnd, SHOW_WINDOW_CMD.SW_HIDE);

    public static int GetDpi(nint hwnd)
    {
        uint dpi = PInvoke.GetDpiForWindow((HWND)hwnd);
        return dpi == 0 ? 96 : (int)dpi;
    }

    /// <summary>
    /// Makes the window the foreground window even when Windows would normally refuse
    /// (the request comes from a hotkey, so the user did ask for it). Returns true on success.
    /// </summary>
    public static bool ForceForeground(nint hwnd)
    {
        var window = (HWND)hwnd;
        if (PInvoke.GetForegroundWindow() == window) return true;
        if (PInvoke.SetForegroundWindow(window)) return true;

        HWND foreground = PInvoke.GetForegroundWindow();
        uint foregroundThread = foreground.IsNull ? 0 : PInvoke.GetWindowThreadProcessId(foreground, out _);
        uint thisThread = PInvoke.GetCurrentThreadId();
        if (foregroundThread == 0 || foregroundThread == thisThread) return PInvoke.GetForegroundWindow() == window;

        // Sharing an input queue with the current foreground thread lifts the restriction.
        if (!PInvoke.AttachThreadInput(thisThread, foregroundThread, true)) return false;
        try
        {
            PInvoke.BringWindowToTop(window);
            PInvoke.SetForegroundWindow(window);
        }
        finally
        {
            PInvoke.AttachThreadInput(thisThread, foregroundThread, false);
        }

        return PInvoke.GetForegroundWindow() == window;
    }

    public static bool IsForeground(nint hwnd) => PInvoke.GetForegroundWindow() == (HWND)hwnd;

    public static void Beep() => PInvoke.MessageBeep(MESSAGEBOX_STYLE.MB_OK);
}

public static class AppIdentity
{
    public const string AppUserModelId = "ShotIt.ShotIt";

    /// <summary>Gives the process a stable identity so toasts and taskbar grouping work for an unpackaged app.</summary>
    public static void Apply() => PInvoke.SetCurrentProcessExplicitAppUserModelID(AppUserModelId);

    /// <summary>Tells Explorer that file associations or context-menu entries changed.</summary>
    public static unsafe void NotifyAssociationsChanged() =>
        PInvoke.SHChangeNotify(SHCNE_ID.SHCNE_ASSOCCHANGED, SHCNF_FLAGS.SHCNF_IDLIST, null, null);

    [DllImport("uxtheme.dll", EntryPoint = "#135", ExactSpelling = true)]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136", ExactSpelling = true)]
    private static extern void FlushMenuThemes();

    /// <summary>
    /// Lets native popup menus (the tray menu) follow the system dark mode. Uses the same uxtheme
    /// entry points as Explorer; silently does nothing on builds where they are missing.
    /// </summary>
    public static void AllowDarkMenus()
    {
        try
        {
            const int AllowDark = 1;
            _ = SetPreferredAppMode(AllowDark);
            FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }
    }
}

public static class DpiAwareness
{
    /// <summary>
    /// Opts the process into per-monitor-v2 DPI awareness. The application manifest already does this;
    /// calling it early is a safety net for hosts without the manifest (tests, tools). Harmless if already set.
    /// </summary>
    public static bool EnablePerMonitorV2() =>
        PInvoke.SetProcessDpiAwarenessContext(Windows.Win32.UI.HiDpi.DPI_AWARENESS_CONTEXT.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
}
