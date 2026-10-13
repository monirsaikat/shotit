using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using ShotIt.Core.Imaging;
using ShotIt.Core.Settings;
using ShotIt.Rendering;

namespace ShotIt.App.Infrastructure;

/// <summary>
/// Clipboard access that never throws at the caller. The clipboard is a shared resource: another
/// program may hold it open, so every operation retries briefly and then reports failure.
/// </summary>
public static class ClipboardService
{
    private const int Attempts = 5;
    private const int DelayMs = 50;

    private static bool Retry(Action action)
    {
        for (int i = 0; i < Attempts; i++)
        {
            try
            {
                action();
                return true;
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                // CLIPBRD_E_CANT_OPEN: someone else has the clipboard right now.
                Thread.Sleep(DelayMs);
            }
        }

        return false;
    }

    /// <summary>
    /// Puts the image on the clipboard as a bitmap (for every program), as PNG (keeps transparency)
    /// and optionally as a file (pastes into Explorer and e-mail attachments).
    /// </summary>
    public static bool SetImage(PixelBuffer image, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(image);
        var data = new DataObject();
        // The plain bitmap format has no alpha; flatten onto white so transparent areas do not turn black.
        data.SetImage(ImageCodec.Flatten(image, ColorRgba.White).ToBitmapSource());
        data.SetData("PNG", new MemoryStream(ImageCodec.Encode(image, ImageFormat.Png)), autoConvert: false);
        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
            data.SetFileDropList(new StringCollection { filePath });

        // copy: true flushes the data so it survives after ShotIt exits.
        return Retry(() => Clipboard.SetDataObject(data, copy: true));
    }

    /// <summary>Puts a file on the clipboard, ready to paste into a folder, a chat or an e-mail.</summary>
    public static bool SetFile(string path)
    {
        var data = new DataObject();
        data.SetFileDropList(new StringCollection { path });
        return Retry(() => Clipboard.SetDataObject(data, copy: true));
    }

    public static bool SetText(string text) =>
        Retry(() => Clipboard.SetDataObject(text ?? string.Empty, copy: true));

    /// <summary>Reads an image from the clipboard: a PNG stream, a bitmap, or the first image file in a file list.</summary>
    public static PixelBuffer? GetImage()
    {
        PixelBuffer? result = null;
        Retry(() =>
        {
            var data = Clipboard.GetDataObject();
            if (data is null) return;

            if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
            {
                result = TryDecode(png.ToArray());
                if (result is not null) return;
            }

            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (string file in files)
                {
                    result = TryDecodeFile(file);
                    if (result is not null) return;
                }
            }

            if (Clipboard.ContainsImage() && Clipboard.GetImage() is BitmapSource bitmap)
            {
                result = bitmap.ToPixelBuffer();
                result.MakeOpaque(); // CF_BITMAP carries no meaningful alpha
            }
        });
        return result;
    }

    private static PixelBuffer? TryDecode(byte[] bytes)
    {
        try { return ImageCodec.Decode(bytes); }
        catch (NotSupportedException) { return null; }
    }

    private static PixelBuffer? TryDecodeFile(string path)
    {
        try { return File.Exists(path) ? ImageCodec.DecodeFile(path) : null; }
        catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException) { return null; }
    }
}
