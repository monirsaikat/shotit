using ShotIt.App.Services;

namespace ShotIt.App;

/// <summary>Opens the application's secondary windows, one instance of each.</summary>
public static class WindowsHost
{
    public static void OpenSettings(AppServices app) => app.Notifier.Warn("Settings", "The settings window is not built yet.");

    public static void OpenHistory(AppServices app) => app.Notifier.Warn("History", "The history window is not built yet.");

    public static void OpenOnboarding(AppServices app)
    {
    }
}
