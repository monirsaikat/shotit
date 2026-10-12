using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ShotIt.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.System.StationsAndDesktops;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace ShotIt.Native;

public sealed record NativeWindow(nint Handle, string Title, string ClassName, PixelRect Bounds, uint ProcessId);

/// <summary>Finds the windows a user can see, in front-to-back order, with their true visible bounds.</summary>
public static unsafe class WindowList
{
    private sealed class EnumState
    {
        public List<NativeWindow> Result { get; } = [];
        public uint ExcludeProcessId { get; init; }
        public bool Children { get; init; }
    }

    /// <summary>Top-level windows from front to back. Windows of <paramref name="excludeProcessId"/> are skipped.</summary>
    public static IReadOnlyList<NativeWindow> EnumerateTopLevel(uint excludeProcessId = 0)
    {
        var state = new EnumState { ExcludeProcessId = excludeProcessId };
        var handle = GCHandle.Alloc(state);
        try
        {
            PInvoke.EnumWindows(&OnWindow, (LPARAM)GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return state.Result;
    }

    /// <summary>Visible child windows (panes, controls with their own HWND), deepest last.</summary>
    public static IReadOnlyList<NativeWindow> EnumerateChildren(nint parent)
    {
        var state = new EnumState { Children = true };
        var handle = GCHandle.Alloc(state);
        try
        {
            PInvoke.EnumChildWindows((HWND)parent, &OnWindow, (LPARAM)GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return state.Result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL OnWindow(HWND hwnd, LPARAM data)
    {
        try
        {
            var state = (EnumState)GCHandle.FromIntPtr(data).Target!;
            if (Describe(hwnd, state.Children) is { } window && (state.ExcludeProcessId == 0 || window.ProcessId != state.ExcludeProcessId))
                state.Result.Add(window);
        }
        catch
        {
            // Never let an exception cross the native boundary.
        }

        return true;
    }

    private static NativeWindow? Describe(HWND hwnd, bool child)
    {
        if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd)) return null;

        if (!child)
        {
            // Windows on other virtual desktops and suspended UWP apps report visible but are cloaked.
            int cloaked = 0;
            if (PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(int)).Succeeded && cloaked != 0)
                return null;
        }

        var bounds = GetBounds(hwnd, child);
        if (bounds.Width < 4 || bounds.Height < 4) return null;

        string className = GetClassName(hwnd);
        if (!child)
        {
            var ex = (WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
            // Fully click-through layered helpers (overlays, input sinks) are not capture targets.
            if (ex.HasFlag(WINDOW_EX_STYLE.WS_EX_TRANSPARENT) && ex.HasFlag(WINDOW_EX_STYLE.WS_EX_LAYERED)) return null;
            if (className is "Progman" or "WorkerW" or "Windows.UI.Core.CoreWindow" && GetTitle(hwnd).Length == 0) return null;
        }

        PInvoke.GetWindowThreadProcessId(hwnd, out uint pid);
        return new NativeWindow((nint)hwnd.Value, GetTitle(hwnd), className, bounds, pid);
    }

    /// <summary>
    /// The window rectangle the user actually sees. GetWindowRect includes invisible resize borders
    /// on Windows 10 and 11, so DWM's extended frame bounds are used whenever available.
    /// </summary>
    public static PixelRect GetBounds(nint hwnd) => GetBounds((HWND)hwnd, child: false);

    private static PixelRect GetBounds(HWND hwnd, bool child)
    {
        if (!child)
        {
            RECT frame;
            if (PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &frame, (uint)sizeof(RECT)).Succeeded)
            {
                var r = frame.ToPixelRect();
                if (!r.IsEmpty) return r;
            }
        }

        return PInvoke.GetWindowRect(hwnd, out RECT rect) ? rect.ToPixelRect() : PixelRect.Empty;
    }

    private static string GetTitle(HWND hwnd)
    {
        int length = PInvoke.GetWindowTextLength(hwnd);
        if (length <= 0) return string.Empty;
        Span<char> buffer = length < 512 ? stackalloc char[length + 1] : new char[length + 1];
        int copied = PInvoke.GetWindowText(hwnd, buffer);
        return copied > 0 ? new string(buffer[..copied]) : string.Empty;
    }

    private static string GetClassName(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        int copied = PInvoke.GetClassName(hwnd, buffer);
        return copied > 0 ? new string(buffer[..copied]) : string.Empty;
    }

    public static nint GetForeground() => (nint)PInvoke.GetForegroundWindow().Value;

    public static NativeWindow? DescribeWindow(nint hwnd) =>
        hwnd != 0 && PInvoke.IsWindow((HWND)hwnd) ? Describe((HWND)hwnd, child: false) : null;

    public static string GetWindowTitle(nint hwnd) => hwnd == 0 ? string.Empty : GetTitle((HWND)hwnd);

    /// <summary>Executable name without extension, or an empty string when the process cannot be queried.</summary>
    public static string GetProcessName(uint processId)
    {
        if (processId == 0) return string.Empty;
        HANDLE process = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process.IsNull) return string.Empty;
        try
        {
            Span<char> buffer = stackalloc char[1024];
            uint size = (uint)buffer.Length;
            if (!PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref size)) return string.Empty;
            return Path.GetFileNameWithoutExtension(new string(buffer[..(int)size]));
        }
        finally
        {
            PInvoke.CloseHandle(process);
        }
    }

    public static uint GetProcessId(nint hwnd)
    {
        if (hwnd == 0) return 0;
        PInvoke.GetWindowThreadProcessId((HWND)hwnd, out uint pid);
        return pid;
    }

    /// <summary>True while an exclusive full-screen Direct3D application (a game) owns the display.</summary>
    public static bool IsExclusiveFullScreenAppRunning() =>
        PInvoke.SHQueryUserNotificationState(out var state).Succeeded &&
        state == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN;

    /// <summary>
    /// False while the secure desktop is showing (UAC prompt, lock screen, Ctrl+Alt+Del). The screen
    /// cannot be captured then, and showing an overlay would only trap the user.
    /// </summary>
    public static bool IsInputDesktopAccessible()
    {
        HDESK desktop = PInvoke.OpenInputDesktop(0, false, (DESKTOP_ACCESS_FLAGS)0x0100 /* DESKTOP_SWITCHDESKTOP */);
        if (desktop.IsNull) return false;
        PInvoke.CloseDesktop(desktop);
        return true;
    }
}
