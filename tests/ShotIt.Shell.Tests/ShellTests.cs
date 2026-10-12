using Microsoft.Win32;
using ShotIt.Shell;

namespace ShotIt.Shell.Tests;

/// <summary>A scratch registry key and folder that are removed after each test; nothing real is touched.</summary>
public sealed class Sandbox : IDisposable
{
    private readonly string _keyName = @"Software\ShotIt.Tests\" + Guid.NewGuid().ToString("N");

    public Sandbox()
    {
        Root = Registry.CurrentUser.CreateSubKey(_keyName, writable: true);
        Folder = Path.Combine(Path.GetTempPath(), "shotit-shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Folder);
        Exe = Path.Combine(Folder, "Program Files", "ShotIt.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(Exe)!);
        File.WriteAllBytes(Exe, [0]);
    }

    public RegistryKey Root { get; }
    public string Folder { get; }
    public string Exe { get; }
    public string SendTo => Path.Combine(Folder, "SendTo");

    public ShellIntegration Create(string? exe = null) => new(exe ?? Exe, Root, SendTo);

    public void Dispose()
    {
        Root.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_keyName, throwOnMissingSubKey: false);
        using (var parent = Registry.CurrentUser.OpenSubKey(@"Software\ShotIt.Tests"))
        {
            if (parent is { SubKeyCount: 0 }) Registry.CurrentUser.DeleteSubKeyTree(@"Software\ShotIt.Tests", throwOnMissingSubKey: false);
        }

        try { Directory.Delete(Folder, true); }
        catch (IOException) { }
    }
}

public class StartupTests
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Approved = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    [Fact]
    public void TurningStartupOnWritesAQuotedSilentCommand()
    {
        using var box = new Sandbox();
        var shell = box.Create();
        Assert.Equal(StartupState.Off, shell.GetStartupState());
        Assert.True(shell.SetStartup(true));
        Assert.Equal(StartupState.On, shell.GetStartupState());
        using var run = box.Root.OpenSubKey(Run)!;
        Assert.Equal("\"" + box.Exe + "\" --silent", run.GetValue("ShotIt"));
    }

    [Fact]
    public void TurningStartupOffRemovesOnlyOurValue()
    {
        using var box = new Sandbox();
        using (var run = box.Root.CreateSubKey(Run)) run.SetValue("OtherApp", "other.exe");
        var shell = box.Create();
        shell.SetStartup(true);
        Assert.True(shell.SetStartup(false));
        Assert.True(shell.SetStartup(false));
        Assert.Equal(StartupState.Off, shell.GetStartupState());
        using var after = box.Root.OpenSubKey(Run)!;
        Assert.Equal(["OtherApp"], after.GetValueNames());
    }

    [Theory]
    [InlineData(new byte[] { 2, 0, 0, 0 }, StartupState.On)]
    [InlineData(new byte[] { 6, 0, 0, 0 }, StartupState.On)]
    [InlineData(new byte[] { 3, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, StartupState.DisabledByUser)]
    [InlineData(new byte[] { 7 }, StartupState.DisabledByUser)]
    [InlineData(new byte[0], StartupState.On)]
    public void TaskManagerSwitchIsRespected(byte[] flags, StartupState expected)
    {
        using var box = new Sandbox();
        var shell = box.Create();
        shell.SetStartup(true);
        using (var approved = box.Root.CreateSubKey(Approved)) approved.SetValue("ShotIt", flags, RegistryValueKind.Binary);
        Assert.Equal(expected, shell.GetStartupState());
    }

    [Fact]
    public void EnablingAgainClearsTheTaskManagerSwitch()
    {
        using var box = new Sandbox();
        var shell = box.Create();
        shell.SetStartup(true);
        using (var approved = box.Root.CreateSubKey(Approved)) approved.SetValue("ShotIt", new byte[] { 3 }, RegistryValueKind.Binary);
        shell.SetStartup(true);
        Assert.Equal(StartupState.On, shell.GetStartupState());
    }

    [Fact]
    public void ARegistrationForAMovedCopyIsDetectedAndRepaired()
    {
        using var box = new Sandbox();
        box.Create(Path.Combine(box.Folder, "old", "ShotIt.exe")).SetStartup(true);
        var shell = box.Create();
        Assert.Equal(StartupState.PointsElsewhere, shell.GetStartupState());
        shell.SetStartup(true);
        Assert.Equal(StartupState.On, shell.GetStartupState());
    }

    [Fact]
    public void QuoteStripsEmbeddedQuotes() => Assert.Equal("\"C:\\a b\\x.exe\"", ShellIntegration.Quote("C:\\a b\\\"x.exe"));
}

public class ContextMenuTests
{
    private const string Verbs = @"Software\Classes\SystemFileAssociations\image\shell";

    [Fact]
    public void InstallsEditAndPinVerbsForImages()
    {
        using var box = new Sandbox();
        var shell = box.Create();
        Assert.False(shell.IsContextMenuInstalled());
        Assert.True(shell.SetContextMenu(true));
        Assert.True(shell.IsContextMenuInstalled());

        using var edit = box.Root.OpenSubKey(Verbs + @"\ShotIt.Edit")!;
        Assert.Equal("Edit with ShotIt", edit.GetValue("MUIVerb"));
        using var editCommand = edit.OpenSubKey("command")!;
        Assert.Equal("\"" + box.Exe + "\" --edit \"%1\"", editCommand.GetValue(null));
        using var pinCommand = box.Root.OpenSubKey(Verbs + @"\ShotIt.Pin\command")!;
        Assert.Equal("\"" + box.Exe + "\" --pin \"%1\"", pinCommand.GetValue(null));
    }

    [Fact]
    public void RemovingLeavesOtherVerbsAlone()
    {
        using var box = new Sandbox();
        using (var other = box.Root.CreateSubKey(Verbs + @"\OtherTool\command")) other.SetValue(null, "other.exe \"%1\"");
        var shell = box.Create();
        shell.SetContextMenu(true);
        Assert.True(shell.SetContextMenu(false));
        Assert.True(shell.SetContextMenu(false));
        Assert.False(shell.IsContextMenuInstalled());
        using var verbs = box.Root.OpenSubKey(Verbs)!;
        Assert.Equal(["OtherTool"], verbs.GetSubKeyNames());
    }

    [Fact]
    public void AMenuForAnotherCopyDoesNotCountAsInstalled()
    {
        using var box = new Sandbox();
        box.Create(Path.Combine(box.Folder, "old", "ShotIt.exe")).SetContextMenu(true);
        Assert.False(box.Create().IsContextMenuInstalled());
    }
}

public class SendToAndUninstallTests
{
    [Fact]
    public void SendToShortcutIsCreatedAndRemoved()
    {
        using var box = new Sandbox();
        var shell = box.Create();
        Assert.False(shell.IsSendToInstalled());
        Assert.True(shell.SetSendTo(true));
        Assert.True(shell.IsSendToInstalled());
        Assert.True(new FileInfo(Path.Combine(box.SendTo, "ShotIt.lnk")).Length > 0);
        Assert.True(shell.SetSendTo(false));
        Assert.True(shell.SetSendTo(false));
        Assert.False(shell.IsSendToInstalled());
    }

    [Fact]
    public void RemoveAllLeavesNoTrace()
    {
        using var box = new Sandbox();
        var shell = box.Create();
        shell.SetStartup(true);
        shell.SetContextMenu(true);
        shell.SetSendTo(true);
        using (var approved = box.Root.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"))
            approved.SetValue("ShotIt", new byte[] { 3 }, RegistryValueKind.Binary);

        shell.RemoveAll();
        shell.RemoveAll();

        Assert.Equal(StartupState.Off, shell.GetStartupState());
        Assert.False(shell.IsContextMenuInstalled());
        Assert.False(shell.IsSendToInstalled());
        using var approvedAfter = box.Root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run")!;
        Assert.Empty(approvedAfter.GetValueNames());
        using var verbs = box.Root.OpenSubKey(@"Software\Classes\SystemFileAssociations\image\shell");
        Assert.True(verbs is null || verbs.SubKeyCount == 0);
    }
}

public class PrintScreenBindingTests
{
    [Theory]
    [InlineData(null, 19045, false)]
    [InlineData(null, 22000, false)]
    [InlineData(null, 22631, true)]
    [InlineData(null, 26100, true)]
    [InlineData(1, 19045, true)]
    [InlineData(0, 26100, false)]
    public void DetectsWhetherSnippingToolOwnsPrintScreen(int? value, int build, bool expected)
    {
        using var box = new Sandbox();
        if (value is { } v)
        {
            using var key = box.Root.CreateSubKey(@"Control Panel\Keyboard");
            key.SetValue("PrintScreenKeyForSnippingEnabled", v, RegistryValueKind.DWord);
        }

        Assert.Equal(expected, new PrintScreenBinding(box.Root, build).IsSnippingToolBound());
    }

    [Fact]
    public void ReleaseAndRestoreRoundTrip()
    {
        using var box = new Sandbox();
        var binding = new PrintScreenBinding(box.Root, 26100);
        Assert.True(binding.ReleaseForShotIt());
        Assert.False(binding.IsSnippingToolBound());
        Assert.True(binding.RestoreSnippingTool());
        Assert.True(binding.IsSnippingToolBound());
    }
}

public class SecretProtectorTests
{
    [Theory]
    [InlineData("hunter2")]
    [InlineData("pässwörd with spaces / and + symbols 🔑")]
    public void SecretsRoundTripAndAreNotStoredInTheClear(string secret)
    {
        var protector = new DpapiSecretProtector();
        string stored = protector.Protect(secret);
        Assert.NotEqual(secret, stored);
        Assert.DoesNotContain(secret, stored, StringComparison.Ordinal);
        Assert.Equal(secret, protector.Unprotect(stored));
    }

    [Fact]
    public void EmptyAndDamagedValuesBecomeEmptyStrings()
    {
        var protector = new DpapiSecretProtector();
        Assert.Equal(string.Empty, protector.Protect(string.Empty));
        Assert.Equal(string.Empty, protector.Unprotect(string.Empty));
        Assert.Equal(string.Empty, protector.Unprotect("not base64 !!"));
        Assert.Equal(string.Empty, protector.Unprotect(Convert.ToBase64String([1, 2, 3, 4, 5, 6, 7, 8])));
        string stored = protector.Protect("secret");
        // Damage the encrypted payload (the header of a DPAPI blob is not integrity-checked).
        int at = stored.Length - 12;
        char swapped = stored[at] == 'A' ? 'B' : 'A';
        Assert.Equal(string.Empty, protector.Unprotect(stored[..at] + swapped + stored[(at + 1)..]));
    }

    [Fact]
    public void TheSameSecretEncryptsDifferentlyEachTime()
    {
        var protector = new DpapiSecretProtector();
        Assert.NotEqual(protector.Protect("same"), protector.Protect("same"));
    }
}


public class MachineStartupTests
{
    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static ShellIntegration Create(Sandbox user, Sandbox machine, string command)
    {
        using (var run = machine.Root.CreateSubKey(Run)) run.SetValue("ShotIt", command);
        return new ShellIntegration(user.Exe, user.Root, user.SendTo, machine.Root);
    }

    [Fact]
    public void AMachineWideEntryForThisCopyIsReportedAsOnForAllUsers()
    {
        using var user = new Sandbox();
        using var machine = new Sandbox();
        var shell = Create(user, machine, "\"" + user.Exe + "\" --silent");
        Assert.Equal(StartupState.OnForAllUsers, shell.GetStartupState());
    }

    [Fact]
    public void AMachineWideEntryForAnotherCopyIsIgnored()
    {
        using var user = new Sandbox();
        using var machine = new Sandbox();
        var shell = Create(user, machine, "\"C:\\Other\\ShotIt.exe\" --silent");
        Assert.Equal(StartupState.Off, shell.GetStartupState());
        shell.SetStartup(true);
        Assert.Equal(StartupState.On, shell.GetStartupState());
    }

    [Fact]
    public void RemoveAllNeverTouchesTheMachineEntry()
    {
        using var user = new Sandbox();
        using var machine = new Sandbox();
        var shell = Create(user, machine, "\"" + user.Exe + "\" --silent");
        shell.RemoveAll();
        using var after = machine.Root.OpenSubKey(Run)!;
        Assert.NotNull(after.GetValue("ShotIt"));
    }
}
