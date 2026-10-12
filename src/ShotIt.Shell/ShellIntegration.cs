using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;
using ShotIt.Core.Upload;

namespace ShotIt.Shell;

/// <summary>
/// Encrypts secrets with Windows DPAPI for the current user. The result can only be read by the
/// same Windows account on the same computer, so a copied settings file reveals nothing.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ShotIt.Secrets.v1");

    public string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return string.Empty;
        byte[] data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return string.Empty;
        try
        {
            byte[] data = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return string.Empty;
        }
    }
}

public enum StartupState
{
    /// <summary>ShotIt is not registered to start with Windows.</summary>
    Off,

    /// <summary>Registered and allowed.</summary>
    On,

    /// <summary>Registered, but the user turned it off in Task Manager or Windows Settings.</summary>
    DisabledByUser,

    /// <summary>Registered for a different copy of ShotIt (the program was moved).</summary>
    PointsElsewhere,

    /// <summary>Started for every user of this computer by the MSI installer; only an administrator can change it.</summary>
    OnForAllUsers,
}

/// <summary>
/// Everything ShotIt writes outside its own data folder. All of it is per-user (no administrator
/// rights), and <see cref="RemoveAll"/> takes every trace out again for a clean uninstall.
/// </summary>
public sealed class ShellIntegration
{
    public const string ValueName = "ShotIt";
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ImageShellPath = @"Software\Classes\SystemFileAssociations\image\shell";
    private const string EditVerb = "ShotIt.Edit";
    private const string PinVerb = "ShotIt.Pin";
    private const string SendToFileName = "ShotIt.lnk";

    private readonly RegistryKey _root;
    private readonly RegistryKey? _machineRoot;
    private readonly string _executable;
    private readonly string _sendToFolder;

    /// <param name="executablePath">Full path of ShotIt.exe.</param>
    /// <param name="root">Registry root; tests pass a scratch key instead of HKEY_CURRENT_USER.</param>
    /// <param name="sendToFolder">The SendTo folder; tests pass a temporary folder.</param>
    /// <param name="machineRoot">Machine-wide registry root (read only). Defaults to HKEY_LOCAL_MACHINE, or to nothing when <paramref name="root"/> is a test key.</param>
    public ShellIntegration(string executablePath, RegistryKey? root = null, string? sendToFolder = null, RegistryKey? machineRoot = null)
    {
        _machineRoot = machineRoot ?? (root is null ? Registry.LocalMachine : null);
        if (string.IsNullOrWhiteSpace(executablePath)) throw new ArgumentException("An executable path is required.", nameof(executablePath));
        _executable = Path.GetFullPath(executablePath);
        _root = root ?? Registry.CurrentUser;
        _sendToFolder = sendToFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.SendTo);
    }

    /// <summary>Quotes the path so that spaces and a trailing argument cannot be misread.</summary>
    public static string Quote(string path) => "\"" + path.Replace("\"", string.Empty, StringComparison.Ordinal) + "\"";

    public string StartupCommand => Quote(_executable) + " --silent";

    // ------------------------------------------------------------------ start with Windows

    public StartupState GetStartupState()
    {
        try
        {
            using (var machineRun = _machineRoot?.OpenSubKey(RunPath))
            {
                if (machineRun?.GetValue(ValueName) is string machineCommand && machineCommand.Contains(_executable, StringComparison.OrdinalIgnoreCase))
                    return StartupState.OnForAllUsers;
            }

            using var run = _root.OpenSubKey(RunPath);
            if (run?.GetValue(ValueName) is not string command || command.Length == 0) return StartupState.Off;
            if (!command.Contains(_executable, StringComparison.OrdinalIgnoreCase)) return StartupState.PointsElsewhere;

            using var approved = _root.OpenSubKey(ApprovedPath);
            // Task Manager records its switch here: an even first byte means enabled, odd means disabled.
            if (approved?.GetValue(ValueName) is byte[] { Length: > 0 } flags && (flags[0] & 1) == 1) return StartupState.DisabledByUser;
            return StartupState.On;
        }
        catch (Exception ex) when (IsAccessProblem(ex))
        {
            return StartupState.Off;
        }
    }

    /// <summary>Turns "start with Windows" on or off. Returns false when Windows refused the change.</summary>
    public bool SetStartup(bool enabled)
    {
        try
        {
            using var run = _root.CreateSubKey(RunPath, writable: true);
            if (enabled)
            {
                run.SetValue(ValueName, StartupCommand, RegistryValueKind.String);
                // The user asked for it here, so clear a "disabled" switch left behind in Task Manager.
                using var approved = _root.OpenSubKey(ApprovedPath, writable: true);
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch (Exception ex) when (IsAccessProblem(ex))
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ Explorer context menu

    public bool IsContextMenuInstalled()
    {
        try
        {
            using var key = _root.OpenSubKey(ImageShellPath + "\\" + EditVerb + "\\command");
            return key?.GetValue(null) is string command && command.Contains(_executable, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (IsAccessProblem(ex))
        {
            return false;
        }
    }

    /// <summary>Adds or removes "Edit with ShotIt" and "Pin to screen with ShotIt" for image files.</summary>
    public bool SetContextMenu(bool enabled)
    {
        try
        {
            if (enabled)
            {
                WriteVerb(EditVerb, "Edit with ShotIt", "--edit");
                WriteVerb(PinVerb, "Pin to screen with ShotIt", "--pin");
            }
            else
            {
                using var shell = _root.OpenSubKey(ImageShellPath, writable: true);
                shell?.DeleteSubKeyTree(EditVerb, throwOnMissingSubKey: false);
                shell?.DeleteSubKeyTree(PinVerb, throwOnMissingSubKey: false);
            }

            return true;
        }
        catch (Exception ex) when (IsAccessProblem(ex))
        {
            return false;
        }
    }

    private void WriteVerb(string verb, string label, string argument)
    {
        using var key = _root.CreateSubKey(ImageShellPath + "\\" + verb, writable: true);
        key.SetValue("MUIVerb", label, RegistryValueKind.String);
        key.SetValue("Icon", Quote(_executable) + ",0", RegistryValueKind.String);
        using var command = key.CreateSubKey("command", writable: true);
        // "%1" is quoted by us; Explorer substitutes the path without interpreting it.
        command.SetValue(null, Quote(_executable) + " " + argument + " \"%1\"", RegistryValueKind.String);
    }

    // ------------------------------------------------------------------ Send to

    private string SendToPath => Path.Combine(_sendToFolder, SendToFileName);

    public bool IsSendToInstalled() => _sendToFolder.Length > 0 && File.Exists(SendToPath);

    public bool SetSendTo(bool enabled)
    {
        if (_sendToFolder.Length == 0) return false;
        try
        {
            if (!enabled)
            {
                File.Delete(SendToPath);
                return true;
            }

            Directory.CreateDirectory(_sendToFolder);
            return Shortcut.Create(SendToPath, _executable, "--edit", "Open in the ShotIt editor");
        }
        catch (Exception ex) when (IsAccessProblem(ex))
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ uninstall

    /// <summary>Removes every registration. Safe to call when nothing is registered.</summary>
    public void RemoveAll()
    {
        SetStartup(false);
        SetContextMenu(false);
        SetSendTo(false);
        try
        {
            using var approved = _root.OpenSubKey(ApprovedPath, writable: true);
            approved?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex) when (IsAccessProblem(ex))
        {
        }
    }

    internal static bool IsAccessProblem(Exception ex) =>
        ex is SecurityException or UnauthorizedAccessException or IOException or ArgumentException or COMException or ObjectDisposedException;
}

/// <summary>Creates .lnk shortcut files through the Windows Script Host automation object.</summary>
public static class Shortcut
{
    public static bool Create(string shortcutPath, string target, string arguments, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        if (type is null) return false;
        object? shell = null, link = null;
        try
        {
            shell = Activator.CreateInstance(type);
            if (shell is null) return false;
            link = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, [shortcutPath], System.Globalization.CultureInfo.InvariantCulture);
            if (link is null) return false;
            var linkType = link.GetType();
            void Set(string name, object value) =>
                linkType.InvokeMember(name, System.Reflection.BindingFlags.SetProperty, null, link, [value], System.Globalization.CultureInfo.InvariantCulture);
            Set("TargetPath", target);
            Set("Arguments", arguments);
            Set("Description", description);
            Set("IconLocation", target + ",0");
            Set("WorkingDirectory", Path.GetDirectoryName(target) ?? string.Empty);
            linkType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, link, null, System.Globalization.CultureInfo.InvariantCulture);
            return File.Exists(shortcutPath);
        }
        catch (Exception ex) when (ex is COMException or System.Reflection.TargetInvocationException or MissingMethodException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }
    }
}

/// <summary>
/// Windows 11 can open Snipping Tool when Print Screen is pressed. While that is on, Print Screen
/// may start Snipping Tool instead of (or as well as) ShotIt.
/// </summary>
public sealed class PrintScreenBinding
{
    private const string KeyboardPath = @"Control Panel\Keyboard";
    private const string SnippingValue = "PrintScreenKeyForSnippingEnabled";
    private readonly RegistryKey _root;
    private readonly int _build;

    public PrintScreenBinding(RegistryKey? root = null, int? osBuild = null)
    {
        _root = root ?? Registry.CurrentUser;
        _build = osBuild ?? Environment.OSVersion.Version.Build;
    }

    /// <summary>True when Windows is set to open Snipping Tool on Print Screen.</summary>
    public bool IsSnippingToolBound()
    {
        try
        {
            using var key = _root.OpenSubKey(KeyboardPath);
            if (key?.GetValue(SnippingValue) is int value) return value != 0;
            // Without an explicit choice, Windows 11 from 22H2 Moment 2 onwards turns it on by default.
            return _build >= 22621;
        }
        catch (Exception ex) when (ShellIntegration.IsAccessProblem(ex))
        {
            return false;
        }
    }

    /// <summary>Turns the Windows setting off so Print Screen belongs to ShotIt. Fully applies after signing out and in.</summary>
    public bool ReleaseForShotIt() => Write(0);

    /// <summary>Gives Print Screen back to Snipping Tool (used when ShotIt is uninstalled or the user changes their mind).</summary>
    public bool RestoreSnippingTool() => Write(1);

    private bool Write(int value)
    {
        try
        {
            using var key = _root.CreateSubKey(KeyboardPath, writable: true);
            key.SetValue(SnippingValue, value, RegistryValueKind.DWord);
            return true;
        }
        catch (Exception ex) when (ShellIntegration.IsAccessProblem(ex))
        {
            return false;
        }
    }
}
