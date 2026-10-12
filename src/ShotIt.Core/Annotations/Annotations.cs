using System.Collections.Immutable;
using System.Text.Json.Serialization;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;

namespace ShotIt.Core.Annotations;

public enum ShapeKind
{
    Rectangle,
    RoundedRectangle,
    Ellipse,
}

/// <summary>Shared behaviour for annotations defined by a single rectangle.</summary>
public abstract record RectAnnotation : Annotation
{
    public RectD Rect { get; init; }

    [JsonIgnore]
    public override RectD Bounds => Rect;

    public override Annotation Transform(Affine2 t) => this with { Rect = t.Apply(Rect) };

    public override bool HitTest(Vec2 p, double tolerance) => Rect.Inflate(tolerance).Contains(p);
}

/// <summary>Shared behaviour for two-point annotations (arrow, measure).</summary>
public abstract record SegmentAnnotation : Annotation
{
    public const int StartHandle = 0, EndHandle = 1;

    public Vec2 Start { get; init; }
    public Vec2 End { get; init; }

    [JsonIgnore]
    public override RectD Bounds => RectD.FromCorners(Start, End);

    public override IReadOnlyList<Handle> GetHandles() =>
        [new(StartHandle, Start, HandleKind.Endpoint), new(EndHandle, End, HandleKind.Endpoint)];

    public override Annotation MoveHandle(int handleId, Vec2 to, bool constrain) => handleId switch
    {
        StartHandle => this with { Start = constrain ? GeometryMath.SnapAngle45(End, to) : to },
        EndHandle => this with { End = constrain ? GeometryMath.SnapAngle45(Start, to) : to },
        _ => this,
    };
}

public sealed record ArrowAnnotation : SegmentAnnotation
{
    public const int ControlHandle = 2;

    /// <summary>Optional bend point. When null the arrow is straight.</summary>
    public Vec2? Control { get; init; }
    public bool DoubleHeaded { get; init; }
    public bool Dashed { get; init; }
    public ColorRgba Color { get; init; } = ColorRgba.Red;
    public double StrokeWidth { get; init; } = 4;

    /// <summary>The quadratic control point that makes the curve pass through <see cref="Control"/> at its midpoint.</summary>
    [JsonIgnore]
    public Vec2 BezierControl => Control is { } c
        ? new Vec2(2 * c.X - (Start.X + End.X) / 2, 2 * c.Y - (Start.Y + End.Y) / 2)
        : Vec2.Lerp(Start, End, 0.5);

    [JsonIgnore]
    public override RectD Bounds => Control is { } c ? RectD.FromPoints([Start, End, c]) : RectD.FromCorners(Start, End);

    public override Annotation Transform(Affine2 t) =>
        this with { Start = t.Apply(Start), End = t.Apply(End), Control = Control is { } c ? t.Apply(c) : null };

    public override bool HitTest(Vec2 p, double tolerance)
    {
        double reach = tolerance + StrokeWidth / 2 + 2;
        return Control is null
            ? GeometryMath.DistanceToSegment(p, Start, End) <= reach
            : GeometryMath.DistanceToQuadratic(p, Start, BezierControl, End) <= reach;
    }

    public override IReadOnlyList<Handle> GetHandles() =>
    [
        new(StartHandle, Start, HandleKind.Endpoint),
        new(EndHandle, End, HandleKind.Endpoint),
        new(ControlHandle, Control ?? Vec2.Lerp(Start, End, 0.5), HandleKind.Control),
    ];

    public override Annotation MoveHandle(int handleId, Vec2 to, bool constrain)
    {
        if (handleId != ControlHandle) return base.MoveHandle(handleId, to, constrain);
        // Snap back to straight when the bend point is dragged close to the chord.
        bool straight = GeometryMath.DistanceToSegment(to, Start, End) < Math.Max(3, StrokeWidth);
        return this with { Control = straight ? null : to };
    }
}

public sealed record MeasureAnnotation : SegmentAnnotation
{
    public ColorRgba Color { get; init; } = new(0x00, 0xA2, 0xFF);

    [JsonIgnore]
    public double Length => Start.DistanceTo(End);

    /// <summary>Angle in degrees, 0 = pointing right, counter-clockwise positive.</summary>
    [JsonIgnore]
    public double AngleDegrees
    {
        get
        {
            double a = Math.Atan2(-(End.Y - Start.Y), End.X - Start.X) * 180 / Math.PI;
            return a < 0 ? a + 360 : a;
        }
    }

    public override Annotation Transform(Affine2 t) => this with { Start = t.Apply(Start), End = t.Apply(End) };

    public override bool HitTest(Vec2 p, double tolerance) => GeometryMath.DistanceToSegment(p, Start, End) <= tolerance + 3;
}

/// <summary>Shared behaviour for point-list annotations.</summary>
public abstract record PathAnnotation : Annotation
{
    public ImmutableArray<Vec2> Points { get; init; } = [];
    public ColorRgba Color { get; init; } = ColorRgba.Red;
    public double StrokeWidth { get; init; } = 4;

    [JsonIgnore]
    public override RectD Bounds => RectD.FromPoints(Points);

    public override Annotation Transform(Affine2 t) => this with { Points = [.. Points.Select(t.Apply)] };

    public override bool HitTest(Vec2 p, double tolerance) =>
        GeometryMath.DistanceToPolyline(p, Points) <= tolerance + StrokeWidth / 2;
}

/// <summary>A straight line or a polyline with corner nodes.</summary>
public sealed record LineAnnotation : PathAnnotation
{
    public bool Dashed { get; init; }

    public override IReadOnlyList<Handle> GetHandles() =>
        [.. Points.Select((pt, i) => new Handle(i, pt, HandleKind.Endpoint))];

    public override Annotation MoveHandle(int handleId, Vec2 to, bool constrain)
    {
        if (handleId < 0 || handleId >= Points.Length) return this;
        if (constrain && Points.Length > 1)
            to = GeometryMath.SnapAngle45(Points[handleId == 0 ? 1 : handleId - 1], to);
        return this with { Points = Points.SetItem(handleId, to) };
    }
}

public sealed record FreehandAnnotation : PathAnnotation;

/// <summary>A marker stroke that multiply-blends with the image so text underneath stays readable.</summary>
public sealed record HighlighterAnnotation : PathAnnotation
{
    public HighlighterAnnotation()
    {
        Color = ColorRgba.Yellow;
        StrokeWidth = 20;
    }
}

public sealed record ShapeAnnotation : RectAnnotation
{
    public ShapeKind Kind { get; init; }
    public ColorRgba Color { get; init; } = ColorRgba.Red;
    public double StrokeWidth { get; init; } = 4;
    public ColorRgba? Fill { get; init; }
    public double CornerRadius { get; init; } = 10;
    public bool Dashed { get; init; }

    public override bool HitTest(Vec2 p, double tolerance)
    {
        double reach = tolerance + StrokeWidth / 2;
        var outer = Rect.Inflate(reach);
        if (Kind == ShapeKind.Ellipse)
        {
            if (!GeometryMath.PointInEllipse(p, outer)) return false;
            if (Fill is not null) return true;
            var inner = Rect.Inflate(-reach);
            return inner.IsEmpty || !GeometryMath.PointInEllipse(p, inner);
        }

        if (!outer.Contains(p)) return false;
        if (Fill is not null) return true;
        var hole = Rect.Inflate(-reach);
        return hole.IsEmpty || !hole.Contains(p);
    }
}

/// <summary>A translucent multiply-blended block, like dragging a marker over a paragraph.</summary>
public sealed record AreaHighlightAnnotation : RectAnnotation
{
    public ColorRgba Color { get; init; } = ColorRgba.Yellow;
    public bool Ellipse { get; init; }
}

/// <summary>Keeps this region bright while the rest of the image is dimmed.</summary>
public sealed record SpotlightAnnotation : RectAnnotation
{
    public bool Ellipse { get; init; }

    /// <summary>How dark the surroundings get, 0..1.</summary>
    public double DimOpacity { get; init; } = 0.6;
}

public sealed record BlurAnnotation : RectAnnotation
{
    public double Radius { get; init; } = 12;
}

public sealed record PixelateAnnotation : RectAnnotation
{
    public int BlockSize { get; init; } = 12;
}

/// <summary>A solid block. Unlike blur it cannot be reversed, so it is the right tool for secrets.</summary>
public sealed record RedactAnnotation : RectAnnotation
{
    public ColorRgba Color { get; init; } = ColorRgba.Black;
}

public sealed record StickerAnnotation : RectAnnotation
{
    /// <summary>A built-in sticker name (check, cross, warning, info, question, star, heart) or any short text.</summary>
    public string Glyph { get; init; } = "check";
}

/// <summary>An image pasted into the editor as an object.</summary>
public sealed record ImageAnnotation : RectAnnotation
{
    public byte[] EncodedImage { get; init; } = [];
}

public sealed record TextAnnotation : Annotation
{
    public Vec2 Position { get; init; }
    public string Text { get; init; } = string.Empty;
    public string FontFamily { get; init; } = "Segoe UI";
    public double FontSize { get; init; } = 20;
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public ColorRgba Color { get; init; } = ColorRgba.Red;
    public ColorRgba? Background { get; init; }
    public ColorRgba? Outline { get; init; }

    /// <summary>Padding between the text and its background pill.</summary>
    public const double Padding = 6;

    [JsonIgnore]
    public override RectD Bounds
    {
        get
        {
            var (w, h) = TextMetrics.Measurer.Measure(Text, FontFamily, FontSize, Bold, Italic);
            return new RectD(Position.X, Position.Y, w + 2 * Padding, h + 2 * Padding);
        }
    }

    public override Annotation Transform(Affine2 t) =>
        this with { Position = t.Apply(Position), FontSize = Math.Clamp(FontSize * Math.Abs(t.Sy), 6, 400) };

    public override bool HitTest(Vec2 p, double tolerance) => Bounds.Inflate(tolerance).Contains(p);

    public override IReadOnlyList<Handle> GetHandles()
    {
        var b = Bounds;
        return [new(RectHandles.BottomRight, b.BottomRight, HandleKind.Corner)];
    }

    public override Annotation MoveHandle(int handleId, Vec2 to, bool constrain)
    {
        var b = Bounds;
        if (handleId != RectHandles.BottomRight || b.Height < 1e-6) return this;
        double scale = Math.Max(0.1, (to.Y - b.Top) / b.Height);
        return this with { FontSize = Math.Clamp(FontSize * scale, 6, 400) };
    }
}

/// <summary>A text box with a pointer tail that can be dragged to what it describes.</summary>
public sealed record CalloutAnnotation : Annotation
{
    public const int TailHandle = 8;

    public RectD Box { get; init; }
    public Vec2 Tail { get; init; }
    public string Text { get; init; } = string.Empty;
    public string FontFamily { get; init; } = "Segoe UI";
    public double FontSize { get; init; } = 18;
    public ColorRgba TextColor { get; init; } = ColorRgba.White;
    public ColorRgba Fill { get; init; } = ColorRgba.Red;
    public ColorRgba Stroke { get; init; } = ColorRgba.White;
    public double StrokeWidth { get; init; } = 2;
    public double CornerRadius { get; init; } = 10;

    [JsonIgnore]
    public override RectD Bounds => Box;

    public override Annotation Transform(Affine2 t) => this with { Box = t.Apply(Box), Tail = t.Apply(Tail) };

    public override bool HitTest(Vec2 p, double tolerance) =>
        Box.Inflate(tolerance).Contains(p) || GeometryMath.DistanceToSegment(p, Box.Center, Tail) <= tolerance + 4;

    public override IReadOnlyList<Handle> GetHandles() =>
        [.. RectHandles.Get(Box), new Handle(TailHandle, Tail, HandleKind.Special)];

    public override Annotation MoveHandle(int handleId, Vec2 to, bool constrain) =>
        handleId == TailHandle
            ? this with { Tail = to }
            : this with { Box = RectHandles.Resize(Box, handleId, to, constrain) };
}

/// <summary>A numbered badge for step-by-step instructions.</summary>
public sealed record StepAnnotation : Annotation
{
    public Vec2 Center { get; init; }
    public int Number { get; init; } = 1;
    public double Radius { get; init; } = 16;
    public ColorRgba Color { get; init; } = ColorRgba.Red;
    public ColorRgba TextColor { get; init; } = ColorRgba.White;

    [JsonIgnore]
    public override RectD Bounds => new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);

    public override Annotation Transform(Affine2 t) =>
        this with { Center = t.Apply(Center), Radius = Math.Clamp(Radius * t.UniformScale, 6, 200) };

    public override bool HitTest(Vec2 p, double tolerance) => p.DistanceTo(Center) <= Radius + tolerance;

    public override IReadOnlyList<Handle> GetHandles() =>
        [new(RectHandles.BottomRight, new Vec2(Center.X + Radius * 0.7071, Center.Y + Radius * 0.7071), HandleKind.Corner)];

    public override Annotation MoveHandle(int handleId, Vec2 to, bool constrain) =>
        this with { Radius = Math.Clamp(to.DistanceTo(Center), 6, 200) };
}

/// <summary>Shows a zoomed copy of <see cref="Source"/> inside <see cref="Dest"/>, joined by connector lines.</summary>
public sealed record MagnifierAnnotation : Annotation
{
    public const int SourceHandle = 8;

    public RectD Source { get; init; }
    public RectD Dest { get; init; }
    public bool Round { get; init; }
    public ColorRgba Border { get; init; } = ColorRgba.Red;
    public double BorderWidth { get; init; } = 3;

    [JsonIgnore]
    public override RectD Bounds => Dest;

    public override Annotation Transform(Affine2 t) => this with { Source = t.Apply(Source), Dest = t.Apply(Dest) };

    public override bool HitTest(Vec2 p, double tolerance) =>
        Dest.Inflate(tolerance).Contains(p) || Source.Inflate(tolerance).Contains(p);

    public override IReadOnlyList<Handle> GetHandles() =>
        [.. RectHandles.Get(Dest), new Handle(SourceHandle, Source.Center, HandleKind.Special)];

    public override Annotation MoveHandle(int handleId, Vec2 to, bool constrain) =>
        handleId == SourceHandle
            ? this with { Source = Source.Offset(to - Source.Center) }
            : this with { Dest = RectHandles.Resize(Dest, handleId, to, constrain) };
}
