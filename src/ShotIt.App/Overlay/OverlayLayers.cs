using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShotIt.App.Editing;
using ShotIt.App.Themes;
using ShotIt.Core.Display;
using ShotIt.Core.Geometry;
using ShotIt.Rendering;

namespace ShotIt.App.Overlay;

/// <summary>
/// Draws everything that is not image content on one monitor: the dimmed surroundings, the region
/// border and handles, the size label, the snap highlight and the crosshair.
/// </summary>
public sealed class OverlayLayer : FrameworkElement
{
    private static readonly Brush Dim = Frozen(Color.FromArgb(115, 0, 0, 0));
    private static readonly Brush Accent = Frozen(Color.FromRgb(0x3D, 0x7B, 0xFF));
    private static readonly Brush LabelBackground = Frozen(Color.FromArgb(230, 0x1F, 0x21, 0x25));
    private static readonly Brush CrosshairBrush = Frozen(Color.FromArgb(150, 255, 255, 255));
    private static readonly Brush SnapFill = Frozen(Color.FromArgb(28, 0x3D, 0x7B, 0xFF));

    private OverlaySession? _session;
    private MonitorDescriptor? _monitor;

    public OverlayLayer()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Configure(OverlaySession? session, MonitorDescriptor? monitor)
    {
        _session = session;
        _monitor = monitor;
        InvalidateVisual();
    }

    /// <summary>When true the hint text is shown (only on the monitor under the cursor, only before any input).</summary>
    public bool ShowHint { get; set; }

    private Rect ToLocal(PixelRect r)
    {
        var m = _monitor!;
        return new Rect(
            DpiMath.PxToDip(r.X - m.Bounds.X, m.Dpi), DpiMath.PxToDip(r.Y - m.Bounds.Y, m.Dpi),
            DpiMath.PxToDip(r.Width, m.Dpi), DpiMath.PxToDip(r.Height, m.Dpi));
    }

    private Point ToLocal(PixelPoint p)
    {
        var m = _monitor!;
        return new Point(DpiMath.PxToDip(p.X - m.Bounds.X, m.Dpi), DpiMath.PxToDip(p.Y - m.Bounds.Y, m.Dpi));
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_session is null || _monitor is null) return;
        var session = _session;
        var monitor = _monitor;
        var full = new Rect(0, 0, DpiMath.PxToDip(monitor.Bounds.Width, monitor.Dpi), DpiMath.PxToDip(monitor.Bounds.Height, monitor.Dpi));
        double px = 96.0 / monitor.Dpi; // one physical pixel in DIPs

        var region = session.Selection.Current;
        bool lasso = session.Lasso.Count > 1;

        // Dim everything except the region (or the freeform outline, or the hovered snap target).
        Geometry? hole = null;
        if (lasso)
        {
            var outline = new StreamGeometry();
            using (var ctx = outline.Open())
            {
                ctx.BeginFigure(ToLocal(session.Lasso[0]), true, true);
                for (int i = 1; i < session.Lasso.Count; i++) ctx.LineTo(ToLocal(session.Lasso[i]), true, false);
            }

            hole = outline;
        }
        else if (region is { } r)
        {
            hole = new RectangleGeometry(ToLocal(r));
        }
        else if (session.HoverTarget is { } hover)
        {
            hole = new RectangleGeometry(ToLocal(hover.Bounds));
        }

        if (hole is null) dc.DrawRectangle(Dim, null, full);
        else dc.DrawGeometry(Dim, null, new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(full), hole));

        if (lasso)
        {
            dc.DrawGeometry(null, new Pen(Brushes.White, 1.5) { DashStyle = session.LassoClosed ? DashStyles.Solid : DashStyles.Dash }, hole);
        }

        if (region is null && !lasso && session.HoverTarget is { } target && session.Phase == OverlayPhase.Selecting)
        {
            var rect = ToLocal(target.Bounds);
            dc.DrawRectangle(SnapFill, new Pen(Accent, 2), Deflate(rect, 1));
            if (target.Label.Length > 0) DrawLabel(dc, target.Label.Length > 70 ? target.Label[..70] + "…" : target.Label, rect, full, above: false);
        }

        if (region is { } selected && !session.LassoClosed)
        {
            var rect = ToLocal(selected);
            if (rect.IntersectsWith(full))
            {
                // The border sits just outside the region so it never covers captured pixels.
                DrawFrame(dc, rect, px);
                if (session.Phase == OverlayPhase.Adjusting && !session.IsShapingRegion) DrawHandles(dc, rect);
            }
        }

        if (region is { } sized)
        {
            string text = string.Create(CultureInfo.InvariantCulture, $"{sized.Width} × {sized.Height}");
            // Show the label only on the monitor that holds the region's top-left corner.
            if (monitor.Bounds.Contains(new PixelPoint(Math.Clamp(sized.X, monitor.Bounds.Left, monitor.Bounds.Right - 1), Math.Clamp(sized.Y, monitor.Bounds.Top, monitor.Bounds.Bottom - 1)))
                && session.Topology.FromPoint(sized.Location) == monitor)
                DrawLabel(dc, text, ToLocal(sized), full, above: true);
        }

        if (session.Phase == OverlayPhase.Selecting && !session.IsDragging && monitor.Bounds.Contains(session.CursorPx))
        {
            var c = ToLocal(session.CursorPx);
            dc.DrawRectangle(CrosshairBrush, null, new Rect(0, c.Y, full.Width, px));
            dc.DrawRectangle(CrosshairBrush, null, new Rect(c.X, 0, px, full.Height));
        }

        if (ShowHint && session.Phase == OverlayPhase.Selecting && !session.IsDragging)
            DrawHint(dc, full, session);
    }

    private static Rect Deflate(Rect r, double d) =>
        r.Width > 2 * d && r.Height > 2 * d ? new Rect(r.X + d, r.Y + d, r.Width - 2 * d, r.Height - 2 * d) : r;

    private static void DrawFrame(DrawingContext dc, Rect rect, double px)
    {
        double t = Math.Max(px, 1);
        dc.DrawRectangle(Accent, null, new Rect(rect.Left - t, rect.Top - t, rect.Width + 2 * t, t));
        dc.DrawRectangle(Accent, null, new Rect(rect.Left - t, rect.Bottom, rect.Width + 2 * t, t));
        dc.DrawRectangle(Accent, null, new Rect(rect.Left - t, rect.Top, t, rect.Height));
        dc.DrawRectangle(Accent, null, new Rect(rect.Right, rect.Top, t, rect.Height));
    }

    private static void DrawHandles(DrawingContext dc, Rect rect)
    {
        const double size = 7;
        var pen = new Pen(Accent, 1.5);
        foreach (var p in new[]
        {
            rect.TopLeft, new Point(rect.Left + rect.Width / 2, rect.Top), rect.TopRight,
            new Point(rect.Right, rect.Top + rect.Height / 2), rect.BottomRight,
            new Point(rect.Left + rect.Width / 2, rect.Bottom), rect.BottomLeft, new Point(rect.Left, rect.Top + rect.Height / 2),
        })
        {
            dc.DrawRectangle(Brushes.White, pen, new Rect(p.X - size / 2, p.Y - size / 2, size, size));
        }
    }

    private static void DrawLabel(DrawingContext dc, string text, Rect anchor, Rect area, bool above)
    {
        var formatted = TextLayout.Create(text, "Segoe UI", 12, true, false, Brushes.White);
        double w = formatted.Width + 14, h = formatted.Height + 6;
        double x = Math.Clamp(anchor.Left, area.Left + 4, Math.Max(area.Left + 4, area.Right - w - 4));
        double y = above ? anchor.Top - h - 6 : anchor.Top + 6;
        if (y < area.Top + 4) y = anchor.Top + 6;
        y = Math.Clamp(y, area.Top + 4, Math.Max(area.Top + 4, area.Bottom - h - 4));
        dc.DrawRoundedRectangle(LabelBackground, null, new Rect(x, y, w, h), 4, 4);
        dc.DrawText(formatted, new Point(x + 7, y + 3));
    }

    private static void DrawHint(DrawingContext dc, Rect area, OverlaySession session)
    {
        string text = session.Purpose switch
        {
            OverlayPurpose.Scrolling => "Select the area that scrolls  ·  Click a window or pane to grab it  ·  Esc to cancel",
            OverlayPurpose.RecordVideo or OverlayPurpose.RecordGif => "Select the area to record  ·  Click a window to grab it  ·  Esc to cancel",
            _ => null,
        } ?? session.Mode switch
        {
            OverlayMode.Freeform => "Draw around what you want to capture  ·  Esc to cancel",
            OverlayMode.Window => "Click a window to capture it  ·  Scroll to pick a part  ·  Esc to cancel",
            _ => "Drag to select  ·  Click a window to grab it  ·  Scroll for inner parts  ·  Ctrl+A whole screen  ·  Esc to cancel",
        };
        var formatted = TextLayout.Create(text, "Segoe UI", 13, false, false, Brushes.White);
        double w = formatted.Width + 28, h = formatted.Height + 14;
        var rect = new Rect(area.Left + (area.Width - w) / 2, area.Top + 28, w, h);
        dc.DrawRoundedRectangle(LabelBackground, null, rect, h / 2, h / 2);
        dc.DrawText(formatted, new Point(rect.X + 14, rect.Y + 7));
    }
}

/// <summary>The pixel loupe that follows the cursor: an 8x zoom of the frozen screen plus position and colour.</summary>
public sealed class MagnifierView : FrameworkElement
{
    private const int Cells = 15;          // pixels shown per side (odd, so there is a centre pixel)
    private const double CellSize = 8;     // DIPs per magnified pixel
    public const double BoxSize = Cells * CellSize;
    public const double TotalHeight = BoxSize + 40;

    private static readonly Brush Background = MakeBrush(Color.FromArgb(240, 0x1F, 0x21, 0x25));
    private static readonly Pen GridPen = MakePen(Color.FromArgb(40, 255, 255, 255), 1);
    private static readonly Pen BorderPen = MakePen(Color.FromArgb(120, 255, 255, 255), 1);
    private static readonly Pen CenterOuter = MakePen(Colors.Black, 1);
    private static readonly Pen CenterInner = MakePen(Colors.White, 1);

    private OverlaySession? _session;
    private ImageBrush? _brush;

    public MagnifierView()
    {
        Width = BoxSize;
        Height = TotalHeight;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
    }

    private static SolidColorBrush MakeBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Pen MakePen(Color c, double thickness)
    {
        var p = new Pen(MakeBrush(c), thickness);
        p.Freeze();
        return p;
    }

    public void Configure(OverlaySession? session)
    {
        _session = session;
        _brush = session is null ? null : new ImageBrush(session.Bitmap) { ViewboxUnits = BrushMappingMode.Absolute, Stretch = Stretch.Fill };
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_session is null || _brush is null) return;
        var cursor = _session.CursorPx;
        var bounds = _session.Frame.VirtualBounds;
        int cx = cursor.X - bounds.X, cy = cursor.Y - bounds.Y;

        var outer = new Rect(0, 0, BoxSize, TotalHeight);
        dc.PushClip(new RectangleGeometry(outer, 6, 6));
        dc.DrawRectangle(Background, null, outer);

        _brush.Viewbox = new Rect(cx - Cells / 2, cy - Cells / 2, Cells, Cells);
        dc.DrawRectangle(_brush, null, new Rect(0, 0, BoxSize, BoxSize));

        for (int i = 1; i < Cells; i++)
        {
            double p = i * CellSize + 0.5;
            dc.DrawLine(GridPen, new Point(p, 0), new Point(p, BoxSize));
            dc.DrawLine(GridPen, new Point(0, p), new Point(BoxSize, p));
        }

        double c = Cells / 2 * CellSize;
        dc.DrawRectangle(null, CenterOuter, new Rect(c - 0.5, c - 0.5, CellSize + 1, CellSize + 1));
        dc.DrawRectangle(null, CenterInner, new Rect(c + 0.5, c + 0.5, CellSize - 1, CellSize - 1));

        string position = string.Create(CultureInfo.InvariantCulture, $"{cursor.X}, {cursor.Y}");
        string hex = string.Empty;
        if (cx >= 0 && cy >= 0 && cx < _session.Frame.Image.Width && cy < _session.Frame.Image.Height)
        {
            var color = _session.Frame.Image.GetPixel(cx, cy);
            hex = color.WithAlpha(255).ToHex();
            dc.DrawRoundedRectangle(color.WithAlpha(255).ToBrush(), BorderPen, new Rect(8, BoxSize + 22, 12, 12), 2, 2);
        }

        dc.DrawText(TextLayout.Create(position, "Segoe UI", 11, false, false, Brushes.White), new Point(8, BoxSize + 4));
        dc.DrawText(TextLayout.Create(hex, "Consolas", 11, false, false, Brushes.White), new Point(26, BoxSize + 21));
        dc.Pop();
        dc.DrawRoundedRectangle(null, BorderPen, new Rect(0.5, 0.5, BoxSize - 1, TotalHeight - 1), 6, 6);
    }
}

/// <summary>The row of capture actions shown under the region: copy, save, upload, pin and so on.</summary>
public sealed class ActionBar : Border
{
    /// <param name="onScreen">True on the capture overlay, where the picture is still a live part of the screen.</param>
    public ActionBar(Action<OverlayAction> invoke, bool includeEdit = true, bool onScreen = false)
    {
        ArgumentNullException.ThrowIfNull(invoke);
        Style = (Style)Application.Current.FindResource("BarPanel");
        MouseDown += (_, e) => e.Handled = true;
        MouseUp += (_, e) => e.Handled = true;

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        void Add(string icon, string tip, OverlayAction action)
        {
            var button = ToolPalette.MakeButton(Icons.Create(icon), tip);
            button.Click += (_, _) => invoke(action);
            panel.Children.Add(button);
        }

        Add(Icons.Copy, "Copy to clipboard (Ctrl+C)", OverlayAction.Copy);
        Add(Icons.Save, "Save (Ctrl+S)", OverlayAction.Save);
        Add(Icons.SaveAs, "Save as… (Ctrl+Shift+S)", OverlayAction.SaveAs);
        Add(Icons.Upload, "Upload and copy link (Ctrl+U)", OverlayAction.Upload);
        Add(Icons.Pin, "Pin to screen (Ctrl+T)", OverlayAction.Pin);
        Add(Icons.Ocr, "Copy text from image (OCR)", OverlayAction.Ocr);
        if (includeEdit) Add(Icons.Edit, "Open in editor (Ctrl+E)", OverlayAction.Edit);

        var more = ToolPalette.MakeButton(Icons.Create(Icons.More, thickDots: true), "More actions");
        more.Click += (_, _) =>
        {
            var menu = new ContextMenu { PlacementTarget = more, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            void Item(string header, OverlayAction action, string? gesture = null)
            {
                var item = new MenuItem { Header = header, InputGestureText = gesture ?? string.Empty };
                item.Click += (_, _) => invoke(action);
                menu.Items.Add(item);
            }

            if (onScreen)
            {
                Item("Scrolling capture of this area", OverlayAction.Scroll);
                Item("Record this area as video (MP4)", OverlayAction.RecordVideo);
                Item("Record this area as GIF", OverlayAction.RecordGif);
            }

            Item("Read QR code or barcode", OverlayAction.Qr);
            Item("Search image on the web", OverlayAction.SearchImage);
            Item("Share…", OverlayAction.Share);
            Item("Print…", OverlayAction.Print, "Ctrl+P");
            menu.IsOpen = true;
        };
        panel.Children.Add(more);
        panel.Children.Add(ToolPalette.Separator());
        Add(Icons.Close, "Cancel (Esc)", OverlayAction.Cancel);
        Child = panel;
    }
}
