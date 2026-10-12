using ShotIt.Core.Display;
using ShotIt.Core.Geometry;

namespace ShotIt.Core.Tests;

public class GeometryTests
{
    [Fact]
    public void FromCorners_normalizes_any_drag_direction()
    {
        var expected = new PixelRect(-50, -20, 150, 60);
        Assert.Equal(expected, PixelRect.FromCorners(new(-50, -20), new(100, 40)));
        Assert.Equal(expected, PixelRect.FromCorners(new(100, 40), new(-50, -20)));
        Assert.Equal(expected, PixelRect.FromCorners(new(100, -20), new(-50, 40)));
    }

    [Fact]
    public void Intersect_handles_negative_coordinates()
    {
        var left = new PixelRect(-1920, 0, 1920, 1080);
        var selection = new PixelRect(-100, 100, 300, 300);
        Assert.Equal(new PixelRect(-100, 100, 100, 300), left.Intersect(selection));
    }

    [Fact]
    public void Intersect_of_disjoint_rects_is_empty()
    {
        Assert.True(new PixelRect(0, 0, 10, 10).Intersect(new PixelRect(10, 0, 10, 10)).IsEmpty);
    }

    [Fact]
    public void Union_ignores_empty()
    {
        var r = new PixelRect(5, 5, 10, 10);
        Assert.Equal(r, PixelRect.Empty.Union(r));
        Assert.Equal(r, r.Union(PixelRect.Empty));
    }

    [Fact]
    public void MoveInside_keeps_size_when_it_fits()
    {
        var bounds = new PixelRect(-1920, -200, 3840, 1280);
        Assert.Equal(new PixelRect(-1920, -200, 100, 100), new PixelRect(-5000, -5000, 100, 100).MoveInside(bounds));
        Assert.Equal(new PixelRect(1820, 980, 100, 100), new PixelRect(5000, 5000, 100, 100).MoveInside(bounds));
    }

    [Fact]
    public void MoveInside_shrinks_oversized_rect()
    {
        var bounds = new PixelRect(0, 0, 100, 100);
        Assert.Equal(bounds, new PixelRect(-50, -50, 500, 500).MoveInside(bounds));
    }

    [Fact]
    public void Contains_treats_right_and_bottom_as_exclusive()
    {
        var r = new PixelRect(0, 0, 10, 10);
        Assert.True(r.Contains(new PixelPoint(9, 9)));
        Assert.False(r.Contains(new PixelPoint(10, 9)));
        Assert.False(r.Contains(new PixelPoint(9, 10)));
    }

    [Fact]
    public void Affine_MapRect_maps_corners_exactly()
    {
        var from = new RectD(10, 20, 100, 50);
        var to = new RectD(-30, 5, 200, 25);
        var t = Affine2.MapRect(from, to);
        Assert.Equal(to.TopLeft, t.Apply(from.TopLeft));
        Assert.Equal(to.BottomRight.X, t.Apply(from.BottomRight).X, 9);
        Assert.Equal(to.BottomRight.Y, t.Apply(from.BottomRight).Y, 9);
    }

    [Fact]
    public void Affine_MapRect_survives_zero_sized_source()
    {
        var t = Affine2.MapRect(new RectD(5, 5, 0, 40), new RectD(5, 5, 20, 80));
        Assert.Equal(1, t.Sx);
        Assert.Equal(2, t.Sy);
    }

    [Theory]
    [InlineData(10, 1, 10, 0)]
    [InlineData(10, 9, 9.5, 9.5)]
    [InlineData(-1, -10, 0, -10)]
    public void SnapAngle45_snaps_to_nearest_octant(double x, double y, double ex, double ey)
    {
        var snapped = GeometryMath.SnapAngle45(new Vec2(0, 0), new Vec2(x, y));
        double len = Math.Sqrt(x * x + y * y);
        var expected = new Vec2(ex, ey).Normalized() * len;
        Assert.Equal(expected.X, snapped.X, 6);
        Assert.Equal(expected.Y, snapped.Y, 6);
    }

    [Fact]
    public void DistanceToSegment_clamps_to_endpoints()
    {
        Assert.Equal(5, GeometryMath.DistanceToSegment(new Vec2(-3, 4), new Vec2(0, 0), new Vec2(10, 0)), 9);
        Assert.Equal(2, GeometryMath.DistanceToSegment(new Vec2(5, 2), new Vec2(0, 0), new Vec2(10, 0)), 9);
        Assert.Equal(1, GeometryMath.DistanceToSegment(new Vec2(1, 0), new Vec2(0, 0), new Vec2(0, 0)), 9);
    }

    [Fact]
    public void PointInPolygon_works_for_concave_shape()
    {
        Vec2[] l = [new(0, 0), new(10, 0), new(10, 4), new(4, 4), new(4, 10), new(0, 10)];
        Assert.True(GeometryMath.PointInPolygon(new Vec2(2, 8), l));
        Assert.False(GeometryMath.PointInPolygon(new Vec2(8, 8), l));
    }

    [Fact]
    public void RectD_ToPixelRectOuter_covers_fractional_edges()
    {
        Assert.Equal(new PixelRect(-2, 0, 4, 3), new RectD(-1.2, 0.5, 2.4, 2.1).ToPixelRectOuter());
    }
}

public class MonitorTests
{
    // Layout: a 1080p monitor at 150 % left of (and partly above) a 4K primary at 200 %.
    private static MonitorTopology Mixed() => new(
    [
        new MonitorDescriptor(1, "L", new PixelRect(-1920, -300, 1920, 1080), new PixelRect(-1920, -300, 1920, 1032), 144, false),
        new MonitorDescriptor(2, "P", new PixelRect(0, 0, 3840, 2160), new PixelRect(0, 0, 3840, 2100), 192, true),
    ]);

    [Fact]
    public void VirtualBounds_spans_negative_origin()
    {
        Assert.Equal(new PixelRect(-1920, -300, 5760, 2460), Mixed().VirtualBounds);
    }

    [Fact]
    public void FromPoint_finds_monitor_or_nearest_in_gaps()
    {
        var t = Mixed();
        Assert.Equal("L", t.FromPoint(new PixelPoint(-1, 0)).DeviceName);
        Assert.Equal("P", t.FromPoint(new PixelPoint(0, 0)).DeviceName);
        // Below the left monitor there is nothing; the nearest monitor wins.
        Assert.Equal("L", t.FromPoint(new PixelPoint(-1000, 1500)).DeviceName);
        Assert.Equal("P", t.FromPoint(new PixelPoint(-10, 2000)).DeviceName);
    }

    [Fact]
    public void FromRect_picks_largest_overlap()
    {
        var t = Mixed();
        Assert.Equal("P", t.FromRect(new PixelRect(-100, 100, 500, 100)).DeviceName);
        Assert.Equal("L", t.FromRect(new PixelRect(-400, 100, 500, 100)).DeviceName);
    }

    [Fact]
    public void Primary_falls_back_to_first_monitor()
    {
        var t = new MonitorTopology([new MonitorDescriptor(1, "A", new PixelRect(0, 0, 800, 600), new PixelRect(0, 0, 800, 600), 96, false)]);
        Assert.Equal("A", t.Primary.DeviceName);
    }

    [Fact]
    public void Empty_topology_is_rejected()
    {
        Assert.Throws<ArgumentException>(() => new MonitorTopology([]));
    }

    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(168)]
    [InlineData(192)]
    [InlineData(240)]
    public void Pixel_to_dip_round_trip_is_exact_for_every_pixel(int dpi)
    {
        var m = new MonitorDescriptor(1, "M", new PixelRect(-2560, -1440, 2560, 1440), new PixelRect(-2560, -1440, 2560, 1400), dpi, false);
        for (int x = m.Bounds.Left; x <= m.Bounds.Right; x += 7)
        {
            for (int y = m.Bounds.Top; y <= m.Bounds.Bottom; y += 13)
            {
                var p = new PixelPoint(x, y);
                Assert.Equal(p, DpiMath.FromMonitorDip(DpiMath.ToMonitorDip(p, m), m));
            }
        }
    }
}
