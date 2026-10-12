using System.Reflection;
using Serilog;
using ShotIt.Core.Settings;
using Velopack;
using Velopack.Sources;

namespace ShotIt.App.Services;

/// <summary>
/// Checks for a newer ShotIt, downloads it in the background and installs it when ShotIt next closes.
/// It never interrupts a capture and never restarts the program on its own.
/// </summary>
public sealed class UpdateService
{
    private readonly AppSettings _settings;
    private readonly ILogger _log;
    private readonly UpdateManager? _manager;
    private readonly string _unavailableReason = string.Empty;
    private UpdateInfo? _ready;
    private bool _busy;

    public UpdateService(AppSettings settings, ILogger log)
    {
        _settings = settings;
        _log = log;
        string feed = ResolveFeed(settings);
        if (feed.Length == 0)
        {
            _unavailableReason = "This build has no update address, so it does not update itself.";
            return;
        }

        if (!Uri.TryCreate(feed, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            // Updates replace the program, so they are only ever fetched over an encrypted connection.
            _unavailableReason = "The update address must start with https://.";
            return;
        }

        try
        {
            bool prerelease = !string.Equals(settings.Updates.Channel, "stable", StringComparison.OrdinalIgnoreCase);
            IUpdateSource source = string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                ? new GithubSource(feed.TrimEnd('/'), null, prerelease)
                : new SimpleWebSource(feed);
            _manager = new UpdateManager(source);
            if (!_manager.IsInstalled) _unavailableReason = "This copy was not set up by the installer (portable or development copy), so it does not update itself.";
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "The updater could not be initialised");
            _unavailableReason = "The updater could not start. Details are in the log file.";
        }
    }

    internal static string ResolveFeed(AppSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Updates.FeedUrl)) return settings.Updates.FeedUrl.Trim();
        return typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateFeedUrl")?.Value?.Trim() ?? string.Empty;
    }

    /// <summary>True when this copy can update itself.</summary>
    public bool IsAvailable => _manager is { IsInstalled: true };

    public bool UpdateReady => _ready is not null;

    public string Describe() =>
        !IsAvailable ? _unavailableReason
        : _ready is not null ? $"Version {_ready.TargetFullRelease.Version} is downloaded and installs when ShotIt next closes."
        : "ShotIt checks for updates in the background and installs them when it closes.";

    /// <summary>Checks, downloads if there is something newer, and returns a sentence describing the outcome.</summary>
    public async Task<string> CheckAndDownloadAsync(bool userInitiated)
    {
        if (!IsAvailable || _manager is null) return _unavailableReason;
        if (_busy) return "Already checking…";
        if (_ready is not null) return Describe();
        _busy = true;
        try
        {
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(true);
            if (info is null) return "You have the latest version.";
            _log.Information("Update {Version} found; downloading", info.TargetFullRelease.Version);
            await _manager.DownloadUpdatesAsync(info).ConfigureAwait(true);
            _ready = info;
            return Describe();
        }
        catch (Exception ex)
        {
            // Offline, proxy, server down: an update check must never bother the user unless they asked.
            _log.Information("Update check failed: {Message}", ex.Message);
            return userInitiated ? "Could not check for updates. Check your internet connection and try again." : string.Empty;
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Hands a downloaded update to the installer, which applies it once this process has exited.</summary>
    public void ApplyOnExit()
    {
        if (_manager is null || _ready is null) return;
        try
        {
            _manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: false);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "The downloaded update could not be scheduled");
        }
    }

    public bool AutoCheckEnabled => IsAvailable && _settings.Updates.AutoCheck;
}
