using System.Runtime.InteropServices;
using ShotIt.Core.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Variant;

namespace ShotIt.Native;

/// <summary>
/// Reads the pictures of a video file one after another, decoded by Windows (Media Foundation) into
/// ordinary pixels. Much faster than asking for stills one at a time, and the colours come out as a
/// player would show them.
/// </summary>
public sealed unsafe class VideoFrameReader : IDisposable
{
    private const uint FirstVideoStream = 0xFFFFFFFC, AllStreams = 0xFFFFFFFE;
    private const uint EndOfStream = 0x2;
    private const uint MediaFoundationVersion = 0x00020070;

    private IMFSourceReader* _reader;
    private bool _started;

    /// <exception cref="InvalidOperationException">The file is not a video Windows can decode.</exception>
    public VideoFrameReader(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Check(PInvoke.MFStartup(MediaFoundationVersion, 0), "start Media Foundation");
        _started = true;

        IMFAttributes* attributes = null;
        IMFMediaType* wanted = null, actual = null;
        try
        {
            Check(PInvoke.MFCreateAttributes(&attributes, 1), "create attributes");
            // Lets the reader convert whatever the decoder produces into RGB, with the colour range handled properly.
            attributes->SetUINT32(PInvoke.MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, 1);

            IMFSourceReader* reader;
            fixed (char* url = path) Check(PInvoke.MFCreateSourceReaderFromURL(url, attributes, &reader), "open the file");
            _reader = reader;

            _reader->SetStreamSelection(AllStreams, false);
            _reader->SetStreamSelection(FirstVideoStream, true);

            Check(PInvoke.MFCreateMediaType(&wanted), "create a media type");
            wanted->SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            wanted->SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_RGB32);
            _reader->SetCurrentMediaType(FirstVideoStream, null, wanted);

            _reader->GetCurrentMediaType(FirstVideoStream, &actual);
            actual->GetUINT64(PInvoke.MF_MT_FRAME_SIZE, out ulong size);
            Width = (int)(size >> 32);
            Height = (int)(size & 0xFFFFFFFF);
            if (Width <= 0 || Height <= 0) throw new InvalidOperationException("The video has no picture.");
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or NotSupportedException)
        {
            Dispose();
            throw new InvalidOperationException("The file is not a video Windows can decode.", ex);
        }
        catch
        {
            Dispose();
            throw;
        }
        finally
        {
            if (attributes is not null) attributes->Release();
            if (wanted is not null) wanted->Release();
            if (actual is not null) actual->Release();
        }
    }

    public int Width { get; }
    public int Height { get; }

    private static void Check(HRESULT result, string what)
    {
        if (result.Failed) throw new InvalidOperationException($"Windows could not {what} (0x{(uint)result.Value:X8}).");
    }

    /// <summary>
    /// Jumps to the last point at or before <paramref name="position"/> from which decoding can start.
    /// The pictures read next may begin a little earlier; their times say exactly where they belong.
    /// </summary>
    public void Seek(TimeSpan position)
    {
        ObjectDisposedException.ThrowIf(_reader is null, this);
        PROPVARIANT where = default;
        where.Anonymous.Anonymous.vt = VARENUM.VT_I8;
        where.Anonymous.Anonymous.Anonymous.hVal = Math.Max(0, position.Ticks);
        _reader->SetCurrentPosition(Guid.Empty, where);
    }

    /// <summary>Reads the next picture and the time it is shown at. Returns false at the end of the video.</summary>
    public bool Read(out PixelBuffer frame, out TimeSpan time)
    {
        ObjectDisposedException.ThrowIf(_reader is null, this);
        while (true)
        {
            uint flags;
            long timestamp;
            IMFSample* sample;
            _reader->ReadSample(FirstVideoStream, 0, null, &flags, &timestamp, &sample);
            if ((flags & EndOfStream) != 0)
            {
                if (sample is not null) sample->Release();
                frame = null!;
                time = default;
                return false;
            }

            // Gaps in the stream come through as empty reads.
            if (sample is null) continue;
            try
            {
                frame = Copy(sample);
                time = TimeSpan.FromTicks(timestamp);
                return true;
            }
            finally
            {
                sample->Release();
            }
        }
    }

    private PixelBuffer Copy(IMFSample* sample)
    {
        IMFMediaBuffer* buffer;
        sample->ConvertToContiguousBuffer(&buffer);
        try
        {
            var frame = new PixelBuffer(Width, Height);
            int rowBytes = Width * 4;
            // The two-dimensional view of the buffer knows where the top row is and which way the rows run.
            Guid twoDimensional = IMF2DBuffer.IID_Guid;
            void* raw;
            if (buffer->QueryInterface(&twoDimensional, &raw).Succeeded)
            {
                var rows = (IMF2DBuffer*)raw;
                try
                {
                    byte* top;
                    int pitch;
                    rows->Lock2D(&top, &pitch);
                    try
                    {
                        for (int y = 0; y < Height; y++)
                            new ReadOnlySpan<byte>(top + (long)y * pitch, rowBytes).CopyTo(frame.Row(y));
                    }
                    finally
                    {
                        rows->Unlock2D();
                    }
                }
                finally
                {
                    rows->Release();
                }
            }
            else
            {
                byte* data;
                uint length;
                buffer->Lock(&data, null, &length);
                try
                {
                    if (length < (uint)(rowBytes * Height)) throw new InvalidOperationException("Windows returned an incomplete picture.");
                    // Without the two-dimensional view, RGB pictures are stored bottom row first.
                    for (int y = 0; y < Height; y++)
                        new ReadOnlySpan<byte>(data + (long)(Height - 1 - y) * rowBytes, rowBytes).CopyTo(frame.Row(y));
                }
                finally
                {
                    buffer->Unlock();
                }
            }

            frame.MakeOpaque();
            return frame;
        }
        finally
        {
            buffer->Release();
        }
    }

    public void Dispose()
    {
        if (_reader is not null)
        {
            _reader->Release();
            _reader = null;
        }

        if (_started)
        {
            _started = false;
            PInvoke.MFShutdown();
        }

        GC.SuppressFinalize(this);
    }

    ~VideoFrameReader() => Dispose();
}
