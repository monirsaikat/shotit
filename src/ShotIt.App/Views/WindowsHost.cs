using ShotIt.App.Services;

namespace ShotIt.App.Views;

/// <summary>Opens the application's secondary windows, one instance of each.</summary>
public static class WindowsHost
{
    public static SettingsWindow OpenSettings(AppServices app, string? section = null)
    {
        ArgumentNullException.ThrowIfNull(app);
        var window = app.ShowSingle(() => new SettingsWindow(app, section));
        if (section is not null) window.Select(section);
        return window;
    }

    public static HistoryWindow OpenHistory(AppServices app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.ShowSingle(() => new HistoryWindow(app));
    }

    public static OnboardingWindow OpenOnboarding(AppServices app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.ShowSingle(() => new OnboardingWindow(app));
    }
}
