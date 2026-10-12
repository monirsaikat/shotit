using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using ShotIt.App.Infrastructure;
using ShotIt.App.Overlay;
using ShotIt.App.Views;
using ShotIt.Core.Imaging;
using ShotIt.Core.Settings;
using ShotIt.Core.Upload;
using ShotIt.Native;
using ShotIt.Ocr;
using ShotIt.Rendering;
using ShotIt.Upload;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace ShotIt.App.Services;

/// <summary>
/// The actions that reach beyond the clipboard and the disk: upload, text recognition, code scanning,
/// image search, the Windows share sheet and printing. Each is registered with <see cref="CaptureActions"/>.
/// </summary>
public sealed class FeatureActions
{
    private readonly AppServices _app;
    private DataTransferManager? _shareManager;
    private string? _sharePath;
    private string? _shareTitle;

    public FeatureActions(AppServices app)
    {
        _app = app;
        var handlers = app.Actions.Handlers;
        handlers[OverlayAction.Upload] = UploadAsync;
        handlers[OverlayAction.Ocr] = OcrAsync;
        handlers[OverlayAction.Qr] = QrAsync;
        handlers[OverlayAction.SearchImage] = SearchImageAsync;
        handlers[OverlayAction.Share] = ShareAsync;
        handlers[OverlayAction.Print] = PrintAsync;
    }

    /// <summary>Tests replace this to answer confirmation prompts without a dialog.</summary>
    public Func<string, string, Window?, bool> Confirm { get; set; } = (title, text, owner) =>
        (owner is not null
            ? MessageBox.Show(owner, text, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK)
            : MessageBox.Show(text, title, MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK)) == MessageBoxResult.OK;

    // ------------------------------------------------------------------ upload

    private UploadRequest BuildRequest(CaptureResult result)
    {
        // PDF and BMP make poor web images; everything else goes out in the user's chosen format.
        var format = _app.Settings.Output.Format is ImageFormat.Jpeg ? ImageFormat.Jpeg : ImageFormat.Png;
        byte[] data = ImageCodec.Encode(result.Image, format, _app.Settings.Output.JpegQuality);
        return new UploadRequest(data, "screenshot" + ImageCodec.Extension(format), ImageCodec.ContentType(format));
    }

    /// <summary>Uploads and returns the link, or null when the user declined or the upload failed (already reported).</summary>
    public async Task<UploadResult?> UploadCoreAsync(CaptureResult result, Window? owner, string purpose)
    {
        var settings = _app.Settings.Upload;
        if (_app.Policies.DisableUpload)
        {
            _app.Notifier.Warn("Uploads are turned off", "Your organisation does not allow uploading screenshots from this computer.");
            return null;
        }

        IUploader uploader;
        try
        {
            uploader = UploaderFactory.Create(settings, _app.Secrets);
        }
        catch (UploadException ex)
        {
            _app.Notifier.Warn("Upload is not set up", ex.Message, () => WindowsHost.OpenSettings(_app, "Upload"));
            return null;
        }

        if (settings.ConfirmBeforeUpload &&
            !Confirm("Upload screenshot", $"{purpose}\n\nThe image will be sent to {uploader.Name}. Anyone who has the link will be able to see it.", owner))
            return null;

        _app.Output.EnsureInLibrary(result);
        var request = BuildRequest(result);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            _app.Log.Information("Uploading {Bytes} bytes to {Uploader}", request.Data.Length, uploader.Name);
            var uploaded = await Task.Run(() => uploader.UploadAsync(request, null, timeout.Token), timeout.Token);
            _app.Output.RecordUpload(result, uploaded.Url, uploaded.DeleteUrl);
            return uploaded;
        }
        catch (UploadException ex)
        {
            // The message never contains credentials: uploaders only quote the server's reply.
            _app.Log.Warning("Upload to {Uploader} failed: {Message}", uploader.Name, ex.Message);
            _app.Notifier.Warn("Upload failed", ex.Message, () => WindowsHost.OpenSettings(_app, "Upload"));
            return null;
        }
        catch (OperationCanceledException)
        {
            _app.Notifier.Warn("Upload failed", $"{uploader.Name} did not answer in time.");
            return null;
        }
    }

    private async Task UploadAsync(CaptureResult result, Window? owner)
    {
        var uploaded = await UploadCoreAsync(result, owner, "Upload this screenshot and get a link?");
        if (uploaded is null) return;
        var settings = _app.Settings.Upload;
        bool isWeb = Uri.TryCreate(uploaded.Url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        bool copied = settings.CopyUrlAfterUpload && ClipboardService.SetText(uploaded.Url);
        if (settings.OpenUrlAfterUpload && isWeb) CaptureActions.OpenUrl(uploaded.Url);
        _app.Notifier.Info(copied ? "Link copied" : "Uploaded", uploaded.Url, () =>
        {
            if (isWeb) CaptureActions.OpenUrl(uploaded.Url);
            else CaptureActions.RevealInExplorer(uploaded.Url);
        });
    }

    private async Task SearchImageAsync(CaptureResult result, Window? owner)
    {
        var uploaded = await UploadCoreAsync(result, owner, "Search the web for this image? A search engine can only look at a picture that is online, so it is uploaded first.");
        if (uploaded is null) return;
        if (!Uri.TryCreate(uploaded.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            _app.Notifier.Warn("Cannot search for this image", "The upload destination does not give a public https link. Choose a different one in Settings › Upload.");
            return;
        }

        CaptureActions.OpenUrl("https://lens.google.com/uploadbyurl?url=" + Uri.EscapeDataString(uploaded.Url));
    }

    // ------------------------------------------------------------------ text and codes

    private async Task OcrAsync(CaptureResult result, Window? owner)
    {
        RecognizedText text;
        try
        {
            var image = result.Image;
            string language = _app.Settings.General.OcrLanguage;
            text = await Task.Run(() => TextRecognizer.RecognizeAsync(image, language));
        }
        catch (OcrUnavailableException ex)
        {
            _app.Log.Warning(ex, "Text recognition unavailable");
            _app.Notifier.Warn("Text recognition is unavailable", ex.Message);
            return;
        }

        if (text.IsEmpty)
        {
            _app.Notifier.Info("No text found", "ShotIt could not find any text in the selected area.");
            return;
        }

        _app.Output.RecordOcrText(result, text.Text);
        bool copied = ClipboardService.SetText(text.Text);
        _app.ShowWindow(new TextResultWindow("Recognised text", text.Text,
            copied ? $"Copied to the clipboard · {text.Lines.Count} line(s) · {text.Language}" : $"{text.Lines.Count} line(s) · {text.Language}"), owner);
    }

    private async Task QrAsync(CaptureResult result, Window? owner)
    {
        var image = result.Image;
        var codes = await Task.Run(() => CodeScanner.Scan(image));
        if (codes.Count == 0)
        {
            _app.Notifier.Info("No code found", "ShotIt could not find a QR code or barcode in the selected area.");
            return;
        }

        string content = string.Join(Environment.NewLine + Environment.NewLine, codes.Select(c => c.Text));
        bool copied = ClipboardService.SetText(content);
        string kinds = string.Join(", ", codes.Select(c => c.Format).Distinct());
        string? link = codes.Count == 1 && codes[0].IsWebLink ? codes[0].Text.Trim() : null;
        _app.ShowWindow(new TextResultWindow("Scanned code", content, (copied ? "Copied to the clipboard · " : string.Empty) + kinds, link), owner);
    }

    // ------------------------------------------------------------------ share

    private string? FileFor(CaptureResult result)
    {
        if (result.SavedPath is { } saved && File.Exists(saved)) return saved;
        if (_app.Output.EnsureInLibrary(result) is { } library && File.Exists(library)) return library;
        try
        {
            // History is off: use a temporary copy that the next start-up sweep removes.
            string folder = Path.Combine(Path.GetTempPath(), "ShotIt", "share");
            Directory.CreateDirectory(folder);
            foreach (string old in Directory.GetFiles(folder))
            {
                try { if (File.GetLastWriteTimeUtc(old) < DateTime.UtcNow.AddHours(-1)) File.Delete(old); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            string path = Path.Combine(folder, "ShotIt " + result.CapturedAt.ToString("yyyy-MM-dd HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture) + ".png");
            File.WriteAllBytes(path, ImageCodec.Encode(result.Image, ImageFormat.Png));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private Task ShareAsync(CaptureResult result, Window? owner)
    {
        string? path = FileFor(result);
        if (path is null)
        {
            _app.Notifier.Warn("Could not share", "The image could not be written to disk.");
            return Task.CompletedTask;
        }

        try
        {
            nint hwnd = owner is not null ? new WindowInteropHelper(owner).Handle : _app.MessageWindow.Handle;
            if (owner is null) WindowInterop.ForceForeground(hwnd);
            if (_shareManager is not null) _shareManager.DataRequested -= OnShareRequested;
            _sharePath = path;
            _shareTitle = string.IsNullOrWhiteSpace(result.SourceTitle) ? "Screenshot" : result.SourceTitle;
            _shareManager = DataTransferManagerInterop.GetForWindow(hwnd);
            _shareManager.DataRequested += OnShareRequested;
            DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
        }
        catch (Exception ex)
        {
            // The share sheet is missing on some editions (Server, LTSC without the shell experience).
            _app.Log.Warning(ex, "The Windows share sheet could not be opened");
            _app.Notifier.Warn("Sharing is unavailable", "Windows could not open the share window. The file is shown in Explorer instead.");
            CaptureActions.RevealInExplorer(path);
        }

        return Task.CompletedTask;
    }

    private async void OnShareRequested(DataTransferManager sender, DataRequestedEventArgs args)
    {
        var deferral = args.Request.GetDeferral();
        try
        {
            if (_sharePath is null) return;
            var file = await StorageFile.GetFileFromPathAsync(_sharePath);
            args.Request.Data.Properties.Title = _shareTitle ?? "Screenshot";
            args.Request.Data.Properties.Description = "Shared from ShotIt";
            args.Request.Data.SetStorageItems([file]);
            args.Request.Data.SetBitmap(global::Windows.Storage.Streams.RandomAccessStreamReference.CreateFromFile(file));
        }
        catch (Exception ex)
        {
            args.Request.FailWithDisplayText("The screenshot could not be prepared for sharing.");
            _app.Log.Warning(ex, "Preparing the share failed");
        }
        finally
        {
            deferral.Complete();
        }
    }

    // ------------------------------------------------------------------ print

    private Task PrintAsync(CaptureResult result, Window? owner)
    {
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return Task.CompletedTask;
        try
        {
            dialog.PrintVisual(BuildPrintVisual(result.Image, dialog.PrintableAreaWidth, dialog.PrintableAreaHeight), "ShotIt screenshot");
        }
        catch (Exception ex) when (ex is System.Printing.PrintSystemException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            _app.Log.Warning(ex, "Printing failed");
            _app.Notifier.Warn("Could not print", "The printer did not accept the job. " + ex.Message);
        }

        return Task.CompletedTask;
    }

    /// <summary>Lays the image out on the page: never enlarged beyond its natural size, shrunk to fit inside the margins.</summary>
    public static Visual BuildPrintVisual(PixelBuffer image, double pageWidth, double pageHeight)
    {
        ArgumentNullException.ThrowIfNull(image);
        const double margin = 48; // half an inch
        double availableW = Math.Max(1, pageWidth - margin * 2), availableH = Math.Max(1, pageHeight - margin * 2);
        double scale = Math.Min(1, Math.Min(availableW / image.Width, availableH / image.Height));
        double w = image.Width * scale, h = image.Height * scale;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(ImageCodec.Flatten(image, ColorRgba.White).ToBitmapSource(), new Rect((pageWidth - w) / 2, margin, w, h));
        }

        return visual;
    }
}
