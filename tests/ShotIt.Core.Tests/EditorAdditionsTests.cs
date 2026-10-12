using ShotIt.Core.Annotations;
using ShotIt.Core.Editing;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using ShotIt.Core.Naming;

namespace ShotIt.Core.Tests;

public class EditorAdditionsTests
{
    private static EditorSession Session() => new(DocState.FromImage(new PixelBuffer(400, 300)));

    private static void Drag(EditorSession s, Vec2 from, Vec2 to, PointerModifiers mods = PointerModifiers.None)
    {
        s.PointerDown(from, mods, 4);
        s.PointerMove(Vec2.Lerp(from, to, 0.5), mods);
        s.PointerMove(to, mods);
        s.PointerUp(to, mods);
    }

    [Fact]
    public void Eraser_removes_touched_strokes_only_and_is_one_undo_step()
    {
        var s = Session();
        s.SetTool(ToolKind.Pen);
        Drag(s, new(20, 20), new(120, 20));
        Drag(s, new(20, 60), new(120, 60));
        s.SetTool(ToolKind.Highlighter);
        Drag(s, new(20, 100), new(120, 100));
        s.SetTool(ToolKind.Rectangle);
        Drag(s, new(10, 10), new(200, 150));
        Assert.Equal(4, s.State.Items.Count);

        s.SetTool(ToolKind.Eraser);
        Drag(s, new(70, 10), new(70, 110)); // crosses all three strokes and lies inside the rectangle
        Assert.Single(s.State.Items);
        Assert.IsType<ShapeAnnotation>(s.State.Items[0]);

        s.Undo();
        Assert.Equal(4, s.State.Items.Count);
    }

    [Fact]
    public void Eraser_that_touches_nothing_adds_no_undo_step()
    {
        var s = Session();
        s.SetTool(ToolKind.Eraser);
        Drag(s, new(300, 200), new(350, 250));
        Assert.False(s.CanUndo);
    }

    [Fact]
    public void Rounded_rectangle_tool_uses_the_corner_radius_option()
    {
        var s = Session();
        s.SetOptions(s.Options with { CornerRadius = 20, Dashed = true });
        s.SetTool(ToolKind.RoundedRectangle);
        Drag(s, new(10, 10), new(110, 80));
        var shape = Assert.IsType<ShapeAnnotation>(Assert.Single(s.State.Items));
        Assert.Equal(ShapeKind.RoundedRectangle, shape.Kind);
        Assert.Equal(20, shape.CornerRadius);
        Assert.True(shape.Dashed);
    }

    [Fact]
    public void Arrows_pick_up_the_dashed_option()
    {
        var s = Session();
        s.SetOptions(s.Options with { Dashed = true });
        s.SetTool(ToolKind.Arrow);
        Drag(s, new(10, 10), new(110, 80));
        Assert.True(Assert.IsType<ArrowAnnotation>(s.State.Items[0]).Dashed);
    }

    [Theory]
    [InlineData(16.0 / 9)]
    [InlineData(1.0)]
    [InlineData(9.0 / 16)]
    public void Crop_holds_the_chosen_aspect_ratio(double aspect)
    {
        var s = Session();
        RectD? requested = null;
        s.CropRequested += (_, r) => requested = r;
        s.CropAspect = aspect;
        s.SetTool(ToolKind.Crop);
        Drag(s, new(50, 40), new(210, 100));
        Assert.NotNull(requested);
        Assert.Equal(aspect, requested.Value.Width / requested.Value.Height, 6);
        Assert.Equal(50, requested.Value.Left, 6);
        Assert.Equal(40, requested.Value.Top, 6);
    }

    [Fact]
    public void Double_clicking_a_line_adds_a_corner_in_the_right_segment()
    {
        var s = Session();
        s.AddAnnotation(new LineAnnotation { Points = [new(10, 10), new(110, 10), new(110, 110)] });
        Assert.True(s.PointerDoubleClick(new(110, 60), 4));
        var line = Assert.IsType<LineAnnotation>(s.State.Items[0]);
        Assert.Equal(new[] { new Vec2(10, 10), new Vec2(110, 10), new Vec2(110, 60), new Vec2(110, 110) }, line.Points.ToArray());
        s.Undo();
        Assert.Equal(3, ((LineAnnotation)s.State.Items[0]).Points.Length);
    }

    [Fact]
    public void Forward_and_backward_move_one_step()
    {
        var s = Session();
        var a = new RedactAnnotation { Rect = new(0, 0, 10, 10) };
        var b = new RedactAnnotation { Rect = new(20, 0, 10, 10) };
        var c = new RedactAnnotation { Rect = new(40, 0, 10, 10) };
        s.Apply(s.State.Add(a).Add(b).Add(c));
        s.Select([a.Id]);
        s.BringForward();
        Assert.Equal([b.Id, a.Id, c.Id], s.State.Items.Select(i => i.Id));
        s.BringForward();
        s.BringForward(); // already on top: stays
        Assert.Equal([b.Id, c.Id, a.Id], s.State.Items.Select(i => i.Id));
        s.SendBackward();
        Assert.Equal([b.Id, a.Id, c.Id], s.State.Items.Select(i => i.Id));
    }

    [Fact]
    public void Align_left_lines_up_the_selection()
    {
        var s = Session();
        var a = new RedactAnnotation { Rect = new(30, 0, 10, 10) };
        var b = new RedactAnnotation { Rect = new(80, 50, 40, 10) };
        s.Apply(s.State.Add(a).Add(b));
        s.SelectAll();
        s.AlignSelected(0);
        Assert.All(s.State.Items, i => Assert.Equal(30, i.Bounds.Left, 6));
        s.AlignSelected(5);
        Assert.All(s.State.Items, i => Assert.Equal(60, i.Bounds.Bottom, 6));
    }

    [Fact]
    public void Text_keeps_italic_from_the_options()
    {
        var s = Session();
        TextEditRequest? request = null;
        s.TextEditRequested += (_, r) => request = r;
        s.SetOptions(s.Options with { Italic = true });
        s.SetTool(ToolKind.Text);
        s.PointerDown(new(40, 40), PointerModifiers.None, 4);
        Assert.NotNull(request);
        s.CommitText(request, "hello");
        Assert.True(Assert.IsType<TextAnnotation>(s.State.Items[0]).Italic);
    }

    [Fact]
    public void The_short_counter_token_works()
    {
        string name = FileNameTemplate.Expand("shot {n}-{counter:3}", new FileNameContext(new DateTime(2026, 1, 2), Counter: 7));
        Assert.Equal("shot 7-007", name);
    }
}
