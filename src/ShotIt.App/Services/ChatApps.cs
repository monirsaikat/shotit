using ShotIt.Native;

namespace ShotIt.App.Services;

/// <summary>A desktop chat program a capture can be sent to.</summary>
public sealed record ChatApp(string Name, string[] ProcessNames, string[] WindowTitles, string LaunchUri, string WebUrl)
{
    public static ChatApp Slack { get; } = new("Slack", ["slack"], [], "slack://open", "https://app.slack.com/client");

    // The current WhatsApp is WhatsApp.Root.exe; older Store versions ran as WhatsApp.exe or inside ApplicationFrameHost.
    public static ChatApp WhatsApp { get; } = new("WhatsApp", ["whatsapp", "whatsapp.root"], ["WhatsApp"], "whatsapp://", "https://web.whatsapp.com");

    /// <summary>
    /// The program's window when it is open on screen, front-most first. Minimised windows and ones closed
    /// to the tray are not listed; launching <see cref="LaunchUri"/> brings those back instead.
    /// </summary>
    public nint FindWindow()
    {
        foreach (var window in WindowList.EnumerateTopLevel((uint)Environment.ProcessId))
        {
            if (window.Bounds.Width < 200 || window.Bounds.Height < 200) continue;
            if (WindowTitles.Any(t => string.Equals(window.Title, t, StringComparison.OrdinalIgnoreCase))) return window.Handle;
            if (window.Title.Length == 0) continue;
            string process = WindowList.GetProcessName(window.ProcessId);
            if (ProcessNames.Any(p => string.Equals(process, p, StringComparison.OrdinalIgnoreCase))) return window.Handle;
        }

        return 0;
    }
}
