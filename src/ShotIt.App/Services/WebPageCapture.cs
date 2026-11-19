using System.Windows.Automation;
using ShotIt.Core.Geometry;
using ShotIt.Native;

namespace ShotIt.App.Services;

/// <summary>
/// Finds the web page inside a browser window: the part that scrolls, without the tabs, address bar
/// and scrollbar. Chromium browsers (Chrome, Edge, Brave, Opera, Vivaldi) give the page its own child
/// window; for the others (Firefox) the page is found through UI Automation.
/// </summary>
internal static class WebPageCapture
{
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "zen", "librewolf",
        "waterfox", "floorp", "thorium", "chromium", "yandex", "browser", "iexplore",
    };

    public static bool IsBrowser(NativeWindow window) => Browsers.Contains(WindowList.GetProcessName(window.ProcessId));

    /// <summary>
    /// The browser to capture: the foreground window when it is a browser, otherwise the front-most browser
    /// window (the tray menu takes the foreground away from it).
    /// </summary>
    public static NativeWindow? FindBrowser(nint foreground)
    {
        if (WindowList.DescribeWindow(foreground) is { } active && IsBrowser(active)) return active;
        return WindowList.EnumerateTopLevel((uint)Environment.ProcessId)
            .FirstOrDefault(w => w.Title.Length > 0 && w.Bounds.Width >= 300 && w.Bounds.Height >= 200 && IsBrowser(w));
    }

    /// <summary>The page area in screen pixels, or null when it cannot be found.</summary>
    public static async Task<PixelRect?> FindPageAreaAsync(NativeWindow browser)
    {
        var window = WindowList.GetBounds(browser.Handle);
        PixelRect? page = WindowList.EnumerateChildren(browser.Handle)
            .Where(c => c.ClassName == "Chrome_RenderWidgetHostHWND" && c.Bounds.Width >= 200 && c.Bounds.Height >= 150)
            .OrderByDescending(c => (long)c.Bounds.Width * c.Bounds.Height)
            .Select(c => (PixelRect?)c.Bounds)
            .FirstOrDefault();

        // UI Automation can be slow on a large page, so it runs off the UI thread and is given a time limit.
        page ??= await Task.Run(() => FindDocument(browser.Handle)).WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(true);
        if (page is not { } found) return null;

        var area = found.Intersect(window);
        if (area.Width < 200 || area.Height < 150) return null;

        // The page's own scrollbar would be stitched in at every position its thumb passed through.
        int scrollbar = (int)Math.Round(17 * WindowInterop.GetDpi(browser.Handle) / 96.0);
        return new PixelRect(area.X, area.Y, area.Width - scrollbar, area.Height);
    }

    private static PixelRect? FindDocument(nint hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var document = root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            if (document is null) return null;
            var r = document.Current.BoundingRectangle;
            if (r.IsEmpty || r.Width < 1 || r.Height < 1) return null;
            return new PixelRect((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// Scrolls the page back to its top: Home first (instant on most pages), then the wheel until the
    /// picture stops changing, for pages where Home is taken by a text box.
    /// </summary>
    public static async Task ScrollToTopAsync(PixelRect area)
    {
        var center = new PixelPoint(area.X + area.Width / 2, area.Y + area.Height / 2);
        KeyboardInput.Tap(KeyboardInput.Home);
        await Task.Delay(250).ConfigureAwait(true);

        var before = GdiScreen.Capture(area);
        for (int round = 0; round < 25; round++)
        {
            MouseInput.MoveTo(center);
            MouseInput.WheelUp(30);
            await Task.Delay(120).ConfigureAwait(true);
            var after = GdiScreen.Capture(area);
            if (after.Data.AsSpan().SequenceEqual(before.Data)) break;
            before = after;
        }

        // Let smooth scrolling come to rest before the first picture is taken.
        await Task.Delay(200).ConfigureAwait(true);
    }
}
