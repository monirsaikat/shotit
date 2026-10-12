using System.Windows.Input;
using System.Windows.Media.Imaging;
using ShotIt.Capture;
using ShotIt.Core.Display;
using ShotIt.Core.Editing;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;
using ShotIt.Core.Selection;
using ShotIt.Core.Settings;
using ShotIt.Native;
using ShotIt.Rendering;

namespace ShotIt.App.Overlay;

public enum OverlayMode
{
    Region,
    Window,
    Freeform,
    FixedSize,
    Preselected,
}

public enum OverlayPhase
{
    Selecting,
    Adjusting,
}

public enum OverlayAction
{
    Copy,
    Save,
    SaveAs,
    Upload,
    Pin,
    Ocr,
    Edit,
    Qr,
    SearchImage,
    Share,
    Print,
    Cancel,
}

/// <summary>What the user selected, ready to be copied, saved, pinned or edited further.</summary>
public sealed record OverlayResult(DocState Document, PixelRect ScreenRegion, string SourceApp, string SourceTitle);

/// <summary>
/// The logic of one capture session, shared by the overlay windows of all monitors. Windows forward
/// input here in virtual-screen pixels and repaint when <see cref="Changed"/> fires.
/// </summary>
public sealed class OverlaySession
{
    private enum DragKind
    {
        None,
        NewSelection,
        AdjustSelection,
        Editor,
        Lasso,
    }

    private readonly SnapIndex _snap;
    private readonly AppSettings _settings;
    private readonly AfterCaptureAction? _afterRegionOverride;
    private readonly List<PixelPoint> _lasso = [];
    private DragKind _drag;
    private PixelPoint _lastDragPx;
    private IReadOnlyList<SnapTarget>? _chain;
    private PixelPoint _chainPoint;
    private int _depth;
    private CancellationTokenSource? _chainCts;
    private bool _ended;

    public OverlaySession(ScreenFrame frame, SnapIndex snap, AppSettings settings, OverlayMode mode, PixelRect? preselect = null,
        AfterCaptureAction? afterRegionOverride = null)
    {
        Frame = frame ?? throw new ArgumentNullException(nameof(frame));
        _snap = snap ?? throw new ArgumentNullException(nameof(snap));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _afterRegionOverride = afterRegionOverride;
        Mode = mode;
        Bitmap = frame.Image.ToCachedBitmap();
        Selection = new SelectionModel(frame.VirtualBounds);
        Editor = new EditorSession(DocState.FromImage(frame.Image)) { ClipArea = default(RectD) };
        Editor.SetOptions(settings.Editor.Tool, applyToSelection: false);
        SnapEnabled = mode == OverlayMode.Window || (mode == OverlayMode.Region && settings.Capture.SnapToWindows);
        ShowMagnifier = settings.Capture.ShowMagnifier;
        CursorPx = Monitors.GetCursorPosition();

        Selection.Changed += (_, _) => OnSelectionChanged();
        Editor.Changed += (_, _) => RaiseChanged();

        if (preselect is { } region)
        {
            Selection.SetRegion(region);
            if (Selection.HasSelection) Phase = OverlayPhase.Adjusting;
        }
    }

    public ScreenFrame Frame { get; }
    public BitmapSource Bitmap { get; }
    public MonitorTopology Topology => Frame.Topology;
    public OverlayMode Mode { get; }
    public SelectionModel Selection { get; }
    public EditorSession Editor { get; }
    public OverlayPhase Phase { get; private set; } = OverlayPhase.Selecting;
    public bool SnapEnabled { get; private set; }
    public bool ShowMagnifier { get; private set; }

    /// <summary>The window or control highlighted under the cursor while choosing a region.</summary>
    public SnapTarget? HoverTarget { get; private set; }

    public PixelPoint CursorPx { get; private set; }

    /// <summary>Points of the freeform outline in virtual-screen pixels.</summary>
    public IReadOnlyList<PixelPoint> Lasso => _lasso;

    public bool LassoClosed { get; private set; }

    public bool IsDragging => _drag != DragKind.None;

    /// <summary>True while the user drags out or reshapes the region; the toolbar hides so it is not in the way.</summary>
    public bool IsShapingRegion => _drag is DragKind.NewSelection or DragKind.AdjustSelection or DragKind.Lasso;

    public bool IsTextEditing { get; set; }

    public event Action? Changed;
    /// <summary>A new region is ready to copy while the annotation toolbar stays open.</summary>
    public event Action? SelectionCompleted;
    public event Action<OverlayAction>? ActionRequested;

    private void RaiseChanged()
    {
        if (!_ended) Changed?.Invoke();
    }

    private PixelPoint Origin => Frame.VirtualBounds.Location;

    public Vec2 ToDoc(PixelPoint px) => new(px.X - Origin.X, px.Y - Origin.Y);

    private double ScaleAt(PixelPoint px) => Topology.FromPoint(px).Scale;

    private static PointerModifiers Mods()
    {
        var keys = Keyboard.Modifiers;
        var mods = PointerModifiers.None;
        if (keys.HasFlag(ModifierKeys.Shift)) mods |= PointerModifiers.Shift;
        if (keys.HasFlag(ModifierKeys.Control)) mods |= PointerModifiers.Ctrl;
        if (keys.HasFlag(ModifierKeys.Alt)) mods |= PointerModifiers.Alt;
        return mods;
    }

    private void OnSelectionChanged()
    {
        Editor.ClipArea = Selection.Current is { } r
            ? new RectD(r.X - Origin.X, r.Y - Origin.Y, r.Width, r.Height)
            : default(RectD);
        RaiseChanged();
    }

    /// <summary>Marks the session finished; late callbacks and events are ignored from now on.</summary>
    public void End()
    {
        _ended = true;
        _chainCts?.Cancel();
        _chainCts?.Dispose();
        _chainCts = null;
    }

    public void RequestAction(OverlayAction action)
    {
        if (_ended) return;
        if (action != OverlayAction.Cancel && !Selection.HasSelection) return;
        ActionRequested?.Invoke(action);
    }

    // ------------------------------------------------------------------ mouse

    public void MouseDown(PixelPoint px, MouseButton button, int clickCount)
    {
        if (_ended) return;
        CursorPx = px;

        if (button == MouseButton.Right)
        {
            if (_drag != DragKind.None) CancelDrag();
            else if (Phase == OverlayPhase.Adjusting && Selection.Current is { } current && current.Contains(px)) ResetSelection();
            else RequestAction(OverlayAction.Cancel);
            return;
        }

        if (button != MouseButton.Left || _drag != DragKind.None) return;
        _lastDragPx = px;

        if (Phase == OverlayPhase.Selecting)
        {
            if (Mode == OverlayMode.Freeform)
            {
                _lasso.Clear();
                _lasso.Add(px);
                LassoClosed = false;
                _drag = DragKind.Lasso;
            }
            else
            {
                Selection.BeginDrag(px);
                _drag = DragKind.NewSelection;
            }

            RaiseChanged();
            return;
        }

        double scale = ScaleAt(px);
        var hit = LassoClosed ? SelectionHit.None : Selection.HitTest(px, (int)Math.Round(6 * scale));
        if (hit is not SelectionHit.None and not SelectionHit.Inside)
        {
            Selection.BeginAdjust(hit, px);
            _drag = DragKind.AdjustSelection;
            RaiseChanged();
            return;
        }

        bool inside = Selection.Current is { } r && r.Contains(px);
        if (inside)
        {
            var doc = ToDoc(px);
            if (clickCount == 2 && Editor.Tool == ToolKind.Select)
            {
                if (!Editor.PointerDoubleClick(doc, 5 * scale)) RequestAction(Map(_settings.Capture.EnterAction));
                return;
            }

            if (Editor.PointerDown(doc, Mods(), 5 * scale))
            {
                if (Editor.IsGestureActive || Editor.Tool is not (ToolKind.Steps or ToolKind.Text or ToolKind.Eyedropper)) _drag = DragKind.Editor;
                RaiseChanged();
                return;
            }

            if (!LassoClosed)
            {
                Selection.BeginAdjust(SelectionHit.Inside, px);
                _drag = DragKind.AdjustSelection;
                RaiseChanged();
            }

            return;
        }

        // A press outside the region starts a new one.
        if (Mode == OverlayMode.Freeform)
        {
            ResetSelection();
            _lasso.Add(px);
            _drag = DragKind.Lasso;
        }
        else
        {
            Editor.ClearSelection();
            Phase = OverlayPhase.Selecting;
            Selection.BeginDrag(px);
            _drag = DragKind.NewSelection;
        }

        RaiseChanged();
    }

    public void MouseMove(PixelPoint px)
    {
        if (_ended) return;
        CursorPx = px;
        var mods = Mods();

        switch (_drag)
        {
            case DragKind.NewSelection:
                // Holding Space moves the whole rectangle instead of resizing it.
                if (Keyboard.IsKeyDown(Key.Space)) Selection.MoveAnchor(px.X - _lastDragPx.X, px.Y - _lastDragPx.Y);
                Selection.DragTo(px, mods.HasFlag(PointerModifiers.Shift), mods.HasFlag(PointerModifiers.Ctrl));
                HoverTarget = Selection.HasSelection ? null : HoverTarget;
                break;

            case DragKind.AdjustSelection:
                Selection.AdjustTo(px, mods.HasFlag(PointerModifiers.Shift));
                break;

            case DragKind.Editor:
                Editor.PointerMove(ToDoc(px), mods);
                break;

            case DragKind.Lasso:
                if (_lasso.Count == 0 || Math.Abs(_lasso[^1].X - px.X) + Math.Abs(_lasso[^1].Y - px.Y) >= 2)
                    _lasso.Add(new PixelPoint(Math.Clamp(px.X, Frame.VirtualBounds.Left, Frame.VirtualBounds.Right - 1), Math.Clamp(px.Y, Frame.VirtualBounds.Top, Frame.VirtualBounds.Bottom - 1)));
                break;

            default:
                if (Phase == OverlayPhase.Selecting) UpdateHover(px, mods.HasFlag(PointerModifiers.Alt));
                break;
        }

        _lastDragPx = px;
        RaiseChanged();
    }

    public void MouseUp(PixelPoint px, MouseButton button)
    {
        if (_ended || button != MouseButton.Left) return;
        CursorPx = px;
        var drag = _drag;
        _drag = DragKind.None;

        switch (drag)
        {
            case DragKind.NewSelection:
                if (Selection.EndDrag())
                {
                    EnterAdjusting();
                }
                else if (SnapEnabled && HoverTarget is { } target)
                {
                    // A plain click on a highlighted window or control selects exactly that.
                    Selection.SetRegion(target.Bounds);
                    if (Selection.HasSelection) EnterAdjusting();
                }
                else
                {
                    Phase = OverlayPhase.Selecting;
                }

                break;

            case DragKind.AdjustSelection:
                Selection.EndAdjust();
                break;

            case DragKind.Editor:
                Editor.PointerUp(ToDoc(px), Mods());
                break;

            case DragKind.Lasso:
            {
                var bounds = LassoBounds();
                if (_lasso.Count >= 3 && bounds.Width >= SelectionModel.MinDrag && bounds.Height >= SelectionModel.MinDrag)
                {
                    LassoClosed = true;
                    Selection.SetRegion(bounds);
                    EnterAdjusting();
                }
                else
                {
                    _lasso.Clear();
                }

                break;
            }
        }

        RaiseChanged();
    }

    private PixelRect LassoBounds()
    {
        if (_lasso.Count == 0) return PixelRect.Empty;
        int l = _lasso.Min(p => p.X), t = _lasso.Min(p => p.Y), r = _lasso.Max(p => p.X), b = _lasso.Max(p => p.Y);
        return PixelRect.FromLTRB(l, t, r + 1, b + 1);
    }

    private void EnterAdjusting()
    {
        Phase = OverlayPhase.Adjusting;
        HoverTarget = null;
        var after = _afterRegionOverride ?? _settings.Capture.AfterRegion;
        if (after == AfterCaptureAction.ShowToolbar) SelectionCompleted?.Invoke();
        else if (Mode != OverlayMode.Preselected) RequestAction(Map(after));
    }

    private void ResetSelection()
    {
        Editor.CancelPointer();
        Editor.ClearSelection();
        Selection.Clear();
        _lasso.Clear();
        LassoClosed = false;
        Phase = OverlayPhase.Selecting;
        RaiseChanged();
    }

    private void CancelDrag()
    {
        var drag = _drag;
        _drag = DragKind.None;
        switch (drag)
        {
            case DragKind.NewSelection:
                Selection.Clear();
                Phase = OverlayPhase.Selecting;
                break;
            case DragKind.AdjustSelection:
                Selection.EndAdjust();
                break;
            case DragKind.Editor:
                Editor.CancelPointer();
                break;
            case DragKind.Lasso:
                _lasso.Clear();
                break;
        }

        RaiseChanged();
    }

    /// <summary>The mouse was taken away mid-drag (another window grabbed it); abandon the drag safely.</summary>
    public void CaptureLost()
    {
        if (_drag != DragKind.None) CancelDrag();
    }

    public void Wheel(int delta)
    {
        if (_ended || Phase != OverlayPhase.Selecting || !SnapEnabled || _drag != DragKind.None) return;
        // Scroll down to go deeper into nested controls, up to come back out.
        _depth = Math.Clamp(_depth + (delta < 0 ? 1 : -1), 0, 12);
        UpdateHover(CursorPx, alt: false);
        RaiseChanged();
    }

    // ------------------------------------------------------------------ snapping

    private void UpdateHover(PixelPoint px, bool alt)
    {
        if (!SnapEnabled)
        {
            HoverTarget = null;
            return;
        }

        var top = _snap.WindowAt(px);
        if (top is null)
        {
            HoverTarget = null;
            _chain = null;
            return;
        }

        if (_chain is not null && (_chain.Count == 0 || _chain[0].Bounds != top.Bounds)) _chain = null;
        HoverTarget = Pick(top, alt);

        bool wantsDepth = _depth > 0 || alt;
        bool stale = _chain is null || Math.Abs(px.X - _chainPoint.X) + Math.Abs(px.Y - _chainPoint.Y) >= 4;
        if (wantsDepth && stale) RequestChain(px, alt);
    }

    private SnapTarget Pick(SnapTarget top, bool alt)
    {
        if (_chain is not { Count: > 0 } chain) return top;
        int index = alt ? chain.Count - 1 : Math.Min(_depth, chain.Count - 1);
        return chain[index];
    }

    private void RequestChain(PixelPoint px, bool alt)
    {
        _chainCts?.Cancel();
        _chainCts?.Dispose();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        _chainCts = cts;
        _chainPoint = px;
        var context = SynchronizationContext.Current;

        // UI Automation makes cross-process calls that can stall; never run it on the UI thread.
        Task.Run(() =>
        {
            try
            {
                return _snap.ChainAt(px, cts.Token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }).ContinueWith(
            task =>
            {
                if (_ended || cts.IsCancellationRequested || task.IsFaulted || task.Result is not { Count: > 0 } chain) return;
                void Apply()
                {
                    if (_ended || Phase != OverlayPhase.Selecting || _drag != DragKind.None) return;
                    _chain = chain;
                    if (_snap.WindowAt(CursorPx) is { } top && top.Bounds == chain[0].Bounds) HoverTarget = Pick(top, alt);
                    RaiseChanged();
                }

                if (context is not null) context.Post(_ => Apply(), null);
                else Apply();
            },
            TaskScheduler.Default);
    }

    // ------------------------------------------------------------------ keyboard

    /// <summary>
    /// Handles Esc. Returns true when the session should stay open (a drag was cancelled) and false
    /// after it asked to be cancelled entirely.
    /// </summary>
    public bool Escape()
    {
        if (_ended) return false;
        if (_drag != DragKind.None)
        {
            CancelDrag();
            return true;
        }

        RequestAction(OverlayAction.Cancel);
        return false;
    }

    public static OverlayAction Map(AfterCaptureAction action) => action switch
    {
        AfterCaptureAction.Save => OverlayAction.Save,
        AfterCaptureAction.OpenEditor => OverlayAction.Edit,
        AfterCaptureAction.Upload => OverlayAction.Upload,
        AfterCaptureAction.Pin => OverlayAction.Pin,
        _ => OverlayAction.Copy,
    };

    public bool KeyDown(Key key, ModifierKeys modifiers)
    {
        if (_ended || IsTextEditing) return false;
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control), shift = modifiers.HasFlag(ModifierKeys.Shift);
        bool hasRegion = Phase == OverlayPhase.Adjusting && Selection.HasSelection;

        if (key is Key.Enter or Key.Return)
        {
            if (hasRegion) RequestAction(Map(_settings.Capture.EnterAction));
            return true;
        }

        if (ctrl)
        {
            switch (key)
            {
                case Key.C: RequestAction(OverlayAction.Copy); return true;
                case Key.S: RequestAction(shift ? OverlayAction.SaveAs : OverlayAction.Save); return true;
                case Key.U: RequestAction(OverlayAction.Upload); return true;
                case Key.P: RequestAction(OverlayAction.Print); return true;
                case Key.E: RequestAction(OverlayAction.Edit); return true;
                case Key.T: RequestAction(OverlayAction.Pin); return true;
                case Key.Z:
                    if (shift) Editor.Redo(); else Editor.Undo();
                    return true;
                case Key.Y: Editor.Redo(); return true;
                case Key.D: Editor.DuplicateSelected(); return true;
                case Key.A:
                    // Select the whole monitor under the cursor.
                    LassoClosed = false;
                    _lasso.Clear();
                    Selection.SetRegion(Topology.FromPoint(CursorPx).Bounds);
                    if (Selection.HasSelection) Phase = OverlayPhase.Adjusting;
                    HoverTarget = null;
                    RaiseChanged();
                    return true;
            }
        }

        if (key is Key.Delete or Key.Back)
        {
            Editor.DeleteSelected();
            return true;
        }

        if (key is Key.Left or Key.Right or Key.Up or Key.Down)
        {
            int step = shift ? 10 : 1;
            int dx = key == Key.Left ? -step : key == Key.Right ? step : 0;
            int dy = key == Key.Up ? -step : key == Key.Down ? step : 0;
            if (!Editor.Selection.IsEmpty) Editor.NudgeSelected(new Vec2(dx, dy));
            else if (hasRegion && !LassoClosed)
            {
                if (ctrl) Selection.ResizeBy(dx, dy);
                else Selection.Nudge(dx, dy);
            }

            return true;
        }

        if (modifiers == ModifierKeys.None)
        {
            if (Phase == OverlayPhase.Selecting && key == Key.M)
            {
                ShowMagnifier = !ShowMagnifier;
                RaiseChanged();
                return true;
            }

            if (Phase == OverlayPhase.Selecting && key == Key.Tab)
            {
                SnapEnabled = !SnapEnabled;
                HoverTarget = null;
                RaiseChanged();
                return true;
            }

            if (hasRegion && Editing.ToolPalette.TryGetToolForKey(key, out var tool))
            {
                Editor.SetTool(tool);
                return true;
            }
        }

        return false;
    }

    // ------------------------------------------------------------------ result

    /// <summary>Cuts the selected region out of the frozen frame together with its annotations.</summary>
    public OverlayResult? BuildResult()
    {
        if (Selection.Current is not { IsEmpty: false } region) return null;
        var docRect = region.Offset(-Origin.X, -Origin.Y);
        var state = Editor.State.Crop(docRect);

        if (LassoClosed && _lasso.Count >= 3)
        {
            var polygon = _lasso.Select(p => new Vec2(p.X - region.X + 0.5, p.Y - region.Y + 0.5)).ToList();
            state = state with { Base = ImageEffects.MaskPolygon(state.Base, polygon) };
        }

        string app = string.Empty, title = string.Empty;
        if (_snap.WindowAt(region.Center) is { } window)
        {
            title = window.Label;
            app = WindowList.GetProcessName(WindowList.GetProcessId(window.Window));
        }

        return new OverlayResult(state, region, app, title);
    }
}
