using System.Runtime.InteropServices;
using ShotIt.Core.Display;
using ShotIt.Core.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace ShotIt.Capture;

/// <summary>Shared Direct3D plumbing for the GPU capture backends.</summary>
internal static class D3DHelpers
{
    private static readonly FeatureLevel[] Levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];

    public static ID3D11Device? CreateDevice(IDXGIAdapter? adapter)
    {
        var result = D3D11.D3D11CreateDevice(adapter, adapter is null ? DriverType.Hardware : DriverType.Unknown, DeviceCreationFlags.BgraSupport, Levels, out ID3D11Device? device);
        return result.Success ? device : null;
    }

    /// <summary>Copies a GPU texture into system memory. Returns null for formats that are not plain 8-bit BGRA.</summary>
    public static PixelBuffer? ReadTexture(ID3D11Device device, ID3D11Texture2D texture, int width, int height)
    {
        var description = texture.Description;
        if (description.Format is not (Format.B8G8R8A8_UNorm or Format.B8G8R8A8_UNorm_SRgb)) return null;
        width = Math.Min(width, (int)description.Width);
        height = Math.Min(height, (int)description.Height);
        if (width <= 0 || height <= 0) return null;

        var stagingDescription = description;
        stagingDescription.Usage = ResourceUsage.Staging;
        stagingDescription.BindFlags = BindFlags.None;
        stagingDescription.CPUAccessFlags = CpuAccessFlags.Read;
        stagingDescription.MiscFlags = ResourceOptionFlags.None;
        stagingDescription.MipLevels = 1;
        stagingDescription.ArraySize = 1;
        stagingDescription.SampleDescription = new SampleDescription(1, 0);

        using var staging = device.CreateTexture2D(stagingDescription);
        var context = device.ImmediateContext;
        context.CopyResource(staging, texture);
        var mapped = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var buffer = new PixelBuffer(width, height);
            int stride = width * 4;
            for (int y = 0; y < height; y++)
                Marshal.Copy(mapped.DataPointer + (nint)(y * (long)mapped.RowPitch), buffer.Data, y * stride, stride);
            buffer.MakeOpaque();
            return buffer;
        }
        finally
        {
            context.Unmap(staging, 0);
        }
    }

    /// <summary>A frame that is entirely black usually means the capture API returned nothing useful.</summary>
    public static bool IsBlank(PixelBuffer buffer)
    {
        var data = buffer.Data;
        // Sample sparsely; a real desktop has a non-black pixel somewhere on a coarse grid.
        int step = Math.Max(4, (data.Length / 4 / 4096) * 4);
        for (int i = 0; i + 2 < data.Length; i += step)
            if (data[i] != 0 || data[i + 1] != 0 || data[i + 2] != 0) return false;
        return true;
    }
}

/// <summary>
/// DXGI Desktop Duplication. It reads the composed desktop from the GPU, so it sees exclusive
/// full-screen games and hardware-overlay video that GDI returns as black. It draws no capture border.
/// </summary>
public sealed class DxgiDuplicationBackend : IMonitorCaptureBackend
{
    public string Name => "DXGI";

    public bool IsAvailable => true;

    public PixelBuffer? TryCapture(MonitorDescriptor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint a = 0; factory.EnumAdapters1(a, out IDXGIAdapter1? adapter).Success && adapter is not null; a++)
        {
            using (adapter)
            {
                for (uint o = 0; adapter.EnumOutputs(o, out IDXGIOutput? output).Success && output is not null; o++)
                {
                    using (output)
                    {
                        var description = output.Description;
                        bool match = description.Monitor == monitor.Handle ||
                            (description.DesktopCoordinates.Left == monitor.Bounds.X && description.DesktopCoordinates.Top == monitor.Bounds.Y);
                        if (!match) continue;
                        // A rotated display hands back the image in panel orientation; let the caller fall back instead.
                        if (description.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified)) return null;
                        return Duplicate(adapter, output, monitor);
                    }
                }
            }
        }

        return null;
    }

    private static PixelBuffer? Duplicate(IDXGIAdapter1 adapter, IDXGIOutput output, MonitorDescriptor monitor)
    {
        using var device = D3DHelpers.CreateDevice(adapter);
        if (device is null) return null;
        using var output1 = output.QueryInterfaceOrNull<IDXGIOutput1>();
        if (output1 is null) return null;
        using var duplication = output1.DuplicateOutput(device);

        // The first frames after duplication starts can be empty (no image yet); wait briefly for a real one.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            var result = duplication.AcquireNextFrame(120, out OutduplFrameInfo info, out IDXGIResource? resource);
            if (result.Failure || resource is null)
            {
                // A timeout only means nothing changed on screen yet.
                if (result == Vortice.DXGI.ResultCode.WaitTimeout) continue;
                return null;
            }

            try
            {
                if (info.LastPresentTime == 0 && attempt < 7) continue;
                using var texture = resource.QueryInterface<ID3D11Texture2D>();
                var image = D3DHelpers.ReadTexture(device, texture, monitor.Bounds.Width, monitor.Bounds.Height);
                if (image is not null && image.Width == monitor.Bounds.Width && image.Height == monitor.Bounds.Height) return image;
                return null;
            }
            finally
            {
                resource.Dispose();
                duplication.ReleaseFrame();
            }
        }

        return null;
    }
}

/// <summary>
/// Windows Graphics Capture. Used when Desktop Duplication is unavailable (for example in some remote
/// and virtual sessions). On Windows 10 the system draws a coloured border around the captured
/// monitor while it runs, which is why this backend is never the default.
/// </summary>
public sealed class WindowsGraphicsCaptureBackend : IMonitorCaptureBackend
{
    private static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid Texture2DIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        nint CreateForWindow(nint window, in Guid iid);

        nint CreateForMonitor(nint monitor, in Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        nint GetInterface(in Guid iid);
    }

    [DllImport("d3d11.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    public string Name => "WGC";

    public bool IsAvailable
    {
        get
        {
            try
            {
                return GraphicsCaptureSession.IsSupported();
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    public PixelBuffer? TryCapture(MonitorDescriptor monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        using var device = D3DHelpers.CreateDevice(null);
        if (device is null) return null;

        nint winrtDevicePointer = 0;
        try
        {
            using var dxgiDevice = device.QueryInterface<IDXGIDevice>();
            if (CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out winrtDevicePointer) < 0 || winrtDevicePointer == 0) return null;
            var winrtDevice = WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(winrtDevicePointer);

            var interop = GraphicsCaptureItem.As<IGraphicsCaptureItemInterop>();
            nint itemPointer = interop.CreateForMonitor(monitor.Handle, GraphicsCaptureItemIid);
            if (itemPointer == 0) return null;
            GraphicsCaptureItem item;
            try { item = GraphicsCaptureItem.FromAbi(itemPointer); }
            finally { Marshal.Release(itemPointer); }

            using var arrived = new ManualResetEventSlim(false);
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(winrtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
            pool.FrameArrived += (_, _) => arrived.Set();
            using var session = pool.CreateCaptureSession(item);
            // Both properties exist only on newer Windows; older builds keep the cursor and the border.
            try { session.IsCursorCaptureEnabled = false; }
            catch (Exception) { }
            try { session.IsBorderRequired = false; }
            catch (Exception) { }

            session.StartCapture();
            if (!arrived.Wait(TimeSpan.FromMilliseconds(1500))) return null;
            using var frame = pool.TryGetNextFrame();
            if (frame is null) return null;

            var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
            nint texturePointer = access.GetInterface(Texture2DIid);
            if (texturePointer == 0) return null;
            using var texture = new ID3D11Texture2D(texturePointer);
            var image = D3DHelpers.ReadTexture(device, texture, frame.ContentSize.Width, frame.ContentSize.Height);
            if (image is null || image.Width != monitor.Bounds.Width || image.Height != monitor.Bounds.Height) return null;
            return image;
        }
        finally
        {
            if (winrtDevicePointer != 0) Marshal.Release(winrtDevicePointer);
        }
    }
}

public static class CaptureBackends
{
    /// <summary>The GPU backends in the order they are tried: no border first.</summary>
    public static IReadOnlyList<IMonitorCaptureBackend> CreateGpuBackends() => [new DxgiDuplicationBackend(), new WindowsGraphicsCaptureBackend()];
}
