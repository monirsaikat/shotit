using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ShotIt.Core.Display;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using ShotIt.Core.Settings;
using ShotIt.Native;

namespace ShotIt.Capture;

/// <summary>A frozen picture of the whole virtual screen plus the monitor layout it was taken with.</summary>
public sealed record ScreenFrame(PixelBuffer Image, MonitorTopology Topology, string Backend, TimeSpan Elapsed)
{
    public PixelRect VirtualBounds => Topology.VirtualBounds;

    /// <summary>Cuts out a region given in virtual-screen coordinates.</summary>
    public PixelBuffer Crop(PixelRect virtualRect) =>
        Image.Crop(virtualRect.Offset(-VirtualBounds.X, -VirtualBounds.Y));
}

public sealed record WindowCapture(PixelBuffer Image, PixelRect Bounds, string Title, string ProcessName);

/// <summary>Thrown when the screen cannot be captured at all (secure desktop, disconnected session).</summary>
public sealed class CaptureUnavailableException : Exception
{
    public CaptureUnavailableException()
    {
    }

    public CaptureUnavailableException(string message)
        : base(message)
    {
    }

    public CaptureUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>One way of reading a monitor's pixels. Implementations must be safe to call from any thread.</summary>
public interface IMonitorCaptureBackend
{
    string Name { get; }

    bool IsAvailable { get; }

    /// <summary>Returns the monitor's pixels, or null when this backend cannot capture it right now.</summary>
    PixelBuffer? TryCapture(MonitorDescriptor monitor);
}

/// <summary>
/// Captures the screen. GDI is the default because it works in every session type, returns correctly
/// tone-mapped colours on HDR displays and never flashes a capture border. GPU backends are tried
/// first only when GDI is known to return black: while an exclusive full-screen application is running,
/// or when the user selects one explicitly.
/// </summary>
public sealed class ScreenCaptureService
{
    private readonly IReadOnlyList<IMonitorCaptureBackend> _gpuBackends;
    private readonly Action<string> _log;

    public ScreenCaptureService(IEnumerable<IMonitorCaptureBackend>? gpuBackends = null, Action<string>? log = null)
    {
        _gpuBackends = gpuBackends?.ToArray() ?? [];
        _log = log ?? (_ => { });
    }

    public CaptureBackendPreference Preference { get; set; } = CaptureBackendPreference.Auto;

    public ScreenFrame CaptureAll(bool includeCursor)
    {
        var watch = Stopwatch.StartNew();
        if (!WindowList.IsInputDesktopAccessible())
            throw new CaptureUnavailableException("The screen is showing a secure desktop (lock screen or administrator prompt) and cannot be captured.");

        var topology = Monitors.GetTopology();
        PixelBuffer? image = null;
        string backendName = "GDI";

        foreach (var backend in GpuBackendsToTry())
        {
            image = TryCaptureWithBackend(backend, topology);
            if (image is not null)
            {
                backendName = backend.Name;
                break;
            }

            _log($"Capture backend {backend.Name} failed; falling back.");
        }

        if (image is null)
        {
            try
            {
                image = GdiScreen.Capture(topology.VirtualBounds);
            }
            catch (Win32Exception ex)
            {
                throw new CaptureUnavailableException("Windows refused to let the screen be read. " + ex.Message, ex);
            }

            if (Preference == CaptureBackendPreference.Auto && RepairBlackMonitors(image, topology) is { } repairedWith)
                backendName = "GDI+" + repairedWith;
        }

        if (includeCursor && CursorCapture.Capture() is { } cursor)
            CursorCapture.Composite(image, topology.VirtualBounds.Location, cursor);

        watch.Stop();
        return new ScreenFrame(image, topology, backendName, watch.Elapsed);
    }

    private IEnumerable<IMonitorCaptureBackend> GpuBackendsToTry()
    {
        switch (Preference)
        {
            case CaptureBackendPreference.Gdi:
                return [];
            case CaptureBackendPreference.WindowsGraphicsCapture:
                return _gpuBackends.Where(b => b.IsAvailable && b.Name == "WGC");
            case CaptureBackendPreference.DesktopDuplication:
                return _gpuBackends.Where(b => b.IsAvailable && b.Name == "DXGI");
            default:
                return WindowList.IsExclusiveFullScreenAppRunning() ? _gpuBackends.Where(b => b.IsAvailable) : [];
        }
    }

    /// <summary>
    /// GDI reads black for a monitor showing borderless full-screen video or games that bypass the
    /// compositor. Such monitors are captured again with a GPU backend. Returns the backend used, if any.
    /// </summary>
    private string? RepairBlackMonitors(PixelBuffer image, MonitorTopology topology)
    {
        string? used = null;
        var origin = topology.VirtualBounds.Location;
        foreach (var monitor in topology.Monitors)
        {
            var local = monitor.Bounds.Offset(-origin.X, -origin.Y).Intersect(image.Bounds);
            if (local.IsEmpty || !IsAllBlack(image, local)) continue;

            _log($"GDI returned a black picture for monitor {monitor.Bounds}; trying a GPU backend.");
            foreach (var backend in _gpuBackends.Where(b => b.IsAvailable))
            {
                PixelBuffer? part;
                try
                {
                    part = backend.TryCapture(monitor);
                }
                catch (Exception ex)
                {
                    _log($"Capture backend {backend.Name} threw: {ex.Message}");
                    continue;
                }

                if (part is null || IsAllBlack(part, part.Bounds)) continue;
                part.MakeOpaque();
                image.Blit(part, local.X, local.Y);
                used = backend.Name;
                break;
            }
        }

        return used;
    }

    /// <summary>True when every pixel in the rectangle is pure black (GDI sets alpha to opaque first).</summary>
    internal static bool IsAllBlack(PixelBuffer image, PixelRect rect)
    {
        for (int y = rect.Top; y < rect.Bottom; y++)
        {
            var row = MemoryMarshal.Cast<byte, uint>(image.Row(y).Slice(rect.X * 4, rect.Width * 4));
            foreach (uint pixel in row)
                if ((pixel & 0x00FFFFFF) != 0) return false;
        }

        return true;
    }

    private PixelBuffer? TryCaptureWithBackend(IMonitorCaptureBackend backend, MonitorTopology topology)
    {
        try
        {
            var bounds = topology.VirtualBounds;
            var result = new PixelBuffer(bounds.Width, bounds.Height);
            foreach (var monitor in topology.Monitors)
            {
                var part = backend.TryCapture(monitor);
                if (part is null) return null;
                result.Blit(part, monitor.Bounds.X - bounds.X, monitor.Bounds.Y - bounds.Y);
            }

            result.MakeOpaque();
            return result;
        }
        catch (Exception ex)
        {
            _log($"Capture backend {backend.Name} threw: {ex.Message}");
            return null;
        }
    }

    /// <summary>Captures one rectangle of the virtual screen directly, without a full frame.</summary>
    public PixelBuffer CaptureRect(PixelRect rect, bool includeCursor)
    {
        var frame = CaptureAll(includeCursor);
        var clipped = rect.Intersect(frame.VirtualBounds);
        if (clipped.IsEmpty) throw new ArgumentException("The rectangle lies outside every monitor.", nameof(rect));
        return frame.Crop(clipped);
    }

    /// <summary>
    /// Captures a single window. The window renders itself when it can (so covered parts are included);
    /// otherwise the visible screen area is used.
    /// </summary>
    public WindowCapture? CaptureWindow(nint hwnd, bool includeCursor)
    {
        if (WindowList.DescribeWindow(hwnd) is not { } window) return null;
        var topology = Monitors.GetTopology();
        var visible = window.Bounds.Intersect(topology.VirtualBounds);
        if (visible.IsEmpty) return null;

        PixelBuffer? image = null;
        var bounds = window.Bounds;

        // PrintWindow output covers the full window rectangle including invisible borders; crop to the frame.
        if (!includeCursor && GdiScreen.PrintWindow(hwnd) is { } printed)
        {
            // The printed image is as large as GetWindowRect; the visible frame is inset inside it.
            int insetX = Math.Max(0, (printed.Width - bounds.Width) / 2);
            var frameInPrint = new PixelRect(insetX, 0, Math.Min(bounds.Width, printed.Width - insetX), Math.Min(bounds.Height, printed.Height));
            if (!frameInPrint.IsEmpty) image = printed.Crop(frameInPrint);
        }

        if (image is null)
        {
            var frame = CaptureAll(includeCursor);
            image = frame.Crop(visible);
            bounds = visible;
        }

        return new WindowCapture(image, bounds, window.Title, WindowList.GetProcessName(window.ProcessId));
    }
}
