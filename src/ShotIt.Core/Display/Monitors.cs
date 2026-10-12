using ShotIt.Core.Geometry;

namespace ShotIt.Core.Display;

/// <summary>One physical display. Bounds are physical pixels in virtual-screen coordinates.</summary>
public sealed record MonitorDescriptor(
    nint Handle,
    string DeviceName,
    PixelRect Bounds,
    PixelRect WorkArea,
    int Dpi,
    bool IsPrimary,
    bool IsHdr = false)
{
    public double Scale => Dpi / 96.0;
}

/// <summary>An immutable snapshot of the monitor layout taken at the start of a capture session.</summary>
public sealed class MonitorTopology
{
    public MonitorTopology(IEnumerable<MonitorDescriptor> monitors)
    {
        Monitors = monitors.ToArray();
        if (Monitors.Count == 0) throw new ArgumentException("At least one monitor is required.", nameof(monitors));
        var bounds = PixelRect.Empty;
        foreach (var m in Monitors) bounds = bounds.Union(m.Bounds);
        VirtualBounds = bounds;
        Primary = Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors[0];
    }

    public IReadOnlyList<MonitorDescriptor> Monitors { get; }
    public PixelRect VirtualBounds { get; }
    public MonitorDescriptor Primary { get; }

    /// <summary>The monitor containing the point, or the nearest one when the point lies in a gap.</summary>
    public MonitorDescriptor FromPoint(PixelPoint p)
    {
        MonitorDescriptor? best = null;
        long bestDistance = long.MaxValue;
        foreach (var m in Monitors)
        {
            if (m.Bounds.Contains(p)) return m;
            long dx = p.X < m.Bounds.Left ? m.Bounds.Left - p.X : p.X >= m.Bounds.Right ? p.X - m.Bounds.Right + 1 : 0;
            long dy = p.Y < m.Bounds.Top ? m.Bounds.Top - p.Y : p.Y >= m.Bounds.Bottom ? p.Y - m.Bounds.Bottom + 1 : 0;
            long d = dx * dx + dy * dy;
            if (d < bestDistance) { bestDistance = d; best = m; }
        }
        return best!;
    }

    /// <summary>The monitor that contains the largest part of the rectangle.</summary>
    public MonitorDescriptor FromRect(PixelRect r)
    {
        MonitorDescriptor? best = null;
        long bestArea = 0;
        foreach (var m in Monitors)
        {
            var i = m.Bounds.Intersect(r);
            long area = (long)i.Width * i.Height;
            if (area > bestArea) { bestArea = area; best = m; }
        }
        return best ?? FromPoint(r.Center);
    }

    /// <summary>True when the two snapshots describe the same layout.</summary>
    public bool LayoutEquals(MonitorTopology other) =>
        Monitors.Count == other.Monitors.Count &&
        Monitors.Zip(other.Monitors).All(p => p.First.Bounds == p.Second.Bounds && p.First.Dpi == p.Second.Dpi);
}

/// <summary>Conversions between physical pixels and WPF device-independent units (1 DIP = 1/96 inch).</summary>
public static class DpiMath
{
    public static double PxToDip(double px, int dpi) => px * 96.0 / dpi;
    public static double DipToPx(double dip, int dpi) => dip * dpi / 96.0;
    public static int DipToPxRounded(double dip, int dpi) => (int)Math.Round(dip * dpi / 96.0, MidpointRounding.AwayFromZero);

    /// <summary>Converts a virtual-screen pixel position to DIPs local to the given monitor.</summary>
    public static Vec2 ToMonitorDip(PixelPoint p, MonitorDescriptor m) =>
        new(PxToDip(p.X - m.Bounds.X, m.Dpi), PxToDip(p.Y - m.Bounds.Y, m.Dpi));

    /// <summary>Converts monitor-local DIPs back to a virtual-screen pixel position.</summary>
    public static PixelPoint FromMonitorDip(Vec2 dip, MonitorDescriptor m) =>
        new(m.Bounds.X + DipToPxRounded(dip.X, m.Dpi), m.Bounds.Y + DipToPxRounded(dip.Y, m.Dpi));
}
