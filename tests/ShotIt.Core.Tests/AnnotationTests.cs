using System.Collections.Immutable;
using System.Text.Json;
using ShotIt.Core.Annotations;
using ShotIt.Core.Editing;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;

namespace ShotIt.Core.Tests;

public class AnnotationTests
{
    public static IEnumerable<object[]> AllKinds() => Samples().Select(a => new object[] { a });

    internal static IEnumerable<Annotation> Samples() =>
    [
        new ArrowAnnotation { Start = new(10, 10), End = new(110, 60) },
        new ArrowAnnotation { Start = new(10, 10), End = new(110, 60), Control = new Vec2(40, 80), DoubleHeaded = true },
        new LineAnnotation { Points = [new(0, 0), new(50, 50), new(100, 0)] },
        new ShapeAnnotation { Rect = new(20, 20, 100, 60), Kind = ShapeKind.Rectangle },
        new ShapeAnnotation { Rect = new(20, 20, 100, 60), Kind = ShapeKind.Ellipse, Fill = ColorRgba.Red.WithAlpha(70) },
        new FreehandAnnotation { Points = [new(5, 5), new(9, 12), new(30, 44)] },
        new HighlighterAnnotation { Points = [new(5, 50), new(200, 50)] },
        new AreaHighlightAnnotation { Rect = new(5, 5, 50, 20) },
        new SpotlightAnnotation { Rect = new(40, 40, 80, 80), Ellipse = true },
        new TextAnnotation { Position = new(30, 30), Text = "Hello\nworld" },
        new CalloutAnnotation { Box = new(50, 50, 120, 60), Tail = new(60, 160), Text = "Look" },
        new StepAnnotation { Center = new(70, 70), Number = 3 },
        new MagnifierAnnotation { Source = new(10, 10, 40, 30), Dest = new(100, 10, 80, 60) },
        new BlurAnnotation { Rect = new(0, 0, 64, 64) },
        new PixelateAnnotation { Rect = new(0, 0, 64, 64) },
        new RedactAnnotation { Rect = new(0, 0, 64, 16) },
        new StickerAnnotation { Rect = new(10, 10, 48, 48) },
        new ImageAnnotation { Rect = new(10, 10, 48, 48), EncodedImage = [1, 2, 3] },
        new MeasureAnnotation { Start = new(0, 0), End = new(30, 40) },
    ];

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Move_shifts_bounds_by_exactly_the_delta(Annotation a)
    {
        var moved = a.MoveBy(new Vec2(-37, 12));
        Assert.Equal(a.Bounds.X - 37, moved.Bounds.X, 6);
        Assert.Equal(a.Bounds.Y + 12, moved.Bounds.Y, 6);
        Assert.Equal(a.Bounds.Width, moved.Bounds.Width, 6);
        Assert.Equal(a.Bounds.Height, moved.Bounds.Height, 6);
        Assert.Equal(a.Id, moved.Id);
        Assert.Equal(a.GetType(), moved.GetType());
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Move_there_and_back_restores_geometry(Annotation a)
    {
        var back = a.MoveBy(new Vec2(100, -50)).MoveBy(new Vec2(-100, 50));
        Assert.Equal(a.Bounds.X, back.Bounds.X, 6);
        Assert.Equal(a.Bounds.Y, back.Bounds.Y, 6);
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Every_handle_can_be_dragged_without_throwing(Annotation a)
    {
        foreach (var h in a.GetHandles())
        {
            var moved = a.MoveHandle(h.Id, h.Position + new Vec2(15, -9), constrain: false);
            Assert.NotNull(moved);
            Assert.Equal(a.Id, moved.Id);
            var constrained = a.MoveHandle(h.Id, h.Position + new Vec2(-200, 300), constrain: true);
            Assert.False(double.IsNaN(constrained.Bounds.Width));
            Assert.False(double.IsNaN(constrained.Bounds.Height));
        }
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Hit_test_hits_the_annotation_and_misses_far_away(Annotation a)
    {
        Assert.False(a.HitTest(new Vec2(-5000, -5000), 4));
        var probe = a switch
        {
            SegmentAnnotation s => Vec2.Lerp(s.Start, s.End, s is ArrowAnnotation { Control: not null } ? 0 : 0.5),
            PathAnnotation p => p.Points[0],
            ShapeAnnotation { Fill: null } s => s.Kind == ShapeKind.Ellipse ? new Vec2(s.Rect.Left, s.Rect.Center.Y) : s.Rect.TopLeft,
            _ => a.Bounds.Center,
        };
        Assert.True(a.HitTest(probe, 4), $"{a.GetType().Name} should be hit at {probe}");
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Json_round_trip_preserves_type_id_and_bounds(Annotation a)
    {
        string json = JsonSerializer.Serialize(a);
        var back = JsonSerializer.Deserialize<Annotation>(json)!;
        Assert.Equal(a.GetType(), back.GetType());
        Assert.Equal(a.Id, back.Id);
        Assert.Equal(a.Bounds, back.Bounds);
    }

    [Fact]
    public void Unfilled_rectangle_is_not_hit_in_the_middle()
    {
        var s = new ShapeAnnotation { Rect = new(0, 0, 200, 200), Kind = ShapeKind.Rectangle };
        Assert.False(s.HitTest(new Vec2(100, 100), 4));
        Assert.True(s.HitTest(new Vec2(1, 100), 4));
        Assert.True((s with { Fill = ColorRgba.Red }).HitTest(new Vec2(100, 100), 4));
    }

    [Fact]
    public void Rect_handle_resize_flips_cleanly_past_opposite_edge()
    {
        var r = RectHandles.Resize(new RectD(10, 10, 100, 50), RectHandles.Right, new Vec2(-40, 0));
        Assert.Equal(new RectD(-40, 10, 50, 50), r);
    }

    [Fact]
    public void Rect_handle_resize_keeps_aspect_on_corners()
    {
        var r = RectHandles.Resize(new RectD(0, 0, 200, 100), RectHandles.BottomRight, new Vec2(300, 110), keepAspect: true);
        Assert.Equal(2, r.Width / r.Height, 6);
        Assert.Equal(0, r.Left, 6);
        Assert.Equal(0, r.Top, 6);
    }

    [Fact]
    public void Arrow_bend_handle_snaps_back_to_straight()
    {
        var arrow = new ArrowAnnotation { Start = new(0, 0), End = new(100, 0) };
        var bent = (ArrowAnnotation)arrow.MoveHandle(ArrowAnnotation.ControlHandle, new Vec2(50, 40), false);
        Assert.NotNull(bent.Control);
        // The curve passes through the dragged point at its midpoint.
        var mid = GeometryMath.QuadraticBezier(bent.Start, bent.BezierControl, bent.End, 0.5);
        Assert.Equal(50, mid.X, 6);
        Assert.Equal(40, mid.Y, 6);
        var straight = (ArrowAnnotation)bent.MoveHandle(ArrowAnnotation.ControlHandle, new Vec2(50, 1), false);
        Assert.Null(straight.Control);
    }

    [Fact]
    public void Segment_endpoint_constrain_snaps_to_45_degrees()
    {
        var arrow = new ArrowAnnotation { Start = new(0, 0), End = new(100, 0) };
        var moved = (ArrowAnnotation)arrow.MoveHandle(SegmentAnnotation.EndHandle, new Vec2(100, 5), true);
        Assert.Equal(0, moved.End.Y, 6);
    }

    [Fact]
    public void Measure_reports_length_and_angle()
    {
        var m = new MeasureAnnotation { Start = new(0, 0), End = new(30, -40) };
        Assert.Equal(50, m.Length, 6);
        Assert.Equal(53.13, m.AngleDegrees, 2);
    }

    [Fact]
    public void Text_resize_scales_font_and_clamps()
    {
        var t = new TextAnnotation { Position = new(0, 0), Text = "x", FontSize = 20 };
        var bigger = (TextAnnotation)t.MoveHandle(RectHandles.BottomRight, new Vec2(0, t.Bounds.Height * 2), false);
        Assert.Equal(40, bigger.FontSize, 6);
        var huge = (TextAnnotation)t.MoveHandle(RectHandles.BottomRight, new Vec2(0, 1e9), false);
        Assert.Equal(400, huge.FontSize);
    }

    [Fact]
    public void Document_json_round_trip_keeps_order()
    {
        var items = Samples().ToImmutableList();
        var back = JsonSerializer.Deserialize<ImmutableList<Annotation>>(JsonSerializer.Serialize(items))!;
        Assert.Equal(items.Select(i => i.Id), back.Select(i => i.Id));
    }
}

public class DocStateTests
{
    private static DocState Doc() => DocState.FromImage(new PixelBuffer(200, 100));

    [Fact]
    public void Crop_translates_annotations_with_the_pixels()
    {
        var doc = Doc().Add(new ShapeAnnotation { Rect = new(50, 40, 20, 10) });
        var cropped = doc.Crop(new PixelRect(30, 30, 100, 50));
        Assert.Equal(new PixelSize(100, 50), cropped.Size);
        Assert.Equal(new RectD(20, 10, 20, 10), cropped.Items[0].Bounds);
    }

    [Fact]
    public void Crop_outside_or_full_is_a_no_op()
    {
        var doc = Doc();
        Assert.Same(doc, doc.Crop(new PixelRect(500, 500, 10, 10)));
        Assert.Same(doc, doc.Crop(new PixelRect(-10, -10, 1000, 1000)));
    }

    [Fact]
    public void Step_numbers_continue_from_highest()
    {
        var doc = Doc();
        Assert.Equal(1, doc.NextStepNumber());
        doc = doc.Add(new StepAnnotation { Number = 1 }).Add(new StepAnnotation { Number = 5 });
        Assert.Equal(6, doc.NextStepNumber());
    }
}
