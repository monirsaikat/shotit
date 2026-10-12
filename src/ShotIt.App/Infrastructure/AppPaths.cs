using System.IO;
using Serilog;
using Serilog.Core;

namespace ShotIt.App.Infrastructure;

/// <summary>
/// Where ShotIt keeps its data. Installed copies use %LOCALAPPDATA%\ShotIt; a portable copy
/// (recognised by a "data" folder beside the executable) keeps everything next to itself.
/// </summary>
public sealed class AppPaths
{
    public AppPaths(string? overrideRoot = null)
    {
        string exeDir = AppContext.BaseDirectory;
        string portableDir = Path.Combine(exeDir, "data");
        string installedDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShotIt");

        if (overrideRoot is not null)
        {
            DataRoot = overrideRoot;
        }
        else if (Directory.Exists(portableDir) && IsWritable(portableDir))
        {
            DataRoot = portableDir;
            IsPortable = true;
        }
        else
        {
            DataRoot = installedDir;
            PortableFallback = Directory.Exists(portableDir);
        }

        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogDirectory);
    }

    public string DataRoot { get; }
    public bool IsPortable { get; }

    /// <summary>True when a portable data folder exists but is read-only, so the profile folder is used instead.</summary>
    public bool PortableFallback { get; }

    public string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public string HistoryDatabase => Path.Combine(DataRoot, "history.db");
    public string LogDirectory => Path.Combine(DataRoot, "logs");
    public string CrashDirectory => Path.Combine(DataRoot, "crash");

    /// <summary>Automatic copies of every capture, organised by month. Managed by the retention policy.</summary>
    public string LibraryDirectory => Path.Combine(DataRoot, "captures");

    /// <summary>Editable projects (base image plus annotations) for history entries, named by entry id.</summary>
    public string ProjectsDirectory => Path.Combine(DataRoot, "projects");

    /// <summary>Pinned images remembered between runs, when the user turned that on.</summary>
    public string PinsDirectory => Path.Combine(DataRoot, "pins");

    /// <summary>Default folder for files the user saves explicitly.</summary>
    public static string DefaultSaveFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ShotIt");

    private static bool IsWritable(string directory)
    {
        try
        {
            string probe = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N"));
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

public static class Logging
{
    public static Logger Create(AppPaths paths) =>
        new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(paths.LogDirectory, "shotit-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                fileSizeLimitBytes: 5 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture)
            .CreateLogger();

    /// <summary>Writes a self-contained crash report. Never throws.</summary>
    public static void WriteCrashReport(AppPaths paths, Exception exception, string origin)
    {
        try
        {
            Directory.CreateDirectory(paths.CrashDirectory);
            string file = Path.Combine(paths.CrashDirectory, $"crash-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.txt");
            File.WriteAllText(file, $"""
                ShotIt {typeof(Logging).Assembly.GetName().Version}
                Time (UTC): {DateTime.UtcNow:o}
                OS: {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})
                Runtime: {Environment.Version}
                Origin: {origin}

                {exception}
                """);
        }
        catch
        {
            // A crash handler must not crash.
        }
    }
}
