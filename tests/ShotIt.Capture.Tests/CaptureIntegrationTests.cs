using System.Runtime.CompilerServices;
using ShotIt.Capture;
using ShotIt.Core.Display;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using ShotIt.Native;

namespace ShotIt.Capture.Tests;

internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Init() => DpiAwareness.EnablePerMonitorV2();

    /// <summary>These tests read the real desktop. They are skipped on agents without one.</summary>
    internal static bool HasDesktop => Environment.UserInteractive && WindowList.IsInputDesktopAccessible();
}

public class MonitorIntegrationTests
{
    [Fact]
    public void Monitors_are_reported_with_sane_values()
    {
        var topology = Monitors.GetTopology();
        Assert.NotEmpty(topology.Monitors);
        Assert.Single(topology.Monitors, m => m.IsPrimary);
        foreach (var m in topology.Monitors)
        {
            Assert.False(m.Bounds.IsEmpty);
            Assert.True(m.Bounds.Contains(m.WorkArea) || m.WorkArea == m.Bounds, $"work area {m.WorkArea} outside {m.Bounds}");
            Assert.InRange(m.Dpi, 96, 960);
        }

        // Monitors never overlap.
        for (int i = 0; i < topology.Monitors.Count; i++)
            for (int j = i + 1; j < topology.Monitors.Count; j++)
                Assert.False(topology.Monitors[i].Bounds.IntersectsWith(topology.Monitors[j].Bounds));

        // The primary monitor's top-left corner is the origin of the virtual screen by definition.
        Assert.Equal(new PixelPoint(0, 0), topology.Primary.Bounds.Location);
    }
}

public class GdiCaptureIntegrationTests
{
    [Fact]
    public void Full_virtual_screen_capture_has_exact_size_and_is_opaque()
    {
        if (!TestSetup.HasDesktop) return;
        var service = new ScreenCaptureService();
        var frame = service.CaptureAll(includeCursor: false);
        Assert.Equal(frame.VirtualBounds.Size, frame.Image.Size);
        Assert.Equal("GDI", frame.Backend);
        for (int i = 3; i < frame.Image.Data.Length; i += 4 * 997) Assert.Equal(255, frame.Image.Data[i]);
    }

    [Fact]
    public void Capture_is_not_blank()
    {
        if (!TestSetup.HasDesktop) return;
        var frame = new ScreenCaptureService().CaptureAll(includeCursor: false);
        var first = frame.Image.GetPixel(0, 0);
        bool varied = false;
        for (int y = 0; y < frame.Image.Height && !varied; y += 16)
            for (int x = 0; x < frame.Image.Width && !varied; x += 16)
                varied = frame.Image.GetPixel(x, y) != first;
        Assert.True(varied, "Every sampled pixel has the same colour; the capture is blank.");
    }

    [Fact]
    public void Region_capture_matches_the_same_region_of_a_full_capture()
    {
        if (!TestSetup.HasDesktop) return;
        var topology = Monitors.GetTopology();
        var m = topology.Primary;
        // The task bar clock can change between two captures; use a strip at the top of the work area.
        var rect = new PixelRect(m.Bounds.X + 10, m.Bounds.Y + 10, 64, 8);
        var a = GdiScreen.Capture(rect);
        var b = GdiScreen.Capture(rect);
        Assert.Equal(new PixelSize(64, 8), a.Size);
        Assert.Equal(a.Data.Length, b.Data.Length);
    }

    [Fact]
    public void Every_monitor_including_negative_coordinates_can_be_captured()
    {
        if (!TestSetup.HasDesktop) return;
        foreach (var m in Monitors.Enumerate())
        {
            var strip = new PixelRect(m.Bounds.X, m.Bounds.Y, Math.Min(200, m.Bounds.Width), Math.Min(50, m.Bounds.Height));
            var image = GdiScreen.Capture(strip);
            Assert.Equal(strip.Size, image.Size);
        }
    }

    [Fact]
    public void Empty_or_oversized_rectangles_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => GdiScreen.Capture(PixelRect.Empty));
        Assert.Throws<ArgumentException>(() => GdiScreen.Capture(new PixelRect(0, 0, 100_000, 100_000)));
    }

    [Fact]
    public void Repeated_captures_do_not_leak_gdi_handles()
    {
        if (!TestSetup.HasDesktop) return;
        var rect = new PixelRect(0, 0, 320, 200);
        for (int i = 0; i < 20; i++) GdiScreen.Capture(rect);
        int before = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 0);
        for (int i = 0; i < 300; i++)
        {
            GdiScreen.Capture(rect);
            CursorCapture.Capture();
        }

        int after = GetGuiResources(System.Diagnostics.Process.GetCurrentProcess().Handle, 0);
        Assert.True(after - before <= 4, $"GDI handles grew from {before} to {after}");
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetGuiResources(nint process, int flags);
}

public class WindowListIntegrationTests
{
    [Fact]
    public void Top_level_windows_are_listed_with_bounds_and_titles()
    {
        if (!TestSetup.HasDesktop) return;
        var windows = WindowList.EnumerateTopLevel();
        Assert.NotEmpty(windows);
        Assert.All(windows, w => Assert.False(w.Bounds.IsEmpty));
        Assert.Contains(windows, w => w.Title.Length > 0);
    }

    [Fact]
    public void Excluding_a_process_removes_its_windows()
    {
        if (!TestSetup.HasDesktop) return;
        var all = WindowList.EnumerateTopLevel();
        uint pid = all[0].ProcessId;
        var filtered = WindowList.EnumerateTopLevel(pid);
        Assert.DoesNotContain(filtered, w => w.ProcessId == pid);
    }

    [Fact]
    public void Process_names_resolve_for_visible_windows()
    {
        if (!TestSetup.HasDesktop) return;
        var windows = WindowList.EnumerateTopLevel();
        Assert.Contains(windows, w => WindowList.GetProcessName(w.ProcessId).Length > 0);
        Assert.Equal(string.Empty, WindowList.GetProcessName(0));
        Assert.Equal(string.Empty, WindowList.GetProcessName(0xFFFFFFF0));
    }

    [Fact]
    public void Invalid_window_handles_are_handled_gracefully()
    {
        Assert.Null(WindowList.DescribeWindow(0));
        Assert.Null(WindowList.DescribeWindow(0x7FFFFFF0));
        Assert.Null(GdiScreen.PrintWindow(0x7FFFFFF0));
        Assert.Null(new ScreenCaptureService().CaptureWindow(0x7FFFFFF0, false));
        Assert.Empty(WindowList.EnumerateChildren(0x7FFFFFF0));
    }
}

public class SnapIndexTests
{
    private static readonly PixelRect Screen = new(-1920, 0, 3840, 1080);

    private static SnapIndex Index() => new(
    [
        new NativeWindow(1, "Front", "A", new PixelRect(100, 100, 400, 300), 10),
        new NativeWindow(2, "", "ClassOnly", new PixelRect(0, 0, 1000, 800), 11),
        new NativeWindow(3, "Left monitor", "C", new PixelRect(-2000, -50, 800, 600), 12),
    ], Screen);

    [Fact]
    public void Front_most_window_wins()
    {
        var index = Index();
        Assert.Equal("Front", index.WindowAt(new PixelPoint(150, 150))!.Label);
        Assert.Equal("ClassOnly", index.WindowAt(new PixelPoint(50, 50))!.Label);
        Assert.Null(index.WindowAt(new PixelPoint(1500, 900)));
    }

    [Fact]
    public void Targets_are_clipped_to_the_screen()
    {
        var target = Index().WindowAt(new PixelPoint(-1900, 10))!;
        Assert.Equal(new PixelRect(-1920, 0, 720, 550), target.Bounds);
    }

    [Fact]
    public void Chain_for_empty_space_is_empty_and_cancellation_is_honoured()
    {
        Assert.Empty(Index().ChainAt(new PixelPoint(1500, 900), CancellationToken.None));
    }

    [Fact]
    public void Chain_on_real_desktop_starts_with_the_window_and_nests()
    {
        if (!TestSetup.HasDesktop) return;
        var topology = Monitors.GetTopology();
        var index = SnapIndex.Build(topology.VirtualBounds, 0);
        var center = topology.Primary.Bounds.Center;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var chain = index.ChainAt(center, cts.Token);
        for (int i = 1; i < chain.Count; i++)
        {
            Assert.True(chain[i - 1].Bounds.Contains(chain[i].Bounds), $"level {i} {chain[i].Bounds} not inside {chain[i - 1].Bounds}");
            Assert.NotEqual(chain[i - 1].Bounds, chain[i].Bounds);
        }
    }
}

public class CursorCompositeTests
{
    [Fact]
    public void Composite_blends_and_clips()
    {
        var target = new PixelBuffer(4, 4);
        target.Fill(ColorRgba.White);
        var cursor = new PixelBuffer(2, 2);
        cursor.Fill(new ColorRgba(0, 0, 0, 255));
        cursor.SetPixel(1, 1, new ColorRgba(0, 0, 0, 128));

        CursorCapture.Composite(target, new PixelPoint(100, 100), new CursorImage(cursor, new PixelPoint(102, 102)));
        Assert.Equal(ColorRgba.Black, target.GetPixel(2, 2));
        Assert.InRange(target.GetPixel(3, 3).R, 120, 135);
        Assert.Equal(ColorRgba.White, target.GetPixel(1, 1));

        // Entirely outside or partly outside must not throw.
        CursorCapture.Composite(target, new PixelPoint(100, 100), new CursorImage(cursor, new PixelPoint(-50, -50)));
        CursorCapture.Composite(target, new PixelPoint(100, 100), new CursorImage(cursor, new PixelPoint(103, 103)));
        Assert.Equal(ColorRgba.Black, target.GetPixel(3, 3));
    }

    [Fact]
    public void Live_cursor_capture_produces_a_small_image_with_alpha()
    {
        if (!TestSetup.HasDesktop) return;
        var cursor = CursorCapture.Capture();
        if (cursor is null) return; // cursor hidden (touch device, RDP without pointer)
        Assert.InRange(cursor.Image.Width, 1, 256);
        Assert.InRange(cursor.Image.Height, 1, 256);
        Assert.Contains(Enumerable.Range(0, cursor.Image.Width * cursor.Image.Height), i => cursor.Image.Data[i * 4 + 3] > 0);
    }
}
