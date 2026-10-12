namespace ShotIt.Core.Geometry;

/// <summary>A point in physical pixels. Virtual-screen coordinates may be negative.</summary>
public readonly record struct PixelPoint(int X, int Y)
{
    public static PixelPoint operator +(PixelPoint a, PixelPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static PixelPoint operator -(PixelPoint a, PixelPoint b) => new(a.X - b.X, a.Y - b.Y);
}

public readonly record struct PixelSize(int Width, int Height)
{
    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>
/// An integer rectangle in physical pixels. Right and Bottom are exclusive.
/// All arithmetic is signed: secondary monitors can sit at negative coordinates.
/// </summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public static readonly PixelRect Empty;

    public int Left => X;
    public int Top => Y;
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public PixelPoint Location => new(X, Y);
    public PixelSize Size => new(Width, Height);
    public PixelPoint Center => new(X + Width / 2, Y + Height / 2);

    public static PixelRect FromLTRB(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    /// <summary>Builds a normalized rectangle from two arbitrary corner points.</summary>
    public static PixelRect FromCorners(PixelPoint a, PixelPoint b) =>
        FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    public bool Contains(PixelPoint p) => p.X >= Left && p.X < Right && p.Y >= Top && p.Y < Bottom;

    public bool Contains(PixelRect r) =>
        !r.IsEmpty && r.Left >= Left && r.Top >= Top && r.Right <= Right && r.Bottom <= Bottom;

    public bool IntersectsWith(PixelRect r) =>
        !IsEmpty && !r.IsEmpty && r.Left < Right && r.Right > Left && r.Top < Bottom && r.Bottom > Top;

    public PixelRect Intersect(PixelRect r)
    {
        int l = Math.Max(Left, r.Left), t = Math.Max(Top, r.Top);
        int rr = Math.Min(Right, r.Right), b = Math.Min(Bottom, r.Bottom);
        return rr > l && b > t ? FromLTRB(l, t, rr, b) : Empty;
    }

    public PixelRect Union(PixelRect r)
    {
        if (IsEmpty) return r;
        if (r.IsEmpty) return this;
        return FromLTRB(Math.Min(Left, r.Left), Math.Min(Top, r.Top), Math.Max(Right, r.Right), Math.Max(Bottom, r.Bottom));
    }

    public PixelRect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);

    public PixelRect Inflate(int d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);

    /// <summary>Shifts the rectangle so it lies inside <paramref name="bounds"/>, shrinking only if it is larger.</summary>
    public PixelRect MoveInside(PixelRect bounds)
    {
        int w = Math.Min(Width, bounds.Width), h = Math.Min(Height, bounds.Height);
        int x = Math.Clamp(X, bounds.Left, bounds.Right - w);
        int y = Math.Clamp(Y, bounds.Top, bounds.Bottom - h);
        return new PixelRect(x, y, w, h);
    }

    public RectD ToRectD() => new(X, Y, Width, Height);

    public override string ToString() => $"{X},{Y} {Width}x{Height}";
}

/// <summary>A point or vector in document space (double precision pixels).</summary>
public readonly record struct Vec2(double X, double Y)
{
    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator *(Vec2 a, double k) => new(a.X * k, a.Y * k);
    public static Vec2 operator /(Vec2 a, double k) => new(a.X / k, a.Y / k);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double LengthSquared => X * X + Y * Y;
    public double DistanceTo(Vec2 o) => (this - o).Length;

    public Vec2 Normalized()
    {
        double l = Length;
        return l < 1e-9 ? new Vec2(0, 0) : new Vec2(X / l, Y / l);
    }

    public Vec2 Perpendicular() => new(-Y, X);

    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
}

/// <summary>A normalized rectangle in document space.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Left => X;
    public double Top => Y;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public Vec2 TopLeft => new(X, Y);
    public Vec2 TopRight => new(Right, Y);
    public Vec2 BottomLeft => new(X, Bottom);
    public Vec2 BottomRight => new(Right, Bottom);
    public Vec2 Center => new(X + Width / 2, Y + Height / 2);
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static RectD FromCorners(Vec2 a, Vec2 b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    public static RectD FromPoints(IEnumerable<Vec2> points)
    {
        double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
        bool any = false;
        foreach (var p in points)
        {
            any = true;
            l = Math.Min(l, p.X); t = Math.Min(t, p.Y); r = Math.Max(r, p.X); b = Math.Max(b, p.Y);
        }
        return any ? new RectD(l, t, r - l, b - t) : default;
    }

    public bool Contains(Vec2 p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;

    public RectD Inflate(double d) =>
        new(X - d, Y - d, Math.Max(0, Width + 2 * d), Math.Max(0, Height + 2 * d));

    public RectD Offset(Vec2 d) => new(X + d.X, Y + d.Y, Width, Height);

    public RectD Union(RectD o)
    {
        double l = Math.Min(Left, o.Left), t = Math.Min(Top, o.Top);
        return new RectD(l, t, Math.Max(Right, o.Right) - l, Math.Max(Bottom, o.Bottom) - t);
    }

    public RectD Intersect(RectD o)
    {
        double l = Math.Max(Left, o.Left), t = Math.Max(Top, o.Top);
        double r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r > l && b > t ? new RectD(l, t, r - l, b - t) : default;
    }

    /// <summary>The smallest integer pixel rectangle that covers this rectangle.</summary>
    public PixelRect ToPixelRectOuter()
    {
        int l = (int)Math.Floor(Left), t = (int)Math.Floor(Top);
        int r = (int)Math.Ceiling(Right), b = (int)Math.Ceiling(Bottom);
        return PixelRect.FromLTRB(l, t, r, b);
    }

    /// <summary>Nearest integer pixel rectangle (rounding every edge).</summary>
    public PixelRect ToPixelRectRounded() =>
        PixelRect.FromLTRB((int)Math.Round(Left), (int)Math.Round(Top), (int)Math.Round(Right), (int)Math.Round(Bottom));
}

/// <summary>Axis-aligned affine transform: scale then translate. Enough for move and resize.</summary>
public readonly record struct Affine2(double Sx, double Sy, double Tx, double Ty)
{
    public static readonly Affine2 Identity = new(1, 1, 0, 0);

    public static Affine2 Translation(Vec2 d) => new(1, 1, d.X, d.Y);

    /// <summary>The transform that maps rectangle <paramref name="from"/> onto <paramref name="to"/>.</summary>
    public static Affine2 MapRect(RectD from, RectD to)
    {
        double sx = from.Width > 1e-9 ? to.Width / from.Width : 1;
        double sy = from.Height > 1e-9 ? to.Height / from.Height : 1;
        return new Affine2(sx, sy, to.X - from.X * sx, to.Y - from.Y * sy);
    }

    public Vec2 Apply(Vec2 p) => new(p.X * Sx + Tx, p.Y * Sy + Ty);

    public RectD Apply(RectD r) => RectD.FromCorners(Apply(r.TopLeft), Apply(r.BottomRight));

    /// <summary>Geometric mean of both scale factors; used for sizes that have no direction (radius, font).</summary>
    public double UniformScale => Math.Sqrt(Math.Abs(Sx * Sy));
}

public static class GeometryMath
{
    public static double DistanceToSegment(Vec2 p, Vec2 a, Vec2 b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 < 1e-12) return p.DistanceTo(a);
        double t = Math.Clamp(((p.X - a.X) * ab.X + (p.Y - a.Y) * ab.Y) / len2, 0, 1);
        return p.DistanceTo(new Vec2(a.X + ab.X * t, a.Y + ab.Y * t));
    }

    public static double DistanceToPolyline(Vec2 p, IReadOnlyList<Vec2> points)
    {
        if (points.Count == 0) return double.MaxValue;
        if (points.Count == 1) return p.DistanceTo(points[0]);
        double best = double.MaxValue;
        for (int i = 1; i < points.Count; i++)
            best = Math.Min(best, DistanceToSegment(p, points[i - 1], points[i]));
        return best;
    }

    public static Vec2 QuadraticBezier(Vec2 a, Vec2 c, Vec2 b, double t)
    {
        double u = 1 - t;
        return new Vec2(u * u * a.X + 2 * u * t * c.X + t * t * b.X, u * u * a.Y + 2 * u * t * c.Y + t * t * b.Y);
    }

    public static double DistanceToQuadratic(Vec2 p, Vec2 a, Vec2 c, Vec2 b, int samples = 24)
    {
        double best = double.MaxValue;
        var prev = a;
        for (int i = 1; i <= samples; i++)
        {
            var cur = QuadraticBezier(a, c, b, (double)i / samples);
            best = Math.Min(best, DistanceToSegment(p, prev, cur));
            prev = cur;
        }
        return best;
    }

    /// <summary>Snaps the direction from <paramref name="origin"/> to <paramref name="p"/> to the nearest 45 degree step.</summary>
    public static Vec2 SnapAngle45(Vec2 origin, Vec2 p)
    {
        var d = p - origin;
        double len = d.Length;
        if (len < 1e-9) return p;
        double step = Math.PI / 4;
        double angle = Math.Round(Math.Atan2(d.Y, d.X) / step) * step;
        return new Vec2(origin.X + Math.Cos(angle) * len, origin.Y + Math.Sin(angle) * len);
    }

    /// <summary>Makes the rectangle spanned by anchor and p square, keeping the drag direction.</summary>
    public static Vec2 ConstrainSquare(Vec2 anchor, Vec2 p)
    {
        double dx = p.X - anchor.X, dy = p.Y - anchor.Y;
        double side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new Vec2(anchor.X + (dx < 0 ? -side : side), anchor.Y + (dy < 0 ? -side : side));
    }

    public static bool PointInEllipse(Vec2 p, RectD r)
    {
        if (r.IsEmpty) return false;
        double nx = (p.X - r.Center.X) / (r.Width / 2), ny = (p.Y - r.Center.Y) / (r.Height / 2);
        return nx * nx + ny * ny <= 1;
    }

    /// <summary>Even-odd point-in-polygon test.</summary>
    public static bool PointInPolygon(Vec2 p, IReadOnlyList<Vec2> poly)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i];
            var b = poly[j];
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (b.X - a.X) * (p.Y - a.Y) / (b.Y - a.Y) + a.X)
                inside = !inside;
        }
        return inside;
    }
}
