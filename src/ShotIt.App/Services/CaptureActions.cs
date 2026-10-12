using System.Diagnostics;
using System.IO;
using System.Windows;
using Serilog;
using ShotIt.App.Overlay;

namespace ShotIt.App.Services;

/// <summary>
/// Carries out what the user chose to do with a capture. This is the feature boundary for output:
/// every failure ends here as a notification, never as an unhandled exception.
/// </summary>
public sealed class CaptureActions
{
    private readonly AppServices _app;
    private readonly ILogger _log;

    public CaptureActions(AppServices app)
    {
        _app = app;
        _log = app.Log;
    }

    /// <summary>Optional features plug in here; a missing handler shows a clear message instead of failing.</summary>
    public Dictionary<OverlayAction, Func<CaptureResult, Window?, Task>> Handlers { get; } = [];

    /// <summary>Runs an action. <paramref name="notifyOnCopy"/> shows a confirmation for captures made without an overlay.</summary>
    public void Run(CaptureResult result, OverlayAction action, Window? owner = null, string? notifyOnCopy = null)
    {
        ArgumentNullException.ThrowIfNull(result);
        try
        {
            switch (action)
            {
                case OverlayAction.Cancel:
                    return;

                case OverlayAction.Copy:
                    if (!_app.Output.Copy(result))
                        _app.Notifier.Warn("Could not copy", "Another program is holding the clipboard. Try again in a moment.");
                    else if (notifyOnCopy is not null)
                        _app.Notifier.Info(notifyOnCopy, "The image is on the clipboard.");
                    break;

                case OverlayAction.Save when _app.Settings.Output.AskWhereToSave:
                    Run(result, OverlayAction.SaveAs, owner);
                    break;

                case OverlayAction.Save:
                {
                    string? path = _app.Output.Save(result, out string? error);
                    if (path is null) _app.Notifier.Warn("Could not save", error ?? "The file could not be written.");
                    else _app.Notifier.Info("Saved", Path.GetFileName(path), () => RevealInExplorer(path));
                    break;
                }

                case OverlayAction.SaveAs:
                {
                    string? path = _app.Output.SaveAs(result, owner, out string? error);
                    if (error is not null) _app.Notifier.Warn("Could not save", error);
                    else if (path is not null) _app.SaveSettingsSoon();
                    break;
                }

                case OverlayAction.Pin:
                    _app.Output.EnsureInLibrary(result);
                    _app.Pins.Pin(result.Image, result.ScreenRegion?.Location);
                    break;

                case OverlayAction.Edit:
                    _app.OpenEditor(result);
                    break;

                default:
                    if (Handlers.TryGetValue(action, out var handler)) _ = RunAsync(handler, result, owner, action);
                    else _app.Notifier.Warn("Not available", $"“{action}” is not available in this build.");
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Action {Action} failed", action);
            _app.Notifier.Warn("Something went wrong", "The action could not be completed. Details are in the log file.");
        }
    }

    private async Task RunAsync(Func<CaptureResult, Window?, Task> handler, CaptureResult result, Window? owner, OverlayAction action)
    {
        try
        {
            await handler(result, owner);
        }
        catch (OperationCanceledException)
        {
            // The user cancelled; nothing to report.
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Action {Action} failed", action);
            _app.Notifier.Warn("Something went wrong", "The action could not be completed. Details are in the log file.");
        }
    }

    /// <summary>Opens Explorer with the file selected.</summary>
    public static void RevealInExplorer(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = false })?.Dispose();
            else if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder))
                OpenFolder(folder);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
        }
    }

    public static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Opens a web address in the default browser. Only http and https are ever launched.</summary>
    public static bool OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return false;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
