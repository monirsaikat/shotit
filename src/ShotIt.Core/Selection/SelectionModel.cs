using ShotIt.Core.Annotations;
using ShotIt.Core.Geometry;

namespace ShotIt.Core.Selection;

public enum SelectionHit
{
    None = -1,
    TopLeft = RectHandles.TopLeft,
    Top = RectHandles.Top,
    TopRight = RectHandles.TopRight,
    Right = RectHandles.Right,
    BottomRight = RectHandles.BottomRight,
    Bottom = RectHandles.Bottom,
    BottomLeft = RectHandles.BottomLeft,
    Left = RectHandles.Left,
    Inside = 100,
}

/// <summary>
/// The capture region state machine: drag out a rectangle, then move, resize or nudge it.
/// Works entirely in virtual-screen physical pixels and never leaves <see cref="Bounds"/>.
/// </summary>
public sealed class SelectionModel
{
    /// <summary>Smallest drag that counts as a region rather than a click.</summary>
    public const int MinDrag = 4;

    private PixelPoint _anchor;
    private PixelRect _startRect;
    private SelectionHit _adjusting = SelectionHit.None;
    private bool _dragging;

    public SelectionModel(PixelRect bounds)
    {
        if (bounds.IsEmpty) throw new ArgumentException("Selection bounds must not be empty.", nameof(bounds));
        Bounds = bounds;
    }

    public PixelRect Bounds { get; }

    /// <summary>The current region, or null when nothing is selected yet.</summary>
    public PixelRect? Current { get; private set; }

    public bool IsDragging => _dragging;
    public bool IsAdjusting => _adjusting != SelectionHit.None;
    public bool HasSelection => Current is { IsEmpty: false };

    public event EventHandler? Changed;

    private void Set(PixelRect? rect)
    {
        if (Current == rect) return;
        Current = rect;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private PixelPoint Clamp(PixelPoint p) =>
        new(Math.Clamp(p.X, Bounds.Left, Bounds.Right), Math.Clamp(p.Y, Bounds.Top, Bounds.Bottom));

    public void Clear()
    {
        _dragging = false;
        _adjusting = SelectionHit.None;
        Set(null);
    }

    /// <summary>Sets the region directly (window snap, last region, fixed size). Clipped to the bounds.</summary>
    public void SetRegion(PixelRect rect)
    {
        _dragging = false;
        _adjusting = SelectionHit.None;
        var clipped = rect.Intersect(Bounds);
        Set(clipped.IsEmpty ? null : clipped);
    }

    // ------------------------------------------------------------ creating

    public void BeginDrag(PixelPoint p)
    {
        _anchor = Clamp(p);
        _dragging = true;
        _adjusting = SelectionHit.None;
        Set(null);
    }

    /// <param name="p">Current pointer position.</param>
    /// <param name="square">Constrain to a square (Shift).</param>
    /// <param name="fromCenter">Grow symmetrically around the anchor (Ctrl).</param>
    public void DragTo(PixelPoint p, bool square = false, bool fromCenter = false)
    {
        if (!_dragging) return;
        p = Clamp(p);
        int dx = p.X - _anchor.X, dy = p.Y - _anchor.Y;
        if (square)
        {
            int side = Math.Max(Math.Abs(dx), Math.Abs(dy));
            dx = dx < 0 ? -side : side;
            dy = dy < 0 ? -side : side;
        }

        var rect = fromCenter
            ? PixelRect.FromLTRB(_anchor.X - Math.Abs(dx), _anchor.Y - Math.Abs(dy), _anchor.X + Math.Abs(dx), _anchor.Y + Math.Abs(dy))
            : PixelRect.FromCorners(_anchor, new PixelPoint(_anchor.X + dx, _anchor.Y + dy));
        rect = rect.Intersect(Bounds);
        Set(rect.IsEmpty ? null : rect);
    }

    /// <summary>Shifts the anchor while dragging (holding Space), moving the whole rectangle.</summary>
    public void MoveAnchor(int dx, int dy)
    {
        if (!_dragging) return;
        _anchor = Clamp(new PixelPoint(_anchor.X + dx, _anchor.Y + dy));
    }

    /// <summary>Finishes the drag. Returns false when the drag was too small to be a region.</summary>
    public bool EndDrag()
    {
        if (!_dragging) return HasSelection;
        _dragging = false;
        if (Current is { } r && r.Width >= MinDrag && r.Height >= MinDrag) return true;
        Set(null);
        return false;
    }

    // ------------------------------------------------------------ adjusting

    /// <summary>What part of the selection lies under the point. <paramref name="grip"/> is the handle radius in pixels.</summary>
    public SelectionHit HitTest(PixelPoint p, int grip)
    {
        if (Current is not { IsEmpty: false } r) return SelectionHit.None;
        bool nearLeft = Math.Abs(p.X - r.Left) <= grip, nearRight = Math.Abs(p.X - r.Right) <= grip;
        bool nearTop = Math.Abs(p.Y - r.Top) <= grip, nearBottom = Math.Abs(p.Y - r.Bottom) <= grip;
        bool inX = p.X >= r.Left - grip && p.X <= r.Right + grip, inY = p.Y >= r.Top - grip && p.Y <= r.Bottom + grip;
        if (!inX || !inY) return SelectionHit.None;

        if (nearLeft && nearTop) return SelectionHit.TopLeft;
        if (nearRight && nearTop) return SelectionHit.TopRight;
        if (nearRight && nearBottom) return SelectionHit.BottomRight;
        if (nearLeft && nearBottom) return SelectionHit.BottomLeft;
        if (nearTop) return SelectionHit.Top;
        if (nearBottom) return SelectionHit.Bottom;
        if (nearLeft) return SelectionHit.Left;
        if (nearRight) return SelectionHit.Right;
        return r.Contains(p) ? SelectionHit.Inside : SelectionHit.None;
    }

    public void BeginAdjust(SelectionHit hit, PixelPoint p)
    {
        if (hit == SelectionHit.None || Current is not { } r) return;
        _adjusting = hit;
        _anchor = p;
        _startRect = r;
    }

    public void AdjustTo(PixelPoint p, bool keepAspect = false)
    {
        if (_adjusting == SelectionHit.None) return;
        if (_adjusting == SelectionHit.Inside)
        {
            Set(_startRect.Offset(p.X - _anchor.X, p.Y - _anchor.Y).MoveInside(Bounds));
            return;
        }

        p = Clamp(p);
        var resized = RectHandles.Resize(_startRect.ToRectD(), (int)_adjusting, new Vec2(p.X, p.Y), keepAspect)
            .ToPixelRectRounded().Intersect(Bounds);
        if (resized.Width >= 1 && resized.Height >= 1) Set(resized);
    }

    public void EndAdjust() => _adjusting = SelectionHit.None;

    /// <summary>Moves the region by whole pixels (arrow keys).</summary>
    public void Nudge(int dx, int dy)
    {
        if (Current is { } r) Set(r.Offset(dx, dy).MoveInside(Bounds));
    }

    /// <summary>Grows or shrinks the region from its bottom-right corner (Ctrl+arrow keys).</summary>
    public void ResizeBy(int dw, int dh)
    {
        if (Current is not { } r) return;
        int w = Math.Clamp(r.Width + dw, 1, Bounds.Right - r.Left);
        int h = Math.Clamp(r.Height + dh, 1, Bounds.Bottom - r.Top);
        Set(new PixelRect(r.X, r.Y, w, h));
    }
}

/// <summary>Chooses where the floating toolbar goes relative to the capture region.</summary>
public static class ToolbarPlacement
{
    /// <summary>
    /// All values share one coordinate space (DIPs local to the monitor's window).
    /// Preference order: below the region, above it, then inside its bottom edge.
    /// The result always lies fully inside <paramref name="area"/> when the toolbar fits at all.
    /// </summary>
    public static Vec2 Compute(RectD selection, double toolbarWidth, double toolbarHeight, RectD area, double gap = 8)
    {
        double x = selection.Right - toolbarWidth;
        x = Math.Min(x, area.Right - toolbarWidth - gap);
        x = Math.Max(x, area.Left + gap);

        double below = selection.Bottom + gap;
        double above = selection.Top - gap - toolbarHeight;
        double y;
        if (below + toolbarHeight + gap <= area.Bottom) y = below;
        else if (above >= area.Top + gap) y = above;
        else y = Math.Min(selection.Bottom, area.Bottom) - toolbarHeight - gap;

        y = Math.Min(y, area.Bottom - toolbarHeight - gap);
        y = Math.Max(y, area.Top + gap);
        return new Vec2(x, y);
    }
}
