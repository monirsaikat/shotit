using System.Runtime.InteropServices;
using ShotIt.Core.Geometry;
using ShotIt.Core.Imaging;

namespace ShotIt.Core.Export;

/// <summary>
/// Writes an animated GIF one frame at a time. Each frame stores only the rectangle that changed since
/// the one before, with its own palette of up to 255 colours, so recordings of a mostly still screen stay small.
/// </summary>
public sealed class GifWriter
{
    private const int Transparent = 255;
    private readonly Stream _output;
    private readonly int _width;
    private readonly int _height;
    private PixelBuffer? _previous;
    private byte[]? _pending;
    private TimeSpan _pendingTime;
    private bool _finished;

    public GifWriter(Stream output, int width, int height)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, ushort.MaxValue);
        _width = width;
        _height = height;

        Span<byte> header =
        [
            (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a',
            (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8),
            0x70, 0, 0, // no global palette; every frame brings its own
            // Application extension asking players to repeat the animation for ever.
            0x21, 0xFF, 0x0B, (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0',
            0x03, 0x01, 0x00, 0x00, 0x00,
        ];
        _output.Write(header);
    }

    /// <summary>Frames written so far, not counting ones that showed nothing new.</summary>
    public int FrameCount { get; private set; }

    /// <summary>Adds what the picture looks like at <paramref name="time"/> since the start. A frame that shows nothing new costs nothing.</summary>
    public void AddFrame(PixelBuffer frame, TimeSpan time)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_finished, this);
        if (frame.Width != _width || frame.Height != _height)
            throw new ArgumentException("Every frame must have the size the animation was created with.", nameof(frame));

        var area = _previous is null ? frame.Bounds : ChangedArea(_previous, frame);
        if (area.IsEmpty) return;

        FlushPending(time);
        _pending = Encode(frame, _previous, area);
        _pendingTime = time;
        _previous = frame;
        FrameCount++;
    }

    /// <summary>Writes the last frame, shown until <paramref name="end"/>, and closes the animation. The stream is left open.</summary>
    public void Finish(TimeSpan end)
    {
        if (_finished) return;
        FlushPending(end);
        _finished = true;
        _output.WriteByte(0x3B);
        _output.Flush();
    }

    private void FlushPending(TimeSpan until)
    {
        if (_pending is null) return;
        // GIF counts in hundredths of a second, and players treat anything under two as "as fast as you like".
        int delay = Math.Clamp((int)Math.Round((until - _pendingTime).TotalMilliseconds / 10), 2, ushort.MaxValue);
        _pending[4] = (byte)delay;
        _pending[5] = (byte)(delay >> 8);
        _output.Write(_pending);
        _pending = null;
    }

    private static PixelRect ChangedArea(PixelBuffer before, PixelBuffer after)
    {
        int width = after.Width, height = after.Height;
        var a = MemoryMarshal.Cast<byte, uint>(before.Data.AsSpan(0, width * height * 4));
        var b = MemoryMarshal.Cast<byte, uint>(after.Data.AsSpan(0, width * height * 4));
        int top = 0;
        while (top < height && a.Slice(top * width, width).SequenceEqual(b.Slice(top * width, width))) top++;
        if (top == height) return PixelRect.Empty;
        int bottom = height - 1;
        while (a.Slice(bottom * width, width).SequenceEqual(b.Slice(bottom * width, width))) bottom--;

        int left = width, right = -1;
        for (int y = top; y <= bottom; y++)
        {
            var rowA = a.Slice(y * width, width);
            var rowB = b.Slice(y * width, width);
            int first = rowA.CommonPrefixLength(rowB);
            if (first == width) continue;
            if (first < left) left = first;
            int last = width - 1;
            while (rowA[last] == rowB[last]) last--;
            if (last > right) right = last;
        }

        return PixelRect.FromLTRB(left, top, right + 1, bottom + 1);
    }

    /// <summary>Builds the graphic control block, image descriptor, palette and compressed pixels for one frame.</summary>
    private static byte[] Encode(PixelBuffer frame, PixelBuffer? previous, PixelRect area)
    {
        int count = area.Width * area.Height;
        var indices = new byte[count];
        var palette = new byte[256 * 3];
        Quantize(frame, previous, area, indices, palette);

        using var block = new MemoryStream(count / 2 + 1024);
        // Leave the frame in place afterwards, so the next one only has to paint what changed.
        block.Write([0x21, 0xF9, 0x04, (byte)(previous is null ? 0x04 : 0x05), 0, 0, Transparent, 0]);
        block.Write(
        [
            0x2C,
            (byte)area.X, (byte)(area.X >> 8), (byte)area.Y, (byte)(area.Y >> 8),
            (byte)area.Width, (byte)(area.Width >> 8), (byte)area.Height, (byte)(area.Height >> 8),
            0x87, // local palette of 256 colours
        ]);
        block.Write(palette);
        Lzw.Compress(indices, block);
        return block.ToArray();
    }

    // ------------------------------------------------------------------ colour reduction

    private struct Bin
    {
        public int Count;
        public long R, G, B;
        public short Key;
    }

    /// <summary>
    /// Reduces the area to at most 255 colours by median cut over a 15-bit histogram. Pixels that did not
    /// change since the previous frame become transparent and take no part in choosing the colours.
    /// </summary>
    private static void Quantize(PixelBuffer frame, PixelBuffer? previous, PixelRect area, byte[] indices, byte[] palette)
    {
        var histogram = new int[32768];
        var sums = new long[32768 * 3];
        var pixels = MemoryMarshal.Cast<byte, uint>(frame.Data.AsSpan(0, frame.Width * frame.Height * 4));
        var old = previous is null ? default : MemoryMarshal.Cast<byte, uint>(previous.Data.AsSpan(0, frame.Width * frame.Height * 4));

        for (int y = 0; y < area.Height; y++)
        {
            int row = (area.Y + y) * frame.Width + area.X;
            for (int x = 0; x < area.Width; x++)
            {
                uint p = pixels[row + x];
                if (previous is not null && p == old[row + x]) continue;
                int b = (int)(p & 0xFF), g = (int)((p >> 8) & 0xFF), r = (int)((p >> 16) & 0xFF);
                int key = (r >> 3 << 10) | (g >> 3 << 5) | (b >> 3);
                histogram[key]++;
                sums[key * 3] += r;
                sums[key * 3 + 1] += g;
                sums[key * 3 + 2] += b;
            }
        }

        var bins = new List<Bin>();
        for (int key = 0; key < histogram.Length; key++)
            if (histogram[key] > 0)
                bins.Add(new Bin { Count = histogram[key], R = sums[key * 3], G = sums[key * 3 + 1], B = sums[key * 3 + 2], Key = (short)key });

        var all = CollectionsMarshal.AsSpan(bins);
        var boxes = new List<(int Start, int Length)> { (0, all.Length) };
        while (boxes.Count < Transparent)
        {
            // Split the box holding the most pixels that still contains more than one colour.
            int pick = -1;
            long most = -1;
            for (int i = 0; i < boxes.Count; i++)
            {
                if (boxes[i].Length < 2) continue;
                long weight = 0;
                foreach (ref readonly var bin in all.Slice(boxes[i].Start, boxes[i].Length)) weight += bin.Count;
                if (weight > most)
                {
                    most = weight;
                    pick = i;
                }
            }

            if (pick < 0) break;
            var (start, length) = boxes[pick];
            var box = all.Slice(start, length);
            int minR = 31, maxR = 0, minG = 31, maxG = 0, minB = 31, maxB = 0;
            foreach (ref readonly var bin in box)
            {
                int r = bin.Key >> 10, g = (bin.Key >> 5) & 31, b = bin.Key & 31;
                minR = Math.Min(minR, r); maxR = Math.Max(maxR, r);
                minG = Math.Min(minG, g); maxG = Math.Max(maxG, g);
                minB = Math.Min(minB, b); maxB = Math.Max(maxB, b);
            }

            int shift = maxG - minG >= maxR - minR && maxG - minG >= maxB - minB ? 5 : maxR - minR >= maxB - minB ? 10 : 0;
            box.Sort((x, y) => ((x.Key >> shift) & 31).CompareTo((y.Key >> shift) & 31));

            long half = most / 2, seen = 0;
            int cut = 1;
            for (int i = 0; i < length - 1; i++)
            {
                seen += box[i].Count;
                cut = i + 1;
                if (seen >= half) break;
            }

            boxes[pick] = (start, cut);
            boxes.Add((start + cut, length - cut));
        }

        var lookup = new byte[32768];
        for (int i = 0; i < boxes.Count; i++)
        {
            long count = 0, r = 0, g = 0, b = 0;
            foreach (ref readonly var bin in all.Slice(boxes[i].Start, boxes[i].Length))
            {
                count += bin.Count;
                r += bin.R;
                g += bin.G;
                b += bin.B;
                lookup[bin.Key] = (byte)i;
            }

            if (count == 0) continue;
            palette[i * 3] = (byte)((r + count / 2) / count);
            palette[i * 3 + 1] = (byte)((g + count / 2) / count);
            palette[i * 3 + 2] = (byte)((b + count / 2) / count);
        }

        for (int y = 0; y < area.Height; y++)
        {
            int row = (area.Y + y) * frame.Width + area.X;
            for (int x = 0; x < area.Width; x++)
            {
                uint p = pixels[row + x];
                indices[y * area.Width + x] = previous is not null && p == old[row + x]
                    ? (byte)Transparent
                    : lookup[(int)(((p >> 16) & 0xFF) >> 3 << 10 | ((p >> 8) & 0xFF) >> 3 << 5 | (p & 0xFF) >> 3)];
            }
        }
    }

    // ------------------------------------------------------------------ compression

    private static class Lzw
    {
        private const int MinCodeSize = 8, ClearCode = 1 << MinCodeSize, EndCode = ClearCode + 1, MaxCodes = 4096;
        private const int TableSize = 1 << 14;

        /// <summary>Writes the minimum code size followed by the compressed pixels as GIF data sub-blocks.</summary>
        public static void Compress(ReadOnlySpan<byte> pixels, Stream output)
        {
            output.WriteByte(MinCodeSize);
            var keys = new int[TableSize];
            var codes = new short[TableSize];
            Array.Fill(keys, -1);

            Span<byte> chunk = stackalloc byte[256];
            int chunkLength = 0, bits = 0, bitCount = 0;
            int codeSize = MinCodeSize + 1, next = EndCode + 1;

            void Emit(int code, Span<byte> chunk)
            {
                bits |= code << bitCount;
                bitCount += codeSize;
                while (bitCount >= 8)
                {
                    chunk[1 + chunkLength++] = (byte)bits;
                    bits >>= 8;
                    bitCount -= 8;
                    if (chunkLength == 255)
                    {
                        chunk[0] = 255;
                        output.Write(chunk[..256]);
                        chunkLength = 0;
                    }
                }
            }

            Emit(ClearCode, chunk);
            int prefix = pixels[0];
            for (int i = 1; i < pixels.Length; i++)
            {
                int value = pixels[i];
                int key = (prefix << 8) | value;
                int slot = (int)(unchecked((uint)key * 0x9E3779B1u) >> 18);
                while (keys[slot] != -1 && keys[slot] != key) slot = (slot + 1) & (TableSize - 1);
                if (keys[slot] == key)
                {
                    prefix = codes[slot];
                    continue;
                }

                Emit(prefix, chunk);
                prefix = value;
                if (next < MaxCodes)
                {
                    // The decoder learns a code one step later, so the width grows only once the new code needs it.
                    if (next == 1 << codeSize) codeSize++;
                    keys[slot] = key;
                    codes[slot] = (short)next++;
                }
                else
                {
                    Emit(ClearCode, chunk);
                    Array.Fill(keys, -1);
                    codeSize = MinCodeSize + 1;
                    next = EndCode + 1;
                }
            }

            Emit(prefix, chunk);
            if (next == 1 << codeSize && codeSize < 12) codeSize++;
            Emit(EndCode, chunk);
            if (bitCount > 0) chunk[1 + chunkLength++] = (byte)bits;
            if (chunkLength > 0)
            {
                chunk[0] = (byte)chunkLength;
                output.Write(chunk[..(chunkLength + 1)]);
            }

            output.WriteByte(0);
        }
    }
}
