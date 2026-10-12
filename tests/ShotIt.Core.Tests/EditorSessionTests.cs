using ShotIt.Core.Annotations;
using ShotIt.Core.Editing;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;

namespace ShotIt.Core.Tests;

public class EditorSessionTests
{
    private static EditorSession New() => new(DocState.FromImage(new PixelBuffer(400, 300)));

    private static void Drag(EditorSession s, Vec2 from, Vec2 to, PointerModifiers mods = PointerModifiers.None)
    {
        Assert.True(s.PointerDown(from, mods, 4));
        s.PointerMove(Vec2.Lerp(from, to, 0.5), mods);
        s.PointerMove(to, mods);
        s.PointerUp(to, mods);
    }

    public static IEnumerable<object[]> DragTools() =>
        new[]
        {
            ToolKind.Arrow, ToolKind.Line, ToolKind.Rectangle, ToolKind.Ellipse, ToolKind.Pen, ToolKind.Highlighter,
            ToolKind.AreaHighlight, ToolKind.Spotlight, ToolKind.Callout, ToolKind.Magnifier, ToolKind.Blur,
            ToolKind.Pixelate, ToolKind.Redact, ToolKind.Sticker, ToolKind.Measure,
        }.Select(t => new object[] { t });

    [Theory]
    [MemberData(nameof(DragTools))]
    public void Every_drag_tool_creates_one_annotation_and_undo_removes_it(ToolKind tool)
    {
        var s = New();
        var before = s.State;
        s.SetTool(tool);
        Drag(s, new Vec2(50, 50), new Vec2(150, 120));
        Assert.Single(s.State.Items);
        Assert.Null(s.Draft);
        Assert.True(s.CanUndo);

        s.Undo();
        Assert.Same(before, s.State);
        s.Redo();
        Assert.Single(s.State.Items);
    }

    [Theory]
    [MemberData(nameof(DragTools))]
    public void A_click_without_drag_creates_nothing(ToolKind tool)
    {
        var s = New();
        s.SetTool(tool);
        s.PointerDown(new Vec2(50, 50), PointerModifiers.None, 4);
        s.PointerUp(new Vec2(51, 50), PointerModifiers.None);
        Assert.Empty(s.State.Items);
        Assert.False(s.CanUndo);
    }

    [Fact]
    public void Draft_is_visible_during_drag_and_escape_discards_it()
    {
        var s = New();
        s.SetTool(ToolKind.Rectangle);
        s.PointerDown(new Vec2(10, 10), PointerModifiers.None, 4);
        s.PointerMove(new Vec2(90, 60), PointerModifiers.None);
        Assert.IsType<ShapeAnnotation>(s.Draft);
        s.CancelPointer();
        Assert.Null(s.Draft);
        Assert.Empty(s.State.Items);
        s.PointerUp(new Vec2(90, 60), PointerModifiers.None); // stray mouse-up after cancel must be harmless
        Assert.Empty(s.State.Items);
    }

    [Fact]
    public void Shift_constrains_rectangle_to_square_and_arrow_to_45_degrees()
    {
        var s = New();
        s.SetTool(ToolKind.Rectangle);
        Drag(s, new Vec2(10, 10), new Vec2(110, 40), PointerModifiers.Shift);
        Assert.Equal(s.State.Items[0].Bounds.Width, s.State.Items[0].Bounds.Height);

        s.SetTool(ToolKind.Arrow);
        Drag(s, new Vec2(10, 200), new Vec2(110, 204), PointerModifiers.Shift);
        var arrow = (ArrowAnnotation)s.State.Items[1];
        Assert.Equal(200, arrow.End.Y, 6);
    }

    [Fact]
    public void Steps_auto_increment()
    {
        var s = New();
        s.SetTool(ToolKind.Steps);
        s.PointerDown(new Vec2(10, 10), PointerModifiers.None, 4);
        s.PointerDown(new Vec2(50, 10), PointerModifiers.None, 4);
        s.PointerDown(new Vec2(90, 10), PointerModifiers.None, 4);
        Assert.Equal([1, 2, 3], s.State.Items.Cast<StepAnnotation>().Select(x => x.Number));
        s.Undo();
        s.PointerDown(new Vec2(90, 10), PointerModifiers.None, 4);
        Assert.Equal(3, ((StepAnnotation)s.State.Items[^1]).Number);
    }

    [Fact]
    public void Text_tool_requests_input_and_commit_adds_annotation()
    {
        var s = New();
        TextEditRequest? request = null;
        s.TextEditRequested += (_, r) => request = r;
        s.SetTool(ToolKind.Text);
        s.PointerDown(new Vec2(40, 40), PointerModifiers.None, 4);
        Assert.NotNull(request);
        s.CommitText(request!, "  hi\r\nthere  ");
        var text = Assert.IsType<TextAnnotation>(Assert.Single(s.State.Items));
        Assert.Equal("  hi\nthere", text.Text);
        Assert.Equal(new Vec2(40, 40), text.Position);
    }

    [Fact]
    public void Empty_text_adds_nothing_and_clearing_existing_text_removes_it()
    {
        var s = New();
        TextEditRequest? request = null;
        s.TextEditRequested += (_, r) => request = r;
        s.SetTool(ToolKind.Text);
        s.PointerDown(new Vec2(40, 40), PointerModifiers.None, 4);
        s.CommitText(request!, "   ");
        Assert.Empty(s.State.Items);

        s.CommitText(request!, "keep");
        s.SetTool(ToolKind.Select);
        Assert.True(s.PointerDoubleClick(s.State.Items[0].Bounds.Center, 4));
        s.CommitText(request!, string.Empty);
        Assert.Empty(s.State.Items);
    }

    [Fact]
    public void Callout_asks_for_text_after_drawing()
    {
        var s = New();
        TextEditRequest? request = null;
        s.TextEditRequested += (_, r) => request = r;
        s.SetTool(ToolKind.Callout);
        Drag(s, new Vec2(50, 50), new Vec2(200, 110));
        Assert.NotNull(request);
        s.CommitText(request!, "Click here");
        Assert.Equal("Click here", ((CalloutAnnotation)s.State.Items[0]).Text);
    }

    [Fact]
    public void Select_tool_moves_annotation_as_one_undo_step()
    {
        var s = New();
        s.SetTool(ToolKind.Redact);
        Drag(s, new Vec2(50, 50), new Vec2(150, 100));
        var created = s.State;
        s.SetTool(ToolKind.Select);

        s.PointerDown(new Vec2(100, 75), PointerModifiers.None, 4);
        for (int i = 1; i <= 20; i++) s.PointerMove(new Vec2(100 + i * 2, 75 + i), PointerModifiers.None);
        s.PointerUp(new Vec2(140, 95), PointerModifiers.None);

        Assert.Equal(new RectD(90, 70, 100, 50), s.State.Items[0].Bounds);
        s.Undo(); // the whole drag is one step
        Assert.Same(created, s.State);
    }

    [Fact]
    public void Clicking_selected_item_without_moving_adds_no_undo_step()
    {
        var s = New();
        s.SetTool(ToolKind.Redact);
        Drag(s, new Vec2(50, 50), new Vec2(150, 100));
        s.SetTool(ToolKind.Select);
        s.PointerDown(new Vec2(100, 75), PointerModifiers.None, 4);
        s.PointerUp(new Vec2(100, 75), PointerModifiers.None);
        Assert.Single(s.Selection);
        s.Undo();
        Assert.Empty(s.State.Items); // only the creation was on the stack
    }

    [Fact]
    public void Select_tool_resizes_via_handle()
    {
        var s = New();
        s.SetTool(ToolKind.Redact);
        Drag(s, new Vec2(50, 50), new Vec2(150, 100));
        s.SetTool(ToolKind.Select);
        s.PointerDown(new Vec2(100, 75), PointerModifiers.None, 4);
        s.PointerUp(new Vec2(100, 75), PointerModifiers.None);

        Drag(s, new Vec2(150, 100), new Vec2(250, 200));
        Assert.Equal(new RectD(50, 50, 200, 150), s.State.Items[0].Bounds);
    }

    [Fact]
    public void Pointer_down_on_empty_space_is_not_handled_and_clears_selection()
    {
        var s = New();
        s.SetTool(ToolKind.Redact);
        Drag(s, new Vec2(50, 50), new Vec2(150, 100));
        s.SetTool(ToolKind.Select);
        s.PointerDown(new Vec2(100, 75), PointerModifiers.None, 4);
        s.PointerUp(new Vec2(100, 75), PointerModifiers.None);
        Assert.False(s.PointerDown(new Vec2(300, 250), PointerModifiers.None, 4));
        Assert.Empty(s.Selection);
    }

    [Fact]
    public void Shift_click_toggles_multi_selection_and_delete_removes_all()
    {
        var s = New();
        s.SetTool(ToolKind.Redact);
        Drag(s, new Vec2(10, 10), new Vec2(60, 60));
        Drag(s, new Vec2(100, 10), new Vec2(160, 60));
        s.SetTool(ToolKind.Select);
        s.PointerDown(new Vec2(30, 30), PointerModifiers.None, 4);
        s.PointerUp(new Vec2(30, 30), PointerModifiers.None);
        s.PointerDown(new Vec2(130, 30), PointerModifiers.Shift, 4);
        s.PointerUp(new Vec2(130, 30), PointerModifiers.Shift);
        Assert.Equal(2, s.Selection.Count);
        s.DeleteSelected();
        Assert.Empty(s.State.Items);
        Assert.Empty(s.Selection);
        s.Undo();
        Assert.Equal(2, s.State.Items.Count);
    }

    [Fact]
    public void Duplicate_copy_paste_and_z_order()
    {
        var s = New();
        s.SetTool(ToolKind.Redact);
        Drag(s, new Vec2(10, 10), new Vec2(60, 60));
        Drag(s, new Vec2(100, 10), new Vec2(160, 60));
        var first = s.State.Items[0].Id;
        s.SetTool(ToolKind.Select);
        s.Select([first]);

        s.BringToFront();
        Assert.Equal(first, s.State.Items[^1].Id);
        s.SendToBack();
        Assert.Equal(first, s.State.Items[0].Id);

        s.DuplicateSelected();
        Assert.Equal(3, s.State.Items.Count);
        Assert.NotEqual(first, s.Selection.Single());

        s.CopySelected();
        s.PasteCopied();
        s.PasteCopied();
        Assert.Equal(5, s.State.Items.Count);
        Assert.Equal(5, s.State.Items.Select(i => i.Id).Distinct().Count());
    }

    [Fact]
    public void Nudge_moves_selection()
    {
        var s = New();
        s.SetTool(ToolKind.Redact);
        Drag(s, new Vec2(10, 10), new Vec2(60, 60));
        s.SetTool(ToolKind.Select);
        s.SelectAll();
        s.NudgeSelected(new Vec2(1, -1));
        Assert.Equal(new RectD(11, 9, 50, 50), s.State.Items[0].Bounds);
    }

    [Fact]
    public void Undo_redo_walks_every_state_exactly()
    {
        var s = New();
        var states = new List<DocState> { s.State };
        s.SetTool(ToolKind.Rectangle);
        for (int i = 0; i < 25; i++)
        {
            Drag(s, new Vec2(10 + i, 10), new Vec2(60 + i, 60));
            states.Add(s.State);
        }

        for (int i = states.Count - 2; i >= 0; i--)
        {
            s.Undo();
            Assert.Same(states[i], s.State);
        }
        Assert.False(s.CanUndo);
        s.Undo(); // extra undo is harmless
        for (int i = 1; i < states.Count; i++)
        {
            s.Redo();
            Assert.Same(states[i], s.State);
        }
        Assert.False(s.CanRedo);
    }

    [Fact]
    public void New_edit_clears_redo()
    {
        var s = New();
        s.SetTool(ToolKind.Rectangle);
        Drag(s, new Vec2(10, 10), new Vec2(60, 60));
        s.Undo();
        Assert.True(s.CanRedo);
        Drag(s, new Vec2(20, 20), new Vec2(70, 70));
        Assert.False(s.CanRedo);
    }

    [Fact]
    public void Clip_area_blocks_drawing_outside_and_clamps_inside()
    {
        var s = New();
        s.ClipArea = new RectD(100, 100, 100, 100);
        s.SetTool(ToolKind.Rectangle);
        Assert.False(s.PointerDown(new Vec2(10, 10), PointerModifiers.None, 4));
        Drag(s, new Vec2(110, 110), new Vec2(390, 290));
        Assert.Equal(new RectD(110, 110, 90, 90), s.State.Items[0].Bounds);
    }

    [Fact]
    public void Crop_tool_raises_request_and_apply_crop_is_undoable()
    {
        var s = New();
        RectD? requested = null;
        s.CropRequested += (_, r) => requested = r;
        s.SetTool(ToolKind.Crop);
        Drag(s, new Vec2(50, 50), new Vec2(250, 150));
        Assert.Equal(new RectD(50, 50, 200, 100), requested);
        Assert.Null(s.CropDraft);

        s.ApplyCrop(requested!.Value.ToPixelRectRounded());
        Assert.Equal(new PixelSize(200, 100), s.State.Size);
        s.Undo();
        Assert.Equal(new PixelSize(400, 300), s.State.Size);
    }

    [Fact]
    public void Changing_options_restyles_selection_only()
    {
        var s = New();
        s.SetTool(ToolKind.Rectangle);
        Drag(s, new Vec2(10, 10), new Vec2(60, 60));
        Drag(s, new Vec2(100, 10), new Vec2(160, 60));
        s.SetTool(ToolKind.Select);
        s.Select([s.State.Items[0].Id]);
        var blue = new ColorRgba(0, 0, 255);
        s.SetOptions(s.Options with { Color = blue, StrokeWidth = 9 });
        Assert.Equal(blue, ((ShapeAnnotation)s.State.Items[0]).Color);
        Assert.Equal(9, ((ShapeAnnotation)s.State.Items[0]).StrokeWidth);
        Assert.Equal(ColorRgba.Red, ((ShapeAnnotation)s.State.Items[1]).Color);
    }

    [Fact]
    public void Dirty_flag_tracks_saved_state()
    {
        var s = New();
        Assert.False(s.IsDirty);
        s.SetTool(ToolKind.Rectangle);
        Drag(s, new Vec2(10, 10), new Vec2(60, 60));
        Assert.True(s.IsDirty);
        s.MarkSaved();
        Assert.False(s.IsDirty);
        s.Undo();
        Assert.True(s.IsDirty);
    }

    [Fact]
    public void Undo_history_is_bounded()
    {
        var s = New();
        s.SetTool(ToolKind.Steps);
        for (int i = 0; i < 260; i++) s.PointerDown(new Vec2(i, 10), PointerModifiers.None, 4);
        int undone = 0;
        while (s.CanUndo) { s.Undo(); undone++; }
        Assert.Equal(200, undone);
        Assert.Equal(60, s.State.Items.Count);
    }

    [Fact]
    public void Magnifier_destination_stays_inside_document()
    {
        var s = New();
        s.SetTool(ToolKind.Magnifier);
        Drag(s, new Vec2(330, 230), new Vec2(390, 290));
        var m = (MagnifierAnnotation)s.State.Items[0];
        Assert.True(m.Dest.Left >= 0 && m.Dest.Top >= 0, $"dest {m.Dest}");
    }
}
