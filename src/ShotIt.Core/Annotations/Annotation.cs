using System.Text.Json.Serialization;
using ShotIt.Core.Geometry;

namespace ShotIt.Core.Annotations;

public enum HandleKind
{
    Corner,
    Edge,
    Endpoint,
    Control,
    Special,
}

/// <summary>A draggable grip on a selected annotation.</summary>
public readonly record struct Handle(int Id, Vec2 Position, HandleKind Kind);

/// <summary>The eight resize grips of a rectangle. Ids run clockwise from the top-left corner.</summary>
public static class RectHandles
{
    public const int TopLeft = 0, Top = 1, TopRight = 2, Right = 3, BottomRight = 4, Bottom = 5, BottomLeft = 6, Left = 7;

    public static IReadOnlyList<Handle> Get(RectD r) =>
    [
        new(TopLeft, r.TopLeft, HandleKind.Corner),
        new(Top, new Vec2(r.Center.X, r.Top), HandleKind.Edge),
        new(TopRight, r.TopRight, HandleKind.Corner),
        new(Right, new Vec2(r.Right, r.Center.Y), HandleKind.Edge),
        new(BottomRight, r.BottomRight, HandleKind.Corner),
        new(Bottom, new Vec2(r.Center.X, r.Bottom), HandleKind.Edge),
        new(BottomLeft, r.BottomLeft, HandleKind.Corner),
        new(Left, new Vec2(r.Left, r.Center.Y), HandleKind.Edge),
    ];

    /// <summary>
    /// Returns the rectangle after dragging handle <paramref name="id"/> to <paramref name="to"/>.
    /// Dragging past the opposite side flips cleanly; the result is always normalized.
    /// </summary>
    public static RectD Resize(RectD r, int id, Vec2 to, bool keepAspect = false)
    {
        double l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
        switch (id)
        {
            case TopLeft: l = to.X; t = to.Y; break;
            case Top: t = to.Y; break;
            case TopRight: rt = to.X; t = to.Y; break;
            case Right: rt = to.X; break;
            case BottomRight: rt = to.X; b = to.Y; break;
            case Bottom: b = to.Y; break;
            case BottomLeft: l = to.X; b = to.Y; break;
            case Left: l = to.X; break;
            default: return r;
        }

        bool corner = id is TopLeft or TopRight or BottomRight or BottomLeft;
        if (keepAspect && corner && r.Width > 1e-9 && r.Height > 1e-9)
        {
            double aspect = r.Width / r.Height;
            double w = Math.Abs(rt - l), h = Math.Abs(b - t);
            if (w / aspect > h) h = w / aspect; else w = h * aspect;
            // Grow from the corner opposite the dragged one.
            if (id is TopLeft or BottomLeft) l = rt - Math.Sign(rt - l == 0 ? 1 : rt - l) * w; else rt = l + Math.Sign(rt - l == 0 ? 1 : rt - l) * w;
            if (id is TopLeft or TopRight) t = b - Math.Sign(b - t == 0 ? 1 : b - t) * h; else b = t + Math.Sign(b - t == 0 ? 1 : b - t) * h;
        }

        return RectD.FromCorners(new Vec2(l, t), new Vec2(rt, b));
    }
}

/// <summary>
/// Base type for everything the user can draw on a screenshot. Annotations are immutable records:
/// every edit produces a new instance, which makes undo a matter of keeping old lists.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(ArrowAnnotation), "arrow")]
[JsonDerivedType(typeof(LineAnnotation), "line")]
[JsonDerivedType(typeof(ShapeAnnotation), "shape")]
[JsonDerivedType(typeof(FreehandAnnotation), "pen")]
[JsonDerivedType(typeof(HighlighterAnnotation), "highlighter")]
[JsonDerivedType(typeof(AreaHighlightAnnotation), "areaHighlight")]
[JsonDerivedType(typeof(SpotlightAnnotation), "spotlight")]
[JsonDerivedType(typeof(TextAnnotation), "text")]
[JsonDerivedType(typeof(CalloutAnnotation), "callout")]
[JsonDerivedType(typeof(StepAnnotation), "step")]
[JsonDerivedType(typeof(MagnifierAnnotation), "magnifier")]
[JsonDerivedType(typeof(BlurAnnotation), "blur")]
[JsonDerivedType(typeof(PixelateAnnotation), "pixelate")]
[JsonDerivedType(typeof(RedactAnnotation), "redact")]
[JsonDerivedType(typeof(StickerAnnotation), "sticker")]
[JsonDerivedType(typeof(ImageAnnotation), "image")]
[JsonDerivedType(typeof(MeasureAnnotation), "measure")]
public abstract record Annotation
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Geometric bounds in document pixels, not including stroke thickness.</summary>
    [JsonIgnore]
    public abstract RectD Bounds { get; }

    /// <summary>Returns a copy with all geometry mapped through the transform (used for move and resize).</summary>
    public abstract Annotation Transform(Affine2 t);

    /// <summary>True when the point is on the visible part of the annotation, within the given tolerance.</summary>
    public abstract bool HitTest(Vec2 p, double tolerance);

    public virtual IReadOnlyList<Handle> GetHandles() => RectHandles.Get(Bounds);

    /// <summary>Returns a copy with one handle dragged to a new position.</summary>
    public virtual Annotation MoveHandle(int handleId, Vec2 to, bool constrain)
    {
        var from = Bounds;
        var target = RectHandles.Resize(from, handleId, to, constrain);
        return Transform(Affine2.MapRect(from, target));
    }

    public Annotation MoveBy(Vec2 delta) => Transform(Affine2.Translation(delta));

    public Annotation WithNewId() => this with { Id = Guid.NewGuid() };
}

/// <summary>Text measurement is supplied by the UI layer; Core only needs sizes for bounds and hit testing.</summary>
public interface ITextMeasurer
{
    (double Width, double Height) Measure(string text, string fontFamily, double fontSize, bool bold, bool italic);
}

public static class TextMetrics
{
    private sealed class ApproximateMeasurer : ITextMeasurer
    {
        public (double Width, double Height) Measure(string text, string fontFamily, double fontSize, bool bold, bool italic)
        {
            var lines = (text ?? string.Empty).Split('\n');
            int longest = lines.Max(l => l.Length);
            return (Math.Max(1, longest) * fontSize * (bold ? 0.6 : 0.55), lines.Length * fontSize * 1.33);
        }
    }

    /// <summary>Replaced by the WPF measurer at application start. The default is a rough estimate for tests.</summary>
    public static ITextMeasurer Measurer { get; set; } = new ApproximateMeasurer();
}
