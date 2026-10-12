using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShotIt.Core.Annotations;
using ShotIt.Core.Editing;
using ShotIt.Core.Geometry;
using ShotIt.Rendering;

namespace ShotIt.App.Editing;

/// <summary>
/// Shows a document (image + annotations) through a view transform and draws selection handles.
/// The capture overlay hosts one per monitor over a shared session; the editor window hosts one with zoom.
/// Input is forwarded by the host through the Pointer methods.
/// </summary>
public sealed class EditorSurface : FrameworkElement
{
    private const double HandleSize = 8;
    private static readonly Brush Accent = Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0x7B, 0xFF)));
    private static readonly Brush CropShade = Freeze(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)));

    private readonly AnnotationRenderer _renderer = new();
    private EditorSession? _session;

    public EditorSurface()
    {
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    public EditorSession? Session
    {
        get => _session;
        set
        {
            if (ReferenceEquals(_session, value)) return;
            if (_session is not null) _session.Changed -= OnSessionChanged;
            _session = value;
            if (_session is not null) _session.Changed += OnSessionChanged;
            InvalidateVisual();
        }
    }

    /// <summary>View units (DIPs) per document pixel.</summary>
    public double Zoom { get; set; } = 1;

    /// <summary>The document coordinate shown at this element's top-left corner.</summary>
    public Vec2 Origin { get; set; }

    public bool DrawBase { get; set; } = true;

    /// <summary>Annotations are clipped to this document rectangle (the capture region on the overlay).</summary>
    public RectD? ClipArea { get; set; }

    /// <summary>Hides one annotation while its text is being edited in a text box on top of it.</summary>
    public Guid? HiddenAnnotation { get; set; }

    private void OnSessionChanged(object? sender, EventArgs e) => InvalidateVisual();

    public Vec2 ToDoc(Point view) => new(view.X / Zoom + Origin.X, view.Y / Zoom + Origin.Y);

    public Point FromDoc(Vec2 doc) => new((doc.X - Origin.X) * Zoom, (doc.Y - Origin.Y) * Zoom);

    public Rect FromDoc(RectD doc) => new(FromDoc(doc.TopLeft), FromDoc(doc.BottomRight));

    /// <summary>Hit tolerance in document pixels that corresponds to a comfortable 5 DIP on screen.</summary>
    public double Tolerance => 5 / Zoom;

    public static PointerModifiers CurrentModifiers()
    {
        var keys = Keyboard.Modifiers;
        var mods = PointerModifiers.None;
        if (keys.HasFlag(ModifierKeys.Shift)) mods |= PointerModifiers.Shift;
        if (keys.HasFlag(ModifierKeys.Control)) mods |= PointerModifiers.Ctrl;
        if (keys.HasFlag(ModifierKeys.Alt)) mods |= PointerModifiers.Alt;
        return mods;
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_session is null) return;
        var state = _session.State;
        if (HiddenAnnotation is { } hidden && state.Find(hidden) is not null) state = state.Remove([hidden]);

        dc.PushTransform(new ScaleTransform(Zoom, Zoom));
        dc.PushTransform(new TranslateTransform(-Origin.X, -Origin.Y));

        if (DrawBase) dc.DrawImage(state.Base.ToCachedBitmap(), new Rect(0, 0, state.Base.Width, state.Base.Height));

        if (ClipArea is { } clip) dc.PushClip(new RectangleGeometry(clip.ToWpf()));
        _renderer.Render(dc, state, _session.Draft, ClipArea, drawBase: false);
        if (ClipArea is not null) dc.Pop();

        DrawCropDraft(dc, state);
        DrawSelectionAdorners(dc);

        dc.Pop();
        dc.Pop();
    }

    private void DrawCropDraft(DrawingContext dc, DocState state)
    {
        if (_session?.CropDraft is not { } crop) return;
        var full = new RectangleGeometry(new Rect(0, 0, state.Base.Width, state.Base.Height));
        var shade = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(crop.ToWpf()));
        dc.DrawGeometry(CropShade, null, shade);
        dc.DrawRectangle(null, new Pen(Brushes.White, 1.5 / Zoom), crop.ToWpf());
    }

    private void DrawSelectionAdorners(DrawingContext dc)
    {
        if (_session is null || _session.Selection.IsEmpty) return;
        double unit = 1 / Zoom;
        var outline = new Pen(Accent, 1 * unit) { DashStyle = new DashStyle([4, 3], 0) };
        var handlePen = new Pen(Accent, 1.5 * unit);
        bool single = _session.Selection.Count == 1;

        foreach (var item in _session.SelectedItems)
        {
            if (item.Id == HiddenAnnotation) continue;
            if (item is not SegmentAnnotation and not LineAnnotation)
                dc.DrawRectangle(null, outline, item.Bounds.Inflate(3 * unit).ToWpf());
            if (!single) continue;

            foreach (var handle in item.GetHandles())
            {
                double half = HandleSize / 2 * unit;
                var p = handle.Position.ToWpf();
                if (handle.Kind is HandleKind.Control or HandleKind.Special or HandleKind.Endpoint)
                    dc.DrawEllipse(handle.Kind == HandleKind.Endpoint ? Brushes.White : Accent, handlePen, p, half, half);
                else
                    dc.DrawRectangle(Brushes.White, handlePen, new Rect(p.X - half, p.Y - half, half * 2, half * 2));
            }
        }
    }

    /// <summary>The cursor that describes what a press at this point would do.</summary>
    public Cursor? CursorAt(Point view)
    {
        if (_session is null) return null;
        var p = ToDoc(view);
        if (_session.Tool != ToolKind.Select)
            return _session.Tool == ToolKind.Text ? Cursors.IBeam : Cursors.Cross;
        if (_session.HitTestHandle(p, Math.Max(Tolerance, 6 / Zoom)) is { } hit)
        {
            return hit.Handle.Kind switch
            {
                HandleKind.Corner => hit.Handle.Id is RectHandles.TopLeft or RectHandles.BottomRight ? Cursors.SizeNWSE : Cursors.SizeNESW,
                HandleKind.Edge => hit.Handle.Id is RectHandles.Top or RectHandles.Bottom ? Cursors.SizeNS : Cursors.SizeWE,
                _ => Cursors.Hand,
            };
        }

        return _session.HitTest(p, Tolerance) is not null ? Cursors.SizeAll : null;
    }
}

/// <summary>
/// Shows a text box on top of an <see cref="EditorSurface"/> whenever the session asks for text
/// (text tool, callout, double-click on existing text) and writes the result back.
/// </summary>
public sealed class TextEditHost
{
    private readonly Canvas _canvas;
    private readonly EditorSurface _surface;
    private readonly Func<Vec2, bool> _ownsPoint;
    private TextBox? _box;
    private TextEditRequest? _request;
    private bool _closing;

    /// <param name="canvas">Canvas that covers the surface; the text box is placed inside it.</param>
    /// <param name="surface">Surface whose transform positions the box.</param>
    /// <param name="ownsPoint">On multi-monitor overlays several hosts hear the same request; only the one whose
    /// view contains the point reacts.</param>
    public TextEditHost(Canvas canvas, EditorSurface surface, Func<Vec2, bool>? ownsPoint = null)
    {
        _canvas = canvas;
        _surface = surface;
        _ownsPoint = ownsPoint ?? (_ => true);
    }

    public bool IsEditing => _box is not null;

    public event Action? EditingChanged;

    public void Attach(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.TextEditRequested += OnRequested;
    }

    public void Detach(EditorSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.TextEditRequested -= OnRequested;
        Close(commit: false);
    }

    private void OnRequested(object? sender, TextEditRequest request)
    {
        if (!_ownsPoint(request.Area.TopLeft)) return;
        Close(commit: true);

        double zoom = _surface.Zoom;
        bool boxed = request.Area.Width > 1; // callouts provide a box, plain text only a position
        var topLeft = _surface.FromDoc(request.Area.TopLeft);
        var color = request.Color.ToWpf();
        // A text box must stay readable whatever is underneath: use a backdrop that contrasts with the text colour.
        var backdrop = request.Color.Luminance > 0.6 ? Color.FromArgb(200, 30, 30, 30) : Color.FromArgb(215, 255, 255, 255);

        var box = new TextBox
        {
            Text = request.InitialText,
            AcceptsReturn = true,
            AcceptsTab = false,
            FontFamily = new FontFamily(request.FontFamily + ", Segoe UI"),
            FontSize = Math.Max(8, request.FontSize * zoom),
            FontWeight = request.Bold || boxed ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush(color),
            CaretBrush = new SolidColorBrush(color),
            Background = new SolidColorBrush(backdrop),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x3D, 0x7B, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(Math.Max(1, ShotIt.Core.Annotations.TextAnnotation.Padding * zoom - 3)),
            MinWidth = boxed ? request.Area.Width * zoom : 60,
            TextWrapping = boxed ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextAlignment = boxed ? TextAlignment.Center : TextAlignment.Left,
            Style = null,
            SnapsToDevicePixels = true,
        };
        if (boxed)
        {
            box.Width = Math.Max(60, request.Area.Width * zoom);
            box.MinHeight = request.Area.Height * zoom;
        }

        Canvas.SetLeft(box, topLeft.X);
        Canvas.SetTop(box, topLeft.Y);
        box.LostKeyboardFocus += (_, _) => Close(commit: true);
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close(commit: false);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                Close(commit: true);
                e.Handled = true;
            }
        };

        _box = box;
        _request = request;
        _surface.HiddenAnnotation = request.TargetId;
        _surface.InvalidateVisual();
        _canvas.Children.Add(box);
        box.Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            Keyboard.Focus(box);
            box.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
        EditingChanged?.Invoke();
    }

    /// <summary>Ends editing. Committing stores the text; cancelling leaves the document as it was.</summary>
    public void Close(bool commit)
    {
        if (_box is null || _request is null || _closing) return;
        _closing = true;
        try
        {
            var box = _box;
            var request = _request;
            _box = null;
            _request = null;
            _canvas.Children.Remove(box);
            _surface.HiddenAnnotation = null;
            if (commit && _surface.Session is { } session) session.CommitText(request, box.Text);
            _surface.InvalidateVisual();
        }
        finally
        {
            _closing = false;
        }

        EditingChanged?.Invoke();
    }
}
