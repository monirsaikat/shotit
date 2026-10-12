using ShotIt.Shell;
using Velopack;

namespace ShotIt.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Velopack handles its install, update and uninstall hooks here and exits for them, before
        // any window, tray icon or single-instance check exists.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => RemoveShellIntegration())
            .Run();

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }

    /// <summary>
    /// Uninstall leaves Windows as it was found: no start-up entry, no context-menu verbs, no Send-to
    /// shortcut. The user's screenshots and settings are deliberately left alone.
    /// </summary>
    private static void RemoveShellIntegration()
    {
        try
        {
            if (Environment.ProcessPath is { Length: > 0 } exe) new ShellIntegration(exe).RemoveAll();
        }
        catch (Exception)
        {
            // An uninstall hook has a hard time limit and nowhere to report to; never let it throw.
        }
    }
}
