using System.Collections.Immutable;
using ShotIt.Core.Annotations;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;

namespace ShotIt.Core.Editing;

public enum ToolKind
{
    Select,
    Arrow,
    Line,
    Rectangle,
    Ellipse,
    Pen,
    Highlighter,
    AreaHighlight,
    Spotlight,
    Text,
    Callout,
    Steps,
    Magnifier,
    Blur,
    Pixelate,
    Redact,
    Sticker,
    Measure,
    Eyedropper,
    Crop,
    RoundedRectangle,
    Eraser,
}

[Flags]
public enum PointerModifiers
{
    None = 0,
    Shift = 1,
    Ctrl = 2,
    Alt = 4,
}

/// <summary>Current drawing settings. One instance is shared by all tools; the UI edits it.</summary>
public sealed record ToolOptions
{
    public ColorRgba Color { get; init; } = ColorRgba.Red;
    public double StrokeWidth { get; init; } = 4;
    public bool Filled { get; init; }
    public bool Dashed { get; init; }
    public bool DoubleHeaded { get; init; }
    public ColorRgba HighlightColor { get; init; } = ColorRgba.Yellow;
    public double HighlightWidth { get; init; } = 20;
    public string FontFamily { get; init; } = "Segoe UI";
    public double FontSize { get; init; } = 20;
    public bool Bold { get; init; } = true;
    public bool Italic { get; init; }
    public bool TextBackground { get; init; }
    public double CornerRadius { get; init; } = 12;
    public double BlurRadius { get; init; } = 12;
    public int PixelSize { get; init; } = 12;
    public string Sticker { get; init; } = "check";
    public double StepRadius { get; init; } = 16;
    public double SpotlightDim { get; init; } = 0.6;
}

/// <summary>Raised when the user should type text. The view shows an input box and calls CommitText.</summary>
public sealed record TextEditRequest(Guid? TargetId, RectD Area, string InitialText, string FontFamily, double FontSize, bool Bold, ColorRgba Color, bool Italic = false);

/// <summary>
/// UI-independent editor: document state, undo history, selection and the pointer state machine of every tool.
/// Views translate mouse input into document coordinates and call the Pointer methods.
/// </summary>
public sealed class EditorSession
{
    private const double MinDragDistance = 3;
    private const int MaxUndo = 200;

    private readonly Stack<DocState> _undo = new();
    private readonly Stack<DocState> _redo = new();
    private DocState? _gestureStart;
    private Drag? _drag;
    private ImmutableList<Annotation> _clipboard = [];

    public EditorSession(DocState initial)
    {
        State = initial ?? throw new ArgumentNullException(nameof(initial));
        SavedState = initial;
    }

    public DocState State { get; private set; }

    /// <summary>The state at the last save; lets the UI show an unsaved-changes marker.</summary>
    public DocState SavedState { get; private set; }

    public bool IsDirty => !ReferenceEquals(State, SavedState);

    /// <summary>The annotation being drawn right now. Rendered on top but not yet part of the document.</summary>
    public Annotation? Draft { get; private set; }

    /// <summary>The rectangle being dragged with the crop tool, if any.</summary>
    public RectD? CropDraft { get; private set; }

    public ImmutableHashSet<Guid> Selection { get; private set; } = [];

    public ToolKind Tool { get; private set; } = ToolKind.Select;

    public ToolOptions Options { get; private set; } = new();

    /// <summary>Width divided by height that the crop rectangle is held to, or null for a free crop.</summary>
    public double? CropAspect { get; set; }

    /// <summary>Restricts drawing to this area (the capture region while editing on the overlay).</summary>
    public RectD? ClipArea { get; set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsGestureActive => _drag is not null;

    public event EventHandler? Changed;
    public event EventHandler? ToolChanged;
    public event EventHandler<TextEditRequest>? TextEditRequested;
    public event EventHandler<Vec2>? ColorPickRequested;
    public event EventHandler<RectD>? CropRequested;

    // ---------------------------------------------------------------- state & history

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    /// <summary>Replaces the document state and records one undo step.</summary>
    public void Apply(DocState next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (ReferenceEquals(next, State) || next == State) return;
        if (_gestureStart is null)
        {
            PushUndo(State);
            _redo.Clear();
        }
        State = next;
        PruneSelection();
        RaiseChanged();
    }

    private void PushUndo(DocState s)
    {
        _undo.Push(s);
        if (_undo.Count > MaxUndo)
        {
            // Drop the oldest entries; a Stack enumerates newest first.
            var keep = _undo.Take(MaxUndo).Reverse().ToArray();
            _undo.Clear();
            foreach (var k in keep) _undo.Push(k);
        }
    }

    /// <summary>Starts a multi-step change (a drag). Everything until EndGesture becomes a single undo step.</summary>
    public void BeginGesture() => _gestureStart ??= State;

    public void EndGesture()
    {
        if (_gestureStart is null) return;
        var start = _gestureStart;
        _gestureStart = null;
        if (!ReferenceEquals(start, State) && start != State)
        {
            PushUndo(start);
            _redo.Clear();
        }
        RaiseChanged();
    }

    public void CancelGesture()
    {
        if (_gestureStart is null) return;
        State = _gestureStart;
        _gestureStart = null;
        RaiseChanged();
    }

    public void Undo()
    {
        CancelPointer();
        if (_undo.Count == 0) return;
        _redo.Push(State);
        State = _undo.Pop();
        PruneSelection();
        RaiseChanged();
    }

    public void Redo()
    {
        CancelPointer();
        if (_redo.Count == 0) return;
        _undo.Push(State);
        State = _redo.Pop();
        PruneSelection();
        RaiseChanged();
    }

    public void MarkSaved()
    {
        SavedState = State;
        RaiseChanged();
    }

    private void PruneSelection()
    {
        if (Selection.IsEmpty) return;
        var alive = State.Items.Select(a => a.Id).ToHashSet();
        Selection = Selection.Where(alive.Contains).ToImmutableHashSet();
    }

    // ---------------------------------------------------------------- tools & options

    public void SetTool(ToolKind tool)
    {
        CancelPointer();
        if (Tool == tool) return;
        Tool = tool;
        if (tool != ToolKind.Select) Selection = [];
        ToolChanged?.Invoke(this, EventArgs.Empty);
        RaiseChanged();
    }

    /// <summary>Changes drawing options and applies the relevant ones to the selected annotations.</summary>
    public void SetOptions(ToolOptions options, bool applyToSelection = true)
    {
        ArgumentNullException.ThrowIfNull(options);
        var previous = Options;
        Options = options;
        if (applyToSelection && !Selection.IsEmpty)
        {
            var next = State;
            foreach (var a in State.Items.Where(a => Selection.Contains(a.Id)))
                next = next.Replace(Restyle(a, previous, options));
            Apply(next);
        }
        ToolChanged?.Invoke(this, EventArgs.Empty);
    }

    private static Annotation Restyle(Annotation a, ToolOptions old, ToolOptions o)
    {
        bool color = old.Color != o.Color, width = old.StrokeWidth != o.StrokeWidth;
        bool fill = old.Filled != o.Filled, dash = old.Dashed != o.Dashed, font = old.FontSize != o.FontSize;
        bool hl = old.HighlightColor != o.HighlightColor, hlw = old.HighlightWidth != o.HighlightWidth;
        return a switch
        {
            ArrowAnnotation x => x with
            {
                Color = color ? o.Color : x.Color,
                StrokeWidth = width ? o.StrokeWidth : x.StrokeWidth,
                Dashed = dash ? o.Dashed : x.Dashed,
                DoubleHeaded = old.DoubleHeaded != o.DoubleHeaded ? o.DoubleHeaded : x.DoubleHeaded,
            },
            LineAnnotation x => x with
            {
                Color = color ? o.Color : x.Color,
                StrokeWidth = width ? o.StrokeWidth : x.StrokeWidth,
                Dashed = dash ? o.Dashed : x.Dashed,
            },
            FreehandAnnotation x => x with { Color = color ? o.Color : x.Color, StrokeWidth = width ? o.StrokeWidth : x.StrokeWidth },
            HighlighterAnnotation x => x with { Color = hl ? o.HighlightColor : x.Color, StrokeWidth = hlw ? o.HighlightWidth : x.StrokeWidth },
            AreaHighlightAnnotation x => x with { Color = hl ? o.HighlightColor : x.Color },
            ShapeAnnotation x => x with
            {
                Color = color ? o.Color : x.Color,
                StrokeWidth = width ? o.StrokeWidth : x.StrokeWidth,
                Dashed = dash ? o.Dashed : x.Dashed,
                Fill = fill || (color && x.Fill is not null) ? (o.Filled ? o.Color.WithAlpha(x.Fill?.A ?? 70) : null) : x.Fill,
                CornerRadius = x.Kind == ShapeKind.RoundedRectangle && old.CornerRadius != o.CornerRadius ? o.CornerRadius : x.CornerRadius,
            },
            TextAnnotation x => x with
            {
                Color = color ? o.Color : x.Color,
                FontSize = font ? o.FontSize : x.FontSize,
                Bold = old.Bold != o.Bold ? o.Bold : x.Bold,
                Italic = old.Italic != o.Italic ? o.Italic : x.Italic,
                FontFamily = old.FontFamily != o.FontFamily ? o.FontFamily : x.FontFamily,
                Background = old.TextBackground != o.TextBackground || color ? TextBackgroundFor(o) : x.Background,
            },
            CalloutAnnotation x => x with
            {
                Fill = color ? o.Color : x.Fill,
                TextColor = color ? ReadableOn(o.Color) : x.TextColor,
                FontSize = font ? o.FontSize : x.FontSize,
            },
            StepAnnotation x => x with { Color = color ? o.Color : x.Color, TextColor = color ? ReadableOn(o.Color) : x.TextColor },
            MagnifierAnnotation x => x with { Border = color ? o.Color : x.Border },
            BlurAnnotation x => x with { Radius = old.BlurRadius != o.BlurRadius ? o.BlurRadius : x.Radius },
            PixelateAnnotation x => x with { BlockSize = old.PixelSize != o.PixelSize ? o.PixelSize : x.BlockSize },
            RedactAnnotation x => x with { Color = color ? o.Color : x.Color },
            SpotlightAnnotation x => x with { DimOpacity = old.SpotlightDim != o.SpotlightDim ? o.SpotlightDim : x.DimOpacity },
            MeasureAnnotation x => x with { Color = color ? o.Color : x.Color },
            _ => a,
        };
    }

    public static ColorRgba ReadableOn(ColorRgba background) =>
        background.Luminance > 0.6 ? ColorRgba.Black : ColorRgba.White;

    private static ColorRgba? TextBackgroundFor(ToolOptions o) =>
        o.TextBackground ? ReadableOn(o.Color).WithAlpha(215) : null;

    // ---------------------------------------------------------------- selection commands

    public IEnumerable<Annotation> SelectedItems => State.Items.Where(a => Selection.Contains(a.Id));

    public void Select(IEnumerable<Guid> ids)
    {
        Selection = [.. ids];
        RaiseChanged();
    }

    public void SelectAll()
    {
        Selection = [.. State.Items.Select(a => a.Id)];
        RaiseChanged();
    }

    public void ClearSelection()
    {
        if (Selection.IsEmpty) return;
        Selection = [];
        RaiseChanged();
    }

    public void DeleteSelected()
    {
        if (Selection.IsEmpty) return;
        Apply(State.Remove(Selection));
    }

    public void NudgeSelected(Vec2 delta)
    {
        if (Selection.IsEmpty) return;
        Apply(State with { Items = [.. State.Items.Select(a => Selection.Contains(a.Id) ? a.MoveBy(delta) : a)] });
    }

    public void DuplicateSelected()
    {
        var copies = SelectedItems.Select(a => a.MoveBy(new Vec2(16, 16)).WithNewId()).ToList();
        if (copies.Count == 0) return;
        Apply(State with { Items = State.Items.AddRange(copies) });
        Select(copies.Select(c => c.Id));
    }

    public void CopySelected() => _clipboard = [.. SelectedItems];

    public bool HasCopiedItems => !_clipboard.IsEmpty;

    public void PasteCopied()
    {
        if (_clipboard.IsEmpty) return;
        var copies = _clipboard.Select(a => a.MoveBy(new Vec2(16, 16)).WithNewId()).ToList();
        _clipboard = [.. copies];
        Apply(State with { Items = State.Items.AddRange(copies) });
        Select(copies.Select(c => c.Id));
    }

    public void BringToFront() => Reorder(front: true);

    public void SendToBack() => Reorder(front: false);

    /// <summary>Moves the selected annotations one step up in the stacking order.</summary>
    public void BringForward() => Step(up: true);

    /// <summary>Moves the selected annotations one step down in the stacking order.</summary>
    public void SendBackward() => Step(up: false);

    private void Step(bool up)
    {
        if (Selection.IsEmpty) return;
        var items = State.Items.ToList();
        if (up)
        {
            for (int i = items.Count - 2; i >= 0; i--)
                if (Selection.Contains(items[i].Id) && !Selection.Contains(items[i + 1].Id)) (items[i], items[i + 1]) = (items[i + 1], items[i]);
        }
        else
        {
            for (int i = 1; i < items.Count; i++)
                if (Selection.Contains(items[i].Id) && !Selection.Contains(items[i - 1].Id)) (items[i], items[i - 1]) = (items[i - 1], items[i]);
        }

        Apply(State with { Items = [.. items] });
    }

    /// <summary>Lines up the selected annotations. <paramref name="edge"/>: 0 left, 1 horizontal centre, 2 right, 3 top, 4 vertical middle, 5 bottom.</summary>
    public void AlignSelected(int edge)
    {
        var picked = SelectedItems.ToList();
        if (picked.Count < 2) return;
        var all = RectD.FromPoints(picked.SelectMany(a => new[] { a.Bounds.TopLeft, new Vec2(a.Bounds.Right, a.Bounds.Bottom) }).ToList());
        Apply(State with
        {
            Items = [.. State.Items.Select(a =>
            {
                if (!Selection.Contains(a.Id)) return a;
                var b = a.Bounds;
                var delta = edge switch
                {
                    0 => new Vec2(all.Left - b.Left, 0),
                    1 => new Vec2(all.Center.X - b.Center.X, 0),
                    2 => new Vec2(all.Right - b.Right, 0),
                    3 => new Vec2(0, all.Top - b.Top),
                    4 => new Vec2(0, all.Center.Y - b.Center.Y),
                    _ => new Vec2(0, all.Bottom - b.Bottom),
                };
                return a.MoveBy(delta);
            })],
        });
    }

    private void Reorder(bool front)
    {
        if (Selection.IsEmpty) return;
        var picked = State.Items.Where(a => Selection.Contains(a.Id)).ToList();
        var rest = State.Items.Where(a => !Selection.Contains(a.Id)).ToList();
        Apply(State with { Items = front ? [.. rest, .. picked] : [.. picked, .. rest] });
    }

    /// <summary>Adds an annotation programmatically (paste image, sticker picker) and selects it.</summary>
    public void AddAnnotation(Annotation annotation)
    {
        Apply(State.Add(annotation));
        Tool = ToolKind.Select;
        Selection = [annotation.Id];
        ToolChanged?.Invoke(this, EventArgs.Empty);
        RaiseChanged();
    }

    public void ApplyCrop(PixelRect rect)
    {
        CancelPointer();
        Selection = [];
        Apply(State.Crop(rect));
    }

    /// <summary>Replaces the base image (rotate, flip, flatten, adjustments) and optionally the annotations.</summary>
    public void ReplaceBase(PixelBuffer image, ImmutableList<Annotation>? items = null)
    {
        CancelPointer();
        Selection = [];
        Apply(State with { Base = image, Items = items ?? State.Items });
    }

    // ---------------------------------------------------------------- hit testing

    /// <summary>Topmost annotation under the point, or null.</summary>
    public Annotation? HitTest(Vec2 p, double tolerance)
    {
        for (int i = State.Items.Count - 1; i >= 0; i--)
            if (State.Items[i].HitTest(p, tolerance)) return State.Items[i];
        return null;
    }

    /// <summary>The handle of the single selected annotation under the point, if any.</summary>
    public (Annotation Item, Handle Handle)? HitTestHandle(Vec2 p, double handleRadius)
    {
        if (Selection.Count != 1) return null;
        var item = State.Find(Selection.First());
        if (item is null) return null;
        foreach (var h in item.GetHandles())
            if (h.Position.DistanceTo(p) <= handleRadius) return (item, h);
        return null;
    }

    // ---------------------------------------------------------------- pointer state machine

    private sealed class Drag
    {
        public required Vec2 Origin { get; init; }
        public DragKind Kind { get; init; }
        public Guid TargetId { get; init; }
        public int HandleId { get; init; }
        public DocState? StartState { get; init; }
        public List<Vec2> Path { get; } = [];
        public bool Moved { get; set; }
        public double Tolerance { get; init; }
    }

    private enum DragKind
    {
        Create,
        MoveSelection,
        MoveHandle,
        Crop,
        Erase,
    }

    /// <summary>
    /// Handles a pointer press. Returns false when the editor did not use the press, so the host
    /// (for example the capture overlay) can use it for something else such as moving the capture region.
    /// </summary>
    public bool PointerDown(Vec2 p, PointerModifiers mods, double tolerance)
    {
        CancelPointer();

        if (Tool == ToolKind.Select)
        {
            if (HitTestHandle(p, Math.Max(tolerance, 6)) is { } hit)
            {
                BeginGesture();
                _drag = new Drag { Origin = p, Kind = DragKind.MoveHandle, TargetId = hit.Item.Id, HandleId = hit.Handle.Id, StartState = State };
                return true;
            }

            var target = HitTest(p, tolerance);
            if (target is null)
            {
                if (!Selection.IsEmpty) ClearSelection();
                return false;
            }

            if (mods.HasFlag(PointerModifiers.Shift))
                Selection = Selection.Contains(target.Id) ? Selection.Remove(target.Id) : Selection.Add(target.Id);
            else if (!Selection.Contains(target.Id))
                Selection = [target.Id];

            BeginGesture();
            _drag = new Drag { Origin = p, Kind = DragKind.MoveSelection, StartState = State };
            RaiseChanged();
            return true;
        }

        if (ClipArea is { } clip && !clip.Contains(p)) return false;

        switch (Tool)
        {
            case ToolKind.Steps:
                Apply(State.Add(new StepAnnotation
                {
                    Center = p,
                    Number = State.NextStepNumber(),
                    Radius = Options.StepRadius,
                    Color = Options.Color,
                    TextColor = ReadableOn(Options.Color),
                }));
                return true;

            case ToolKind.Text:
                TextEditRequested?.Invoke(this, new TextEditRequest(
                    null, new RectD(p.X, p.Y, 0, 0), string.Empty, Options.FontFamily, Options.FontSize, Options.Bold, Options.Color, Options.Italic));
                return true;

            case ToolKind.Eraser:
                BeginGesture();
                _drag = new Drag { Origin = p, Kind = DragKind.Erase, Tolerance = Math.Max(tolerance, 6) };
                EraseAt(p, _drag.Tolerance);
                return true;

            case ToolKind.Eyedropper:
                ColorPickRequested?.Invoke(this, p);
                return true;

            case ToolKind.Crop:
                _drag = new Drag { Origin = p, Kind = DragKind.Crop };
                return true;

            default:
                _drag = new Drag { Origin = p, Kind = DragKind.Create };
                _drag.Path.Add(p);
                return true;
        }
    }

    public void PointerMove(Vec2 p, PointerModifiers mods)
    {
        if (_drag is not { } drag) return;
        if (drag.Kind == DragKind.Erase)
        {
            // A fast drag jumps many pixels between events; walk the gap so no stroke is skipped.
            var from = drag.Path.Count > 0 ? drag.Path[^1] : drag.Origin;
            int steps = Math.Max(1, (int)Math.Ceiling(from.DistanceTo(p) / Math.Max(2, drag.Tolerance)));
            for (int i = 1; i <= steps; i++) EraseAt(Vec2.Lerp(from, p, (double)i / steps), drag.Tolerance);
            drag.Path.Clear();
            drag.Path.Add(p);
            return;
        }

        if (!drag.Moved && p.DistanceTo(drag.Origin) < MinDragDistance) return;
        drag.Moved = true;
        bool shift = mods.HasFlag(PointerModifiers.Shift);

        switch (drag.Kind)
        {
            case DragKind.MoveSelection:
            {
                var delta = p - drag.Origin;
                if (shift) delta = Math.Abs(delta.X) > Math.Abs(delta.Y) ? new Vec2(delta.X, 0) : new Vec2(0, delta.Y);
                var start = drag.StartState!;
                State = start with { Items = [.. start.Items.Select(a => Selection.Contains(a.Id) ? a.MoveBy(delta) : a)] };
                RaiseChanged();
                break;
            }

            case DragKind.MoveHandle:
            {
                var original = drag.StartState!.Find(drag.TargetId);
                if (original is null) break;
                State = drag.StartState.Replace(original.MoveHandle(drag.HandleId, p, shift));
                RaiseChanged();
                break;
            }

            case DragKind.Crop:
                CropDraft = RectD.FromCorners(drag.Origin, CropAspect is { } aspect and > 0 ? ConstrainAspect(drag.Origin, p, aspect) : shift ? GeometryMath.ConstrainSquare(drag.Origin, p) : p);
                RaiseChanged();
                break;

            case DragKind.Create:
                if (ClipArea is { } clip) p = new Vec2(Math.Clamp(p.X, clip.Left, clip.Right), Math.Clamp(p.Y, clip.Top, clip.Bottom));
                if (drag.Path.Count == 0 || drag.Path[^1].DistanceTo(p) >= 1.5) drag.Path.Add(p);
                Draft = BuildDraft(drag, p, mods);
                RaiseChanged();
                break;
        }
    }

    public void PointerUp(Vec2 p, PointerModifiers mods)
    {
        if (_drag is not { } drag) return;
        PointerMove(p, mods);
        _drag = null;

        switch (drag.Kind)
        {
            case DragKind.MoveSelection:
            case DragKind.MoveHandle:
            case DragKind.Erase:
                EndGesture();
                break;

            case DragKind.Crop:
            {
                var rect = CropDraft;
                CropDraft = null;
                RaiseChanged();
                if (rect is { Width: >= 4, Height: >= 4 } r) CropRequested?.Invoke(this, r);
                break;
            }

            case DragKind.Create:
            {
                var draft = Draft;
                Draft = null;
                if (draft is null || !drag.Moved)
                {
                    RaiseChanged();
                    break;
                }

                Apply(State.Add(draft));
                if (draft is CalloutAnnotation callout)
                {
                    TextEditRequested?.Invoke(this, new TextEditRequest(
                        callout.Id, callout.Box, callout.Text, callout.FontFamily, callout.FontSize, false, callout.TextColor));
                }
                RaiseChanged();
                break;
            }
        }
    }

    /// <summary>The eraser removes whole pen and highlighter strokes it touches; other annotations are left alone.</summary>
    private void EraseAt(Vec2 p, double tolerance)
    {
        var hit = State.Items.Where(a => a is FreehandAnnotation or HighlighterAnnotation && a.HitTest(p, tolerance)).Select(a => a.Id).ToList();
        if (hit.Count == 0) return;
        State = State.Remove(hit);
        PruneSelection();
        RaiseChanged();
    }

    private static Vec2 ConstrainAspect(Vec2 origin, Vec2 p, double aspect)
    {
        double dx = p.X - origin.X, dy = p.Y - origin.Y;
        // Follow whichever direction the pointer has travelled further, relative to the wanted shape.
        double w = Math.Max(Math.Abs(dx), Math.Abs(dy) * aspect);
        double h = w / aspect;
        return new Vec2(origin.X + (dx < 0 ? -w : w), origin.Y + (dy < 0 ? -h : h));
    }

    /// <summary>Double-click: edit the text of a text or callout annotation, or add a corner to a line.</summary>
    public bool PointerDoubleClick(Vec2 p, double tolerance)
    {
        switch (HitTest(p, tolerance))
        {
            case LineAnnotation line when line.Points.Length >= 2:
            {
                // Insert the new corner into the segment that was clicked, turning the line into a polyline.
                int best = 0;
                double bestDistance = double.MaxValue;
                for (int i = 0; i + 1 < line.Points.Length; i++)
                {
                    double d = GeometryMath.DistanceToSegment(p, line.Points[i], line.Points[i + 1]);
                    if (d < bestDistance) (best, bestDistance) = (i, d);
                }

                Apply(State.Replace(line with { Points = line.Points.Insert(best + 1, p) }));
                Selection = [line.Id];
                RaiseChanged();
                return true;
            }

            case TextAnnotation t:
                TextEditRequested?.Invoke(this, new TextEditRequest(t.Id, t.Bounds, t.Text, t.FontFamily, t.FontSize, t.Bold, t.Color, t.Italic));
                return true;
            case CalloutAnnotation c:
                TextEditRequested?.Invoke(this, new TextEditRequest(c.Id, c.Box, c.Text, c.FontFamily, c.FontSize, false, c.TextColor));
                return true;
            default:
                return false;
        }
    }

    /// <summary>Aborts whatever drag is in progress (Esc, tool switch, lost capture).</summary>
    public void CancelPointer()
    {
        if (_drag is null && Draft is null && CropDraft is null) return;
        _drag = null;
        Draft = null;
        CropDraft = null;
        CancelGesture();
        RaiseChanged();
    }

    /// <summary>Stores the text typed for a <see cref="TextEditRequest"/>. Empty text removes the annotation.</summary>
    public void CommitText(TextEditRequest request, string text)
    {
        ArgumentNullException.ThrowIfNull(request);
        text = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

        if (request.TargetId is { } id && State.Find(id) is { } existing)
        {
            switch (existing)
            {
                case TextAnnotation t:
                    Apply(text.Length == 0 ? State.Remove([id]) : State.Replace(t with { Text = text }));
                    break;
                case CalloutAnnotation c:
                    Apply(State.Replace(c with { Text = text }));
                    break;
            }
            return;
        }

        if (text.Length == 0) return;
        Apply(State.Add(new TextAnnotation
        {
            Position = request.Area.TopLeft,
            Text = text,
            FontFamily = request.FontFamily,
            FontSize = request.FontSize,
            Bold = request.Bold,
            Italic = request.Italic,
            Color = request.Color,
            Background = TextBackgroundFor(Options),
        }));
    }

    private Annotation? BuildDraft(Drag drag, Vec2 p, PointerModifiers mods)
    {
        bool shift = mods.HasFlag(PointerModifiers.Shift);
        var o = Options;
        var a = drag.Origin;
        var rect = RectD.FromCorners(a, shift ? GeometryMath.ConstrainSquare(a, p) : p);
        var end = shift ? GeometryMath.SnapAngle45(a, p) : p;
        var id = Draft?.Id ?? Guid.NewGuid();

        return Tool switch
        {
            ToolKind.Arrow => new ArrowAnnotation { Id = id, Start = a, End = end, Color = o.Color, StrokeWidth = o.StrokeWidth, Dashed = o.Dashed, DoubleHeaded = o.DoubleHeaded },
            ToolKind.Line => new LineAnnotation { Id = id, Points = [a, end], Color = o.Color, StrokeWidth = o.StrokeWidth, Dashed = o.Dashed },
            ToolKind.Measure => new MeasureAnnotation { Id = id, Start = a, End = end },
            ToolKind.Rectangle => new ShapeAnnotation { Id = id, Rect = rect, Kind = ShapeKind.Rectangle, Color = o.Color, StrokeWidth = o.StrokeWidth, Dashed = o.Dashed, Fill = o.Filled ? o.Color.WithAlpha(70) : null },
            ToolKind.RoundedRectangle => new ShapeAnnotation { Id = id, Rect = rect, Kind = ShapeKind.RoundedRectangle, CornerRadius = o.CornerRadius, Color = o.Color, StrokeWidth = o.StrokeWidth, Dashed = o.Dashed, Fill = o.Filled ? o.Color.WithAlpha(70) : null },
            ToolKind.Ellipse => new ShapeAnnotation { Id = id, Rect = rect, Kind = ShapeKind.Ellipse, Color = o.Color, StrokeWidth = o.StrokeWidth, Dashed = o.Dashed, Fill = o.Filled ? o.Color.WithAlpha(70) : null },
            ToolKind.Pen => new FreehandAnnotation { Id = id, Points = shift ? [a, p] : [.. drag.Path], Color = o.Color, StrokeWidth = o.StrokeWidth },
            ToolKind.Highlighter => new HighlighterAnnotation { Id = id, Points = shift ? [a, new Vec2(p.X, a.Y)] : [.. drag.Path], Color = o.HighlightColor, StrokeWidth = o.HighlightWidth },
            ToolKind.AreaHighlight => new AreaHighlightAnnotation { Id = id, Rect = rect, Color = o.HighlightColor, Ellipse = mods.HasFlag(PointerModifiers.Alt) },
            ToolKind.Spotlight => new SpotlightAnnotation { Id = id, Rect = rect, DimOpacity = o.SpotlightDim, Ellipse = mods.HasFlag(PointerModifiers.Alt) },
            ToolKind.Blur => new BlurAnnotation { Id = id, Rect = rect, Radius = o.BlurRadius },
            ToolKind.Pixelate => new PixelateAnnotation { Id = id, Rect = rect, BlockSize = o.PixelSize },
            ToolKind.Redact => new RedactAnnotation { Id = id, Rect = rect, Color = ColorRgba.Black },
            ToolKind.Sticker => new StickerAnnotation { Id = id, Rect = RectD.FromCorners(a, GeometryMath.ConstrainSquare(a, p)), Glyph = o.Sticker },
            ToolKind.Callout => new CalloutAnnotation
            {
                Id = id,
                Box = rect,
                Tail = new Vec2(rect.Left + rect.Width * 0.25, rect.Bottom + Math.Max(24, rect.Height * 0.6)),
                Fill = o.Color,
                TextColor = ReadableOn(o.Color),
                Stroke = ReadableOn(o.Color),
                FontFamily = o.FontFamily,
                FontSize = o.FontSize,
            },
            ToolKind.Magnifier => new MagnifierAnnotation
            {
                Id = id,
                Source = rect,
                Dest = PlaceMagnifier(rect),
                Border = o.Color,
            },
            _ => null,
        };
    }

    /// <summary>Puts the zoomed view at twice the size beside the source, wherever there is room.</summary>
    private RectD PlaceMagnifier(RectD source)
    {
        double w = source.Width * 2, h = source.Height * 2, gap = 24;
        var area = ClipArea ?? new RectD(0, 0, State.Size.Width, State.Size.Height);
        double x = source.Right + gap, y = source.Top;
        if (x + w > area.Right) x = source.Left - gap - w;
        if (x < area.Left) { x = source.Left; y = source.Bottom + gap; }
        if (y + h > area.Bottom) y = Math.Max(area.Top, source.Top - gap - h);
        return new RectD(x, y, w, h);
    }
}
