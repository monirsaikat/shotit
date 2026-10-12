using ShotIt.Capture;
using ShotIt.Core.Display;
using ShotIt.Core.Imaging;
using ShotIt.Core.Settings;
using ShotIt.Native;

namespace ShotIt.Capture.Tests;

public class GpuBackendTests
{
    public static TheoryData<string> BackendNames => new() { "DXGI", "WGC" };

    private static IMonitorCaptureBackend Backend(string name) => CaptureBackends.CreateGpuBackends().Single(b => b.Name == name);

    [Fact]
    public void Backends_are_offered_border_free_first()
    {
        var names = CaptureBackends.CreateGpuBackends().Select(b => b.Name).ToArray();
        Assert.Equal(["DXGI", "WGC"], names);
    }

    [Theory]
    [MemberData(nameof(BackendNames))]
    public void A_backend_returns_the_monitor_at_its_exact_size_or_nothing(string name)
    {
        if (!TestSetup.HasDesktop) return;
        var backend = Backend(name);
        if (!backend.IsAvailable) return;
        foreach (var monitor in Monitors.GetTopology().Monitors)
        {
            PixelBuffer? image;
            try { image = backend.TryCapture(monitor); }
            catch (Exception) { image = null; } // the service treats a throwing backend as "not available"
            if (image is null) continue; // remote sessions and some virtual displays cannot be duplicated
            Assert.Equal(monitor.Bounds.Width, image.Width);
            Assert.Equal(monitor.Bounds.Height, image.Height);
            Assert.Equal(255, image.GetPixel(image.Width / 2, image.Height / 2).A);
        }
    }

    [Theory]
    [InlineData(CaptureBackendPreference.DesktopDuplication)]
    [InlineData(CaptureBackendPreference.WindowsGraphicsCapture)]
    public void The_service_always_returns_a_frame_whatever_backend_is_preferred(CaptureBackendPreference preference)
    {
        if (!TestSetup.HasDesktop) return;
        var service = new ScreenCaptureService(CaptureBackends.CreateGpuBackends()) { Preference = preference };
        var frame = service.CaptureAll(includeCursor: false);
        Assert.Equal(frame.VirtualBounds.Size, frame.Image.Size);
        Assert.True(frame.Backend is "GDI" or "DXGI" or "WGC", frame.Backend);
    }

    private sealed class Broken(bool throws) : IMonitorCaptureBackend
    {
        public int Calls { get; private set; }

        public string Name => "DXGI";

        public bool IsAvailable => true;

        public PixelBuffer? TryCapture(MonitorDescriptor monitor)
        {
            Calls++;
            return throws ? throw new InvalidOperationException("device lost") : null;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_failing_backend_falls_back_to_gdi(bool throws)
    {
        if (!TestSetup.HasDesktop) return;
        var broken = new Broken(throws);
        var messages = new List<string>();
        var service = new ScreenCaptureService([broken], messages.Add) { Preference = CaptureBackendPreference.DesktopDuplication };
        var frame = service.CaptureAll(includeCursor: false);
        Assert.Equal("GDI", frame.Backend);
        Assert.True(broken.Calls >= 1);
        Assert.NotEmpty(messages);
    }

    [Fact]
    public void The_classic_preference_never_touches_a_gpu_backend()
    {
        if (!TestSetup.HasDesktop) return;
        var broken = new Broken(throws: true);
        var service = new ScreenCaptureService([broken]) { Preference = CaptureBackendPreference.Gdi };
        Assert.Equal("GDI", service.CaptureAll(includeCursor: false).Backend);
        Assert.Equal(0, broken.Calls);
    }
}
