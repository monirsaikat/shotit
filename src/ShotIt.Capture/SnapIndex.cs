using System.Windows.Automation;
using ShotIt.Core.Geometry;
using ShotIt.Native;

namespace ShotIt.Capture;

/// <summary>Something the capture region can snap to: a window or a control inside one.</summary>
public sealed record SnapTarget(PixelRect Bounds, string Label, nint Window, int Depth);

/// <summary>
/// The windows that were on screen when the session started, front to back. Built before the overlay
/// appears, because afterwards the overlay itself would be the answer to every "what is under the cursor".
/// </summary>
public sealed class SnapIndex
{
    private readonly IReadOnlyList<NativeWindow> _windows;
    private readonly PixelRect _bounds;

    public SnapIndex(IReadOnlyList<NativeWindow> windowsFrontToBack, PixelRect virtualBounds)
    {
        _windows = windowsFrontToBack ?? throw new ArgumentNullException(nameof(windowsFrontToBack));
        _bounds = virtualBounds;
    }

    public static SnapIndex Build(PixelRect virtualBounds, uint excludeProcessId) =>
        new(WindowList.EnumerateTopLevel(excludeProcessId), virtualBounds);

    public IReadOnlyList<NativeWindow> Windows => _windows;

    /// <summary>The front-most window under the point, clipped to the screen.</summary>
    public SnapTarget? WindowAt(PixelPoint p)
    {
        foreach (var w in _windows)
        {
            if (!w.Bounds.Contains(p)) continue;
            var visible = w.Bounds.Intersect(_bounds);
            if (visible.IsEmpty) continue;
            return new SnapTarget(visible, w.Title.Length > 0 ? w.Title : w.ClassName, w.Handle, 0);
        }

        return null;
    }

    /// <summary>
    /// The chain of nested targets under the point, from the window itself down to the deepest control.
    /// Index 0 is always the window. Uses child HWNDs first (instant) and then UI Automation.
    /// Safe to call from a background thread; honours cancellation between cross-process calls.
    /// </summary>
    public IReadOnlyList<SnapTarget> ChainAt(PixelPoint p, CancellationToken cancellationToken)
    {
        var top = WindowAt(p);
        if (top is null) return [];
        var chain = new List<SnapTarget> { top };

        try
        {
            AddUiaChain(chain, top, p, cancellationToken);
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or UnauthorizedAccessException
            or System.Runtime.InteropServices.COMException or TimeoutException or ArgumentException)
        {
            // Elevated or unresponsive applications refuse automation; window-level snapping still works.
        }

        if (chain.Count == 1) AddChildWindows(chain, top, p);
        return chain;
    }

    private static void AddChildWindows(List<SnapTarget> chain, SnapTarget top, PixelPoint p)
    {
        // Children containing the point, largest first, each strictly inside the previous one.
        var candidates = WindowList.EnumerateChildren(top.Window)
            .Where(c => c.Bounds.Contains(p))
            .Select(c => c.Bounds.Intersect(top.Bounds))
            .Where(r => !r.IsEmpty)
            .Distinct()
            .OrderByDescending(r => (long)r.Width * r.Height);

        var last = top.Bounds;
        foreach (var rect in candidates)
        {
            if (rect == last || !last.Contains(rect)) continue;
            chain.Add(new SnapTarget(rect, top.Label, top.Window, chain.Count));
            last = rect;
        }
    }

    private static void AddUiaChain(List<SnapTarget> chain, SnapTarget top, PixelPoint p, CancellationToken ct)
    {
        const int MaxDepth = 12;
        var element = AutomationElement.FromHandle(top.Window);
        var point = new System.Windows.Point(p.X, p.Y);
        var last = top.Bounds;

        var request = new CacheRequest { AutomationElementMode = AutomationElementMode.Full, TreeFilter = Automation.ControlViewCondition };
        request.Add(AutomationElement.BoundingRectangleProperty);
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.IsOffscreenProperty);

        for (int depth = 0; depth < MaxDepth; depth++)
        {
            ct.ThrowIfCancellationRequested();
            AutomationElementCollection children;
            using (request.Activate())
                children = element.FindAll(TreeScope.Children, Condition.TrueCondition);

            AutomationElement? best = null;
            var bestRect = PixelRect.Empty;
            long bestArea = long.MaxValue;
            foreach (AutomationElement child in children)
            {
                var r = child.Cached.BoundingRectangle;
                if (r.IsEmpty || double.IsInfinity(r.Width) || double.IsNaN(r.Width) || !r.Contains(point)) continue;
                if (child.Cached.IsOffscreen) continue;
                var rect = PixelRect.FromLTRB((int)Math.Round(r.Left), (int)Math.Round(r.Top), (int)Math.Round(r.Right), (int)Math.Round(r.Bottom)).Intersect(last);
                long area = (long)rect.Width * rect.Height;
                if (rect.Width < 4 || rect.Height < 4 || area >= bestArea) continue;
                best = child;
                bestRect = rect;
                bestArea = area;
            }

            if (best is null) break;
            if (bestRect != last)
            {
                string name = best.Cached.Name ?? string.Empty;
                string type = best.Cached.ControlType?.ProgrammaticName.Replace("ControlType.", string.Empty, StringComparison.Ordinal) ?? string.Empty;
                string label = name.Length is > 0 and <= 60 ? $"{type}: {name}" : type;
                chain.Add(new SnapTarget(bestRect, label, top.Window, chain.Count));
                last = bestRect;
            }

            element = best;
        }
    }
}
