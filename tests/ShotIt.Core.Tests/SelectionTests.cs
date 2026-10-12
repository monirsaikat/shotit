using ShotIt.Core.Geometry;
using ShotIt.Core.Selection;

namespace ShotIt.Core.Tests;

public class SelectionModelTests
{
    // Two monitors, the left one at negative coordinates.
    private static readonly PixelRect Bounds = new(-1920, 0, 3840, 1080);

    [Fact]
    public void Drag_in_any_direction_gives_normalized_region()
    {
        var m = new SelectionModel(Bounds);
        m.BeginDrag(new PixelPoint(100, 100));
        m.DragTo(new PixelPoint(-200, 50));
        Assert.True(m.EndDrag());
        Assert.Equal(new PixelRect(-200, 50, 300, 50), m.Current);
    }

    [Fact]
    public void Tiny_drag_is_a_click_not_a_region()
    {
        var m = new SelectionModel(Bounds);
        m.BeginDrag(new PixelPoint(100, 100));
        m.DragTo(new PixelPoint(102, 101));
        Assert.False(m.EndDrag());
        Assert.Null(m.Current);
    }

    [Fact]
    public void Drag_is_clamped_to_bounds()
    {
        var m = new SelectionModel(Bounds);
        m.BeginDrag(new PixelPoint(-1900, 10));
        m.DragTo(new PixelPoint(-99999, 99999));
        m.EndDrag();
        Assert.Equal(new PixelRect(-1920, 10, 20, 1070), m.Current);
    }

    [Fact]
    public void Square_and_from_center_constraints()
    {
        var m = new SelectionModel(Bounds);
        m.BeginDrag(new PixelPoint(500, 500));
        m.DragTo(new PixelPoint(600, 530), square: true);
        Assert.Equal(new PixelRect(500, 500, 100, 100), m.Current);
        m.DragTo(new PixelPoint(600, 530), fromCenter: true);
        Assert.Equal(new PixelRect(400, 470, 200, 60), m.Current);
    }

    [Fact]
    public void Space_drag_moves_the_anchor()
    {
        var m = new SelectionModel(Bounds);
        m.BeginDrag(new PixelPoint(100, 100));
        m.DragTo(new PixelPoint(200, 200));
        m.MoveAnchor(50, 50);
        m.DragTo(new PixelPoint(250, 250));
        Assert.Equal(new PixelRect(150, 150, 100, 100), m.Current);
    }

    [Fact]
    public void Hit_test_distinguishes_handles_inside_and_outside()
    {
        var m = new SelectionModel(Bounds);
        m.SetRegion(new PixelRect(100, 100, 200, 100));
        Assert.Equal(SelectionHit.TopLeft, m.HitTest(new PixelPoint(101, 99), 6));
        Assert.Equal(SelectionHit.BottomRight, m.HitTest(new PixelPoint(300, 200), 6));
        Assert.Equal(SelectionHit.Top, m.HitTest(new PixelPoint(200, 102), 6));
        Assert.Equal(SelectionHit.Left, m.HitTest(new PixelPoint(98, 150), 6));
        Assert.Equal(SelectionHit.Inside, m.HitTest(new PixelPoint(200, 150), 6));
        Assert.Equal(SelectionHit.None, m.HitTest(new PixelPoint(400, 150), 6));
        Assert.Equal(SelectionHit.None, new SelectionModel(Bounds).HitTest(new PixelPoint(0, 0), 6));
    }

    [Fact]
    public void Moving_keeps_size_and_stays_inside_bounds()
    {
        var m = new SelectionModel(Bounds);
        m.SetRegion(new PixelRect(100, 100, 200, 100));
        m.BeginAdjust(SelectionHit.Inside, new PixelPoint(150, 150));
        m.AdjustTo(new PixelPoint(-99999, -99999));
        Assert.Equal(new PixelRect(-1920, 0, 200, 100), m.Current);
        m.AdjustTo(new PixelPoint(99999, 99999));
        Assert.Equal(new PixelRect(1720, 980, 200, 100), m.Current);
        m.EndAdjust();
        Assert.False(m.IsAdjusting);
    }

    [Fact]
    public void Resizing_by_handle_can_flip_and_is_clamped()
    {
        var m = new SelectionModel(Bounds);
        m.SetRegion(new PixelRect(100, 100, 200, 100));
        m.BeginAdjust(SelectionHit.Right, new PixelPoint(300, 150));
        m.AdjustTo(new PixelPoint(50, 150));
        Assert.Equal(new PixelRect(50, 100, 50, 100), m.Current);
        m.AdjustTo(new PixelPoint(99999, 150));
        Assert.Equal(new PixelRect(100, 100, 1820, 100), m.Current);
    }

    [Fact]
    public void Resizing_never_collapses_to_zero()
    {
        var m = new SelectionModel(Bounds);
        m.SetRegion(new PixelRect(100, 100, 200, 100));
        m.BeginAdjust(SelectionHit.Right, new PixelPoint(300, 150));
        m.AdjustTo(new PixelPoint(100, 150));
        Assert.True(m.Current!.Value.Width >= 1);
    }

    [Fact]
    public void Nudge_and_resize_by_keyboard()
    {
        var m = new SelectionModel(Bounds);
        m.SetRegion(new PixelRect(-1920, 0, 100, 100));
        m.Nudge(-10, -10);
        Assert.Equal(new PixelRect(-1920, 0, 100, 100), m.Current);
        m.Nudge(5, 7);
        Assert.Equal(new PixelRect(-1915, 7, 100, 100), m.Current);
        m.ResizeBy(-1000, 10);
        Assert.Equal(new PixelRect(-1915, 7, 1, 110), m.Current);
        m.ResizeBy(100000, 100000);
        Assert.Equal(PixelRect.FromLTRB(-1915, 7, 1920, 1080), m.Current);
    }

    [Fact]
    public void SetRegion_clips_and_rejects_outside()
    {
        var m = new SelectionModel(Bounds);
        m.SetRegion(new PixelRect(1900, 1000, 500, 500));
        Assert.Equal(new PixelRect(1900, 1000, 20, 80), m.Current);
        m.SetRegion(new PixelRect(5000, 5000, 10, 10));
        Assert.Null(m.Current);
    }

    [Fact]
    public void Changed_fires_only_on_real_change()
    {
        var m = new SelectionModel(Bounds);
        int count = 0;
        m.Changed += (_, _) => count++;
        m.SetRegion(new PixelRect(0, 0, 10, 10));
        m.SetRegion(new PixelRect(0, 0, 10, 10));
        Assert.Equal(1, count);
    }
}

public class ToolbarPlacementTests
{
    private static readonly RectD Area = new(0, 0, 1920, 1080);

    private static void AssertInside(Vec2 p, double w, double h)
    {
        Assert.True(p.X >= Area.Left && p.X + w <= Area.Right, $"x={p.X}");
        Assert.True(p.Y >= Area.Top && p.Y + h <= Area.Bottom, $"y={p.Y}");
    }

    [Fact]
    public void Prefers_below_right_aligned()
    {
        var p = ToolbarPlacement.Compute(new RectD(400, 300, 600, 300), 420, 40, Area);
        Assert.Equal(new Vec2(580, 608), p);
    }

    [Fact]
    public void Goes_above_when_no_room_below()
    {
        var p = ToolbarPlacement.Compute(new RectD(400, 700, 600, 370), 420, 40, Area);
        Assert.Equal(700 - 8 - 40, p.Y);
    }

    [Fact]
    public void Goes_inside_for_full_screen_selection()
    {
        var p = ToolbarPlacement.Compute(Area, 420, 40, Area);
        Assert.Equal(1080 - 40 - 8, p.Y);
        AssertInside(p, 420, 40);
    }

    [Fact]
    public void Always_stays_on_the_monitor_for_any_selection_position()
    {
        for (int x = -300; x <= 2000; x += 97)
        {
            for (int y = -300; y <= 1200; y += 83)
            {
                foreach (var (w, h) in new[] { (5.0, 5.0), (300.0, 200.0), (1920.0, 1080.0) })
                {
                    var p = ToolbarPlacement.Compute(new RectD(x, y, w, h), 420, 76, Area);
                    AssertInside(p, 420, 76);
                }
            }
        }
    }
}
