using System.IO;
using Microsoft.Win32;
using Serilog;
using ShotIt.App.Infrastructure;
using ShotIt.Core.Editing;
using ShotIt.Core.Geometry;
using ShotIt.Core.History;
using ShotIt.Core.Imaging;
using ShotIt.Core.Naming;
using ShotIt.Core.Settings;
using ShotIt.Rendering;

namespace ShotIt.App.Services;

/// <summary>
/// One finished capture travelling through the output actions. It remembers its history entry so that
/// copying and then saving the same capture updates one library item instead of creating two.
/// </summary>
public sealed class CaptureResult
{
    private PixelBuffer? _final;

    public CaptureResult(DocState document, string sourceApp = "", string sourceTitle = "", PixelRect? screenRegion = null)
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        SourceApp = sourceApp ?? string.Empty;
        SourceTitle = sourceTitle ?? string.Empty;
        ScreenRegion = screenRegion;
        CapturedAt = DateTime.Now;
    }

    /// <summary>The editable form: image plus annotations.</summary>
    public DocState Document { get; }

    public string SourceApp { get; }
    public string SourceTitle { get; }

    /// <summary>Where on the virtual screen the capture came from, when known (used to place pins).</summary>
    public PixelRect? ScreenRegion { get; }

    public DateTime CapturedAt { get; }

    /// <summary>The flattened pixels that get copied, saved and uploaded. Rendered once, on the UI thread.</summary>
    public PixelBuffer Image => _final ??= DocumentRenderer.RenderFinal(Document);

    public long? HistoryId { get; set; }
    public string? LibraryPath { get; set; }
    public string? SavedPath { get; set; }
}

/// <summary>Copy, save and library bookkeeping for captures. Every method reports failure instead of throwing.</summary>
public sealed class OutputService
{
    private readonly AppSettings _settings;
    private readonly AppPaths _paths;
    private readonly HistoryRepository _history;
    private readonly ILogger _log;
    private int _counter;

    public OutputService(AppSettings settings, AppPaths paths, HistoryRepository history, ILogger log)
    {
        _settings = settings;
        _paths = paths;
        _history = history;
        _log = log;
    }

    public event Action? HistoryChanged;

    private bool IsExcludedFromHistory(CaptureResult result) =>
        _settings.History.Mode == HistoryMode.Off ||
        Wildcard.MatchesAny(result.SourceTitle, _settings.History.ExcludedTitlePatterns);

    private string BaseName(CaptureResult result) => FileNameTemplate.Expand(
        _settings.Output.FileNameTemplate,
        new FileNameContext(result.CapturedAt, result.SourceApp, result.SourceTitle, result.Image.Width, result.Image.Height, Interlocked.Increment(ref _counter)));

    public string SaveFolder => string.IsNullOrWhiteSpace(_settings.Output.SaveFolder) ? AppPaths.DefaultSaveFolder : _settings.Output.SaveFolder;

    /// <summary>Copies the image to the clipboard and records it in the library.</summary>
    public bool Copy(CaptureResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (_settings.History.Mode == HistoryMode.All) EnsureInLibrary(result);
        if (_settings.Output.AutoSaveEveryCapture && result.SavedPath is null) Save(result);
        string? file = _settings.Output.CopyFileAlongsideImage ? result.SavedPath ?? result.LibraryPath : null;
        bool ok = ClipboardService.SetImage(result.Image, file);
        if (!ok) _log.Warning("Clipboard was busy; image not copied");
        return ok;
    }

    /// <summary>Saves into the configured folder using the file name template. Returns the path, or null on failure.</summary>
    public string? Save(CaptureResult result, out string? error)
    {
        ArgumentNullException.ThrowIfNull(result);
        error = null;
        string folder = SaveFolder;
        var format = _settings.Output.Format;
        try
        {
            Directory.CreateDirectory(folder);
            string path = FileNameTemplate.MakeUniquePath(folder, BaseName(result), ImageCodec.Extension(format));
            WriteFile(path, result.Image, format);
            OnSaved(result, path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _log.Warning(ex, "Saving to {Folder} failed", folder);
            error = $"Could not save to “{folder}”. {ex.Message} Choose another folder in Settings › Output.";
            return null;
        }
    }

    public string? Save(CaptureResult result) => Save(result, out _);

    /// <summary>Writes the capture back to a file it was saved to before (the editor's plain "Save").</summary>
    public bool Overwrite(CaptureResult result, string path, out string? error)
    {
        ArgumentNullException.ThrowIfNull(result);
        error = null;
        try
        {
            WriteFile(path, result.Image, ImageCodec.FormatFromPath(path));
            OnSaved(result, path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _log.Warning(ex, "Overwriting {Path} failed", path);
            error = "Could not save the file. " + ex.Message;
            return false;
        }
    }

    /// <summary>Asks where to save. Returns the path, or null when cancelled or failed.</summary>
    public string? SaveAs(CaptureResult result, System.Windows.Window? owner, out string? error)
    {
        ArgumentNullException.ThrowIfNull(result);
        error = null;
        string initial = Directory.Exists(_settings.Output.LastSaveAsFolder) ? _settings.Output.LastSaveAsFolder : SaveFolder;
        try { Directory.CreateDirectory(initial); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { initial = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures); }

        var dialog = new SaveFileDialog
        {
            Title = "Save screenshot",
            InitialDirectory = initial,
            FileName = BaseName(result),
            Filter = "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg|Bitmap (*.bmp)|*.bmp|PDF document (*.pdf)|*.pdf",
            FilterIndex = _settings.Output.Format switch { ImageFormat.Jpeg => 2, ImageFormat.Bmp => 3, ImageFormat.Pdf => 4, _ => 1 },
            AddExtension = true,
            OverwritePrompt = true,
        };
        bool? chosen = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (chosen != true) return null;

        try
        {
            string path = dialog.FileName;
            WriteFile(path, result.Image, ImageCodec.FormatFromPath(path));
            _settings.Output.LastSaveAsFolder = Path.GetDirectoryName(path) ?? string.Empty;
            OnSaved(result, path);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _log.Warning(ex, "Save As failed");
            error = "Could not save the file. " + ex.Message;
            return null;
        }
    }

    private void WriteFile(string path, PixelBuffer image, ImageFormat format)
    {
        byte[] bytes = ImageCodec.Encode(image, format, _settings.Output.JpegQuality);
        // Write to a temporary name first so a failure never leaves a truncated image behind.
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private void OnSaved(CaptureResult result, string path)
    {
        result.SavedPath = path;
        if (IsExcludedFromHistory(result)) return;
        try
        {
            if (result.HistoryId is { } id && _history.Get(id) is { } existing)
            {
                // Point the library entry at the user's file and drop the internal copy.
                if (result.LibraryPath is { } library && !string.Equals(library, path, StringComparison.OrdinalIgnoreCase)) TryDelete(library);
                result.LibraryPath = null;
                _history.Update(existing with
                {
                    FilePath = path, FileSize = new FileInfo(path).Length, Width = result.Image.Width, Height = result.Image.Height,
                    Thumbnail = ImageCodec.Thumbnail(result.Image),
                });
            }
            else
            {
                result.HistoryId = _history.Add(NewItem(result, path));
            }

            SyncProject(result);
            HistoryChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not record the saved file in history");
        }
    }

    private HistoryItem NewItem(CaptureResult result, string path) => new()
    {
        CreatedUtc = result.CapturedAt.ToUniversalTime(),
        FilePath = path,
        Width = result.Image.Width,
        Height = result.Image.Height,
        SourceApp = result.SourceApp,
        SourceTitle = result.SourceTitle,
        FileSize = File.Exists(path) ? new FileInfo(path).Length : 0,
        Thumbnail = ImageCodec.Thumbnail(result.Image),
    };

    /// <summary>
    /// Makes sure the capture has a library entry, writing an internal PNG copy when the user has not saved it.
    /// Returns the file backing the entry, or null when history is off, excluded or the disk refused.
    /// </summary>
    public string? EnsureInLibrary(CaptureResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.HistoryId is not null) return result.SavedPath ?? result.LibraryPath;
        if (IsExcludedFromHistory(result)) return null;
        try
        {
            string folder = Path.Combine(_paths.LibraryDirectory, result.CapturedAt.ToString("yyyy", System.Globalization.CultureInfo.InvariantCulture), result.CapturedAt.ToString("MM", System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(folder);
            string path = FileNameTemplate.MakeUniquePath(folder, BaseName(result), ".png");
            WriteFile(path, result.Image, ImageFormat.Png);
            result.LibraryPath = path;
            result.HistoryId = _history.Add(NewItem(result, path));
            SyncProject(result);
            HistoryChanged?.Invoke();
            return path;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not add the capture to the library");
            return null;
        }
    }

    public void RecordUpload(CaptureResult result, string url, string? deleteUrl)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            if (result.HistoryId is { } id && _history.Get(id) is { } item)
            {
                _history.Update(item with { UploadUrl = url, DeleteUrl = deleteUrl ?? string.Empty });
                HistoryChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not record the upload in history");
        }
    }

    public void RecordOcrText(CaptureResult result, string text)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            if (result.HistoryId is { } id && _history.Get(id) is { } item)
            {
                _history.Update(item with { OcrText = text });
                HistoryChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not record OCR text in history");
        }
    }

    /// <summary>Applies the retention policy: removes expired library entries and the internal copies behind them.</summary>
    public int ApplyRetention()
    {
        try
        {
            long maxBytes = (long)_settings.History.MaxSizeMb * 1024 * 1024;
            var expired = _history.FindExpired(_settings.History.MaxAgeDays, maxBytes);
            if (expired.Count == 0) return 0;
            foreach (var item in expired)
            {
                // Only files inside ShotIt's own library folder are deleted; files the user saved elsewhere stay.
                if (IsInsideLibrary(item.FilePath)) TryDelete(item.FilePath);
                TryDelete(ProjectPath(item.Id));
            }

            _history.Delete(expired.Select(e => e.Id));
            HistoryChanged?.Invoke();
            return expired.Count;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Retention pass failed");
            return 0;
        }
    }

    // ------------------------------------------------------------------ editable projects

    public string ProjectPath(long historyId) =>
        Path.Combine(_paths.ProjectsDirectory, historyId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ProjectFile.Extension);

    /// <summary>Keeps the editable project for a history entry in step with the capture: written when there are annotations, removed when there are none.</summary>
    private void SyncProject(CaptureResult result)
    {
        if (result.HistoryId is not { } id) return;
        string path = ProjectPath(id);
        try
        {
            if (ProjectFile.HasEditableContent(result.Document)) ProjectFile.Save(path, result.Document);
            else TryDelete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _log.Warning(ex, "Could not store the editable project for history entry {Id}", id);
        }
    }

    /// <summary>
    /// Loads a history entry for editing. When an editable project exists the annotations come back as
    /// objects; otherwise the flattened picture is opened. Returns null (with a reason) when the file is gone.
    /// </summary>
    public CaptureResult? OpenHistoryItem(HistoryItem item, out string? error)
    {
        ArgumentNullException.ThrowIfNull(item);
        error = null;
        DocState? document = null;
        string project = ProjectPath(item.Id);
        if (File.Exists(project))
        {
            try
            {
                document = ProjectFile.Load(project);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _log.Warning(ex, "The editable project for history entry {Id} could not be read; opening the picture instead", item.Id);
            }
        }

        if (document is null)
        {
            if (!File.Exists(item.FilePath))
            {
                error = "The file is no longer there. It may have been moved or deleted.";
                return null;
            }

            try
            {
                document = DocState.FromImage(ImageCodec.DecodeFile(item.FilePath));
            }
            catch (Exception ex) when (ex is NotSupportedException or IOException or UnauthorizedAccessException)
            {
                error = "The file could not be read as an image.";
                return null;
            }
        }

        bool internalCopy = IsInsideLibrary(item.FilePath);
        return new CaptureResult(document, item.SourceApp, item.SourceTitle)
        {
            HistoryId = item.Id,
            LibraryPath = internalCopy ? item.FilePath : null,
            SavedPath = internalCopy || !File.Exists(item.FilePath) ? null : item.FilePath,
        };
    }

    /// <summary>Removes history entries, their editable projects and ShotIt's internal copies. Files the user saved elsewhere stay.</summary>
    public int DeleteHistoryItems(IReadOnlyCollection<HistoryItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count == 0) return 0;
        try
        {
            foreach (var item in items)
            {
                if (IsInsideLibrary(item.FilePath)) TryDelete(item.FilePath);
                TryDelete(ProjectPath(item.Id));
            }

            _history.Delete(items.Select(i => i.Id));
            HistoryChanged?.Invoke();
            return items.Count;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Deleting history entries failed");
            return 0;
        }
    }

    /// <summary>Removes everything from history except entries marked "Keep". Returns how many were removed.</summary>
    public int ClearHistory()
    {
        try
        {
            // Collect first, then delete: paging over a list that shrinks would skip entries.
            var doomed = new List<HistoryItem>();
            for (int offset = 0; ; offset += 500)
            {
                var page = _history.Query(null, 500, offset, withThumbnails: false);
                doomed.AddRange(page.Where(i => !i.Keep));
                if (page.Count < 500) break;
            }

            int removed = DeleteHistoryItems(doomed);
            return removed;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Clearing history failed");
            return 0;
        }
    }

    public void SetKeep(HistoryItem item, bool keep)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            if (_history.Get(item.Id) is { } current) _history.Update(current with { Keep = keep });
            HistoryChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Could not change the keep flag");
        }
    }

    public bool IsInsideLibrary(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            string root = Path.GetFullPath(_paths.LibraryDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Information("Could not delete {Path}: {Message}", path, ex.Message);
        }
    }
}
