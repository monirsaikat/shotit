using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using ShotIt.Core.Display;
using ShotIt.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace ShotIt.Native;

internal static class NativeConvert
{
    public static PixelRect ToPixelRect(this RECT r) => PixelRect.FromLTRB(r.left, r.top, r.right, r.bottom);
}

/// <summary>Enumerates displays. All coordinates are physical pixels (the process is per-monitor DPI aware).</summary>
public static unsafe class Monitors
{
    public static MonitorTopology GetTopology() => new(Enumerate());

    public static IReadOnlyList<MonitorDescriptor> Enumerate()
    {
        var list = new List<MonitorDescriptor>();
        var handle = GCHandle.Alloc(list);
        try
        {
            PInvoke.EnumDisplayMonitors(default, (RECT*)null, &OnMonitor, (LPARAM)GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        if (list.Count == 0)
        {
            // Headless sessions report no monitors; give callers something sane to work with.
            int w = Math.Max(1, PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CXSCREEN));
            int h = Math.Max(1, PInvoke.GetSystemMetrics(Windows.Win32.UI.WindowsAndMessaging.SYSTEM_METRICS_INDEX.SM_CYSCREEN));
            var r = new PixelRect(0, 0, w, h);
            list.Add(new MonitorDescriptor(0, "DISPLAY", r, r, 96, true));
        }

        return list;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL OnMonitor(HMONITOR monitor, HDC dc, RECT* rect, LPARAM data)
    {
        try
        {
            var list = (List<MonitorDescriptor>)GCHandle.FromIntPtr(data).Target!;
            MONITORINFOEXW info = default;
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (!PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&info)) return true;

            uint dpiX = 96;
            if (PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint x, out _).Succeeded && x > 0)
                dpiX = x;

            const uint MonitorInfoPrimary = 1;
            list.Add(new MonitorDescriptor(
                (nint)monitor.Value,
                info.szDevice.ToString(),
                info.monitorInfo.rcMonitor.ToPixelRect(),
                info.monitorInfo.rcWork.ToPixelRect(),
                (int)dpiX,
                (info.monitorInfo.dwFlags & MonitorInfoPrimary) != 0));
        }
        catch
        {
            // Never let an exception cross the native boundary.
        }

        return true;
    }

    public static PixelPoint GetCursorPosition() =>
        PInvoke.GetCursorPos(out var p) ? new PixelPoint(p.X, p.Y) : default;
}
