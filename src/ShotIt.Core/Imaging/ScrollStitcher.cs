using System.Runtime.InteropServices;

namespace ShotIt.Core.Imaging;

/// <summary>
/// One picture of a scrolling area together with a compact description of every row, so two pictures
/// can be compared thousands of times without touching the pixels again.
/// </summary>
public sealed class ScrollFrame
{
    private const int MaxStrips = 8;
    private readonly int[] _sums;
    private readonly bool[] _plain;

    public ScrollFrame(PixelBuffer image)
    {
        Image = image ?? throw new ArgumentNullException(nameof(image));
        int width = image.Width, height = image.Height;
        // Each row is described strip by strip, so a scroll bar or a hover effect spoils one strip, not the row.
        Strips = Math.Clamp(width / 16, 1, MaxStrips);
        StripWidth = width / Strips;
        Tolerance = StripWidth * 2;
        _sums = new int[height * Strips];
        _plain = new bool[height * Strips];

        var pixels = MemoryMarshal.Cast<byte, uint>(image.Data.AsSpan(0, width * height * 4));
        for (int y = 0; y < height; y++)
        {
            for (int s = 0; s < Strips; s++)
            {
                var strip = pixels.Slice(y * width + s * StripWidth, StripWidth);
                uint first = strip[0] & 0x00FFFFFF;
                bool plain = true;
                int sum = 0;
                foreach (uint raw in strip)
                {
                    uint p = raw & 0x00FFFFFF;
                    if (p != first) plain = false;
                    sum += (int)(p & 0xFF) + (int)((p >> 8) & 0xFF) + (int)(p >> 16);
                }

                _sums[y * Strips + s] = sum;
                _plain[y * Strips + s] = plain;
            }
        }
    }

    public PixelBuffer Image { get; }
    public int Width => Image.Width;
    public int Height => Image.Height;
    internal int Strips { get; }
    internal int StripWidth { get; }

    /// <summary>How far two strips may differ and still count as the same (rendering noise, a blinking caret).</summary>
    internal int Tolerance { get; }

    internal bool Same(int row, int strip, ScrollFrame other, int otherRow) =>
        Math.Abs(_sums[row * Strips + strip] - other._sums[otherRow * Strips + strip]) <= Tolerance;

    internal bool IsPlain(int row, int strip) => _plain[row * Strips + strip];
}

/// <summary>
/// How a picture relates to the one taken before it: the content moved up by <see cref="Offset"/> rows,
/// while <see cref="Top"/> rows at the top and <see cref="Bottom"/> rows at the bottom stayed where they were
/// (a sticky header or footer).
/// </summary>
public readonly record struct ScrollMatch(int Offset, int Top, int Bottom);

public static class ScrollMatcher
{
    /// <summary>The share of comparable strips that must agree before an offset is believed.</summary>
    private const double Threshold = 0.8;

    /// <summary>
    /// Works out how far the content scrolled between two pictures of the same area. Returns an offset of
    /// zero when nothing moved, and null when the two pictures cannot be related at all.
    /// </summary>
    /// <param name="hint">The offset of the previous step; it wins when the content repeats and several offsets fit.</param>
    public static ScrollMatch? Match(ScrollFrame previous, ScrollFrame next, int hint = 0)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(next);
        if (previous.Width != next.Width || previous.Height != next.Height)
            throw new ArgumentException("Both pictures must have the same size.", nameof(next));

        int height = next.Height, strips = next.Strips;

        // A strip that looks the same in both pictures (a side bar beside the scrolling part) says nothing about movement.
        Span<bool> active = stackalloc bool[strips];
        int activeCount = 0;
        for (int s = 0; s < strips; s++)
        {
            int changed = 0;
            for (int y = 0; y < height; y++)
                if (!next.Same(y, s, previous, y)) changed++;
            active[s] = changed > height / 50;
            if (active[s]) activeCount++;
        }

        if (activeCount == 0) return new ScrollMatch(0, 0, 0);

        // One strip may disagree: that is where the scroll bar thumb moves.
        int allowance = activeCount >= 4 ? 1 : 0;
        var rowSame = new bool[height];
        int changedRows = 0;
        for (int y = 0; y < height; y++)
        {
            int differing = 0;
            for (int s = 0; s < strips; s++)
                if (active[s] && !next.Same(y, s, previous, y)) differing++;
            rowSame[y] = differing <= allowance;
            if (!rowSame[y]) changedRows++;
        }

        if (changedRows <= height / 50) return new ScrollMatch(0, 0, 0);

        int top = 0;
        while (top < height && rowSame[top]) top++;
        int bottom = 0;
        while (bottom < height - top && rowSame[height - 1 - bottom]) bottom++;
        int body = height - top - bottom;
        int minOverlap = Math.Max(8, body / 10);
        if (body <= minOverlap) return null;

        int maxOffset = body - minOverlap;
        var scores = new double[maxOffset + 1];
        double best = 0;
        for (int d = 1; d <= maxOffset; d++)
        {
            int informative = 0, matched = 0;
            for (int y = top; y < height - bottom - d; y++)
            {
                for (int s = 0; s < strips; s++)
                {
                    if (!active[s]) continue;
                    bool same = next.Same(y, s, previous, y + d);
                    // Two blank strips of the same colour fit at every offset, so they prove nothing.
                    if (same && next.IsPlain(y, s) && previous.IsPlain(y + d, s)) continue;
                    informative++;
                    if (same) matched++;
                }
            }

            scores[d] = informative >= activeCount * 6 ? (double)matched / informative : 0;
            if (scores[d] > best) best = scores[d];
        }

        if (best < Threshold) return null;
        if (hint >= 1 && hint <= maxOffset && scores[hint] >= Threshold && scores[hint] >= best - 0.05)
            return new ScrollMatch(hint, top, bottom);

        // Among offsets that fit equally well, the smallest has the largest overlap behind it.
        for (int d = 1; d <= maxOffset; d++)
            if (scores[d] >= best - 0.01) return new ScrollMatch(d, top, bottom);
        return null;
    }
}

public enum ScrollAddResult
{
    /// <summary>The picture continued the page and was joined on.</summary>
    Added,

    /// <summary>The picture shows the same thing as the one before: the end of the page.</summary>
    NoMovement,

    /// <summary>The picture could not be related to the one before.</summary>
    NoMatch,

    /// <summary>The picture was joined on and the page has now reached the height limit.</summary>
    Full,
}

/// <summary>Joins pictures of a scrolling area, taken one scroll step apart, into one tall picture.</summary>
public sealed class ScrollStitcher
{
    private readonly int _maxHeight;
    private byte[] _canvas = [];
    private ScrollFrame? _last;
    private int _width;
    private int _position;

    public ScrollStitcher(int maxHeight)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);
        _maxHeight = maxHeight;
    }

    /// <summary>Height of the joined picture so far.</summary>
    public int Height { get; private set; }

    public int FrameCount { get; private set; }

    /// <summary>How far the most recently joined picture had scrolled.</summary>
    public int LastOffset { get; private set; }

    public ScrollAddResult Add(ScrollFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_last is null)
        {
            _width = frame.Width;
            _last = frame;
            FrameCount = 1;
            return Write(frame, fromRow: 0);
        }

        if (frame.Width != _last.Width || frame.Height != _last.Height)
            throw new ArgumentException("Every picture must have the same size.", nameof(frame));
        if (ScrollMatcher.Match(_last, frame, LastOffset) is not { } match) return ScrollAddResult.NoMatch;
        if (match.Offset == 0) return ScrollAddResult.NoMovement;

        _position += match.Offset;
        LastOffset = match.Offset;
        _last = frame;
        FrameCount++;
        // Everything below a sticky header is written, footer included: the next picture overwrites the
        // footer with page content, so only the last picture's footer survives, at the very bottom.
        return Write(frame, match.Top);
    }

    private ScrollAddResult Write(ScrollFrame frame, int fromRow)
    {
        int stride = _width * 4;
        int end = Math.Min(_position + frame.Height, _maxHeight);
        if ((long)end * stride > _canvas.Length)
        {
            long wanted = Math.Max((long)end * stride, Math.Min(_canvas.Length * 2L, (long)_maxHeight * stride));
            Array.Resize(ref _canvas, checked((int)wanted));
        }

        for (int y = fromRow; y < frame.Height && _position + y < end; y++)
            frame.Image.Row(y).CopyTo(_canvas.AsSpan((_position + y) * stride, stride));

        Height = Math.Max(Height, end);
        return _position + frame.Height >= _maxHeight ? ScrollAddResult.Full : ScrollAddResult.Added;
    }

    public PixelBuffer Build()
    {
        if (_last is null) throw new InvalidOperationException("No picture has been added.");
        return new PixelBuffer(_width, Height, _canvas.AsSpan(0, _width * Height * 4).ToArray());
    }
}
