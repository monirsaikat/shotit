using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ShotIt.App.Services;
using ShotIt.Core.Settings;
using ShotIt.Rendering;
using ShotIt.Shell;

namespace ShotIt.App.Views;

/// <summary>
/// The one-time welcome: how to capture, plus the two choices that are better made once up front
/// (start with Windows, and who owns the Print Screen key).
/// </summary>
public sealed class OnboardingWindow : Window
{
    private readonly AppServices _app;
    private readonly CheckBox _startup;
    private readonly CheckBox? _printScreen;
    private bool _finished;

    public OnboardingWindow(AppServices app)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        Title = "Welcome to ShotIt";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = AppIcon.Render(64);
        UseLayoutRounding = true;

        var p = new StackPanel { Margin = new Thickness(28, 24, 28, 22) };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new Image { Source = AppIcon.Render(96), Width = 44, Height = 44, Margin = new Thickness(0, 0, 14, 0) });
        header.Children.Add(new TextBlock { Text = "ShotIt is ready", FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        p.Children.Add(header);

        var keys = app.Settings.Hotkeys;
        string region = keys.Region.IsEmpty ? "the ShotIt icon near the clock" : keys.Region.ToString();
        p.Children.Add(Line("Press " + region + ", drag over what you want, and annotate right there."));
        p.Children.Add(Line("Then press Ctrl+C to copy, Ctrl+S to save, or Esc to cancel."));
        if (!keys.FullScreen.IsEmpty) p.Children.Add(Line(keys.FullScreen + " captures the whole screen."));
        p.Children.Add(Line("ShotIt lives in the notification area. Right-click its icon for everything else."));

        p.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(0x33, 0x80, 0x80, 0x80)), Margin = new Thickness(0, 14, 0, 10) });

        _startup = new CheckBox { Content = "Start ShotIt when I sign in to Windows", IsChecked = true, Margin = new Thickness(0, 4, 0, 4) };
        p.Children.Add(_startup);

        bool usesPrintScreen = new[] { keys.Region, keys.FullScreen, keys.ActiveWindow, keys.Window, keys.LastRegion }.Any(k => k.VirtualKey == Hotkey.VkPrintScreen);
        if (usesPrintScreen && app.PrintScreen.IsSnippingToolBound())
        {
            _printScreen = new CheckBox { Content = "Use Print Screen for ShotIt instead of Snipping Tool", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
            p.Children.Add(_printScreen);
            var hint = Ui.Hint("This turns off one Windows setting (Accessibility › Keyboard). You can turn it back on there at any time.");
            hint.Margin = new Thickness(28, 0, 0, 4);
            p.Children.Add(hint);
        }

        var failures = app.Hotkeys.Failures;
        if (failures.Count > 0)
        {
            p.Children.Add(new TextBlock
            {
                Text = string.Join(", ", failures.Select(f => f.Hotkey.ToString()).Distinct()) + (failures.Count == 1 ? " is" : " are") + " already used by another program. You can pick other keys in Settings.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes2.Warning,
                Margin = new Thickness(0, 8, 0, 0),
            });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        buttons.Children.Add(Ui.Button("Settings…", () =>
        {
            Finish();
            WindowsHost.OpenSettings(_app);
        }));
        var done = Ui.Button("Start using ShotIt", Finish, accent: true);
        done.IsDefault = true;
        done.Margin = new Thickness(0, 4, 0, 4);
        buttons.Children.Add(done);
        p.Children.Add(buttons);
        Content = p;

        // Closing with the title-bar button counts as "done" too, but changes nothing in Windows.
        Closed += (_, _) =>
        {
            if (_finished) return;
            _app.Settings.General.OnboardingDone = true;
            _app.SaveSettingsNow();
        };
    }

    private static TextBlock Line(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3), FontSize = 14 };

    /// <summary>Applies the ticked choices. Internal so the self-test can run it without clicking.</summary>
    internal void Finish()
    {
        if (_finished) return;
        _finished = true;
        if (_startup.IsChecked == true) _app.Shell.SetStartup(true);
        _app.Settings.General.StartWithWindows = _app.Shell.GetStartupState() == StartupState.On;
        if (_printScreen?.IsChecked == true && _app.PrintScreen.ReleaseForShotIt()) _app.Hotkeys.Reapply();
        _app.Settings.General.OnboardingDone = true;
        _app.SaveSettingsNow();
        Close();
    }
}
