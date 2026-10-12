using Microsoft.Win32;

namespace ShotIt.Shell;

/// <summary>
/// Restrictions an administrator can set through Group Policy, Intune or the MSI properties. They live
/// under Software\Policies\ShotIt; a machine-wide value wins over a per-user one, and ordinary users
/// cannot write either location's machine half.
/// </summary>
public sealed record AppPolicies
{
    public const string KeyPath = @"Software\Policies\ShotIt";

    public static readonly AppPolicies None = new();

    /// <summary>Uploading, image search and every other action that sends a capture over the network is blocked.</summary>
    public bool DisableUpload { get; init; }

    /// <summary>ShotIt never checks for or installs updates (the administrator deploys them).</summary>
    public bool DisableUpdates { get; init; }

    /// <summary>No capture is recorded in history.</summary>
    public bool DisableHistory { get; init; }

    /// <summary>Replaces the built-in update address, for organisations that mirror releases internally.</summary>
    public string UpdateFeedUrl { get; init; } = string.Empty;

    public bool Any => DisableUpload || DisableUpdates || DisableHistory || UpdateFeedUrl.Length > 0;

    /// <summary>Reads the policies. The parameters exist so tests can point at scratch keys.</summary>
    public static AppPolicies Read(RegistryKey? machineRoot = null, RegistryKey? userRoot = null)
    {
        RegistryKey? machine = null, user = null;
        try
        {
            machine = TryOpen(machineRoot ?? Registry.LocalMachine);
            user = TryOpen(userRoot ?? Registry.CurrentUser);
            if (machine is null && user is null) return None;
            return new AppPolicies
            {
                DisableUpload = Flag(machine, user, "DisableUpload"),
                DisableUpdates = Flag(machine, user, "DisableUpdates"),
                DisableHistory = Flag(machine, user, "DisableHistory"),
                UpdateFeedUrl = Text(machine, user, "UpdateFeedUrl"),
            };
        }
        finally
        {
            machine?.Dispose();
            user?.Dispose();
        }
    }

    private static RegistryKey? TryOpen(RegistryKey root)
    {
        try
        {
            return root.OpenSubKey(KeyPath);
        }
        catch (Exception ex) when (ShellIntegration.IsAccessProblem(ex))
        {
            return null;
        }
    }

    private static object? Value(RegistryKey? machine, RegistryKey? user, string name)
    {
        try
        {
            return machine?.GetValue(name) ?? user?.GetValue(name);
        }
        catch (Exception ex) when (ShellIntegration.IsAccessProblem(ex))
        {
            return null;
        }
    }

    private static bool Flag(RegistryKey? machine, RegistryKey? user, string name) => Value(machine, user, name) switch
    {
        int number => number != 0,
        long number => number != 0,
        string text => text.Trim() is "1" or "true" or "True" or "TRUE",
        _ => false,
    };

    private static string Text(RegistryKey? machine, RegistryKey? user, string name) =>
        Value(machine, user, name) is string text ? text.Trim() : string.Empty;
}
