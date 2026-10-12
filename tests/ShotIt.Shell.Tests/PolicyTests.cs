using Microsoft.Win32;
using ShotIt.Shell;

namespace ShotIt.Shell.Tests;

public class PolicyTests
{
    private static void Set(RegistryKey root, string name, object value, RegistryValueKind kind)
    {
        using var key = root.CreateSubKey(AppPolicies.KeyPath, writable: true);
        key.SetValue(name, value, kind);
    }

    [Fact]
    public void NoPolicyKeyMeansNoRestrictions()
    {
        using var machine = new Sandbox();
        using var user = new Sandbox();
        var policies = AppPolicies.Read(machine.Root, user.Root);
        Assert.Equal(AppPolicies.None, policies);
        Assert.False(policies.Any);
    }

    [Fact]
    public void MachineValuesAreRead()
    {
        using var machine = new Sandbox();
        using var user = new Sandbox();
        Set(machine.Root, "DisableUpload", 1, RegistryValueKind.DWord);
        Set(machine.Root, "DisableUpdates", 1, RegistryValueKind.DWord);
        Set(machine.Root, "DisableHistory", 0, RegistryValueKind.DWord);
        Set(machine.Root, "UpdateFeedUrl", " https://updates.corp.example/shotit ", RegistryValueKind.String);
        var policies = AppPolicies.Read(machine.Root, user.Root);
        Assert.True(policies.DisableUpload);
        Assert.True(policies.DisableUpdates);
        Assert.False(policies.DisableHistory);
        Assert.Equal("https://updates.corp.example/shotit", policies.UpdateFeedUrl);
        Assert.True(policies.Any);
    }

    [Fact]
    public void UserValuesApplyWhenTheMachineIsSilent()
    {
        using var machine = new Sandbox();
        using var user = new Sandbox();
        Set(user.Root, "DisableHistory", 1, RegistryValueKind.DWord);
        Assert.True(AppPolicies.Read(machine.Root, user.Root).DisableHistory);
    }

    [Fact]
    public void AMachineValueCannotBeOverriddenByTheUser()
    {
        using var machine = new Sandbox();
        using var user = new Sandbox();
        Set(machine.Root, "DisableUpload", 1, RegistryValueKind.DWord);
        Set(user.Root, "DisableUpload", 0, RegistryValueKind.DWord);
        Assert.True(AppPolicies.Read(machine.Root, user.Root).DisableUpload);

        Set(machine.Root, "DisableUpdates", 0, RegistryValueKind.DWord);
        Set(user.Root, "DisableUpdates", 1, RegistryValueKind.DWord);
        Assert.False(AppPolicies.Read(machine.Root, user.Root).DisableUpdates);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("0", false)]
    [InlineData("yes please", false)]
    [InlineData("", false)]
    public void TextValuesWrittenByMsiPropertiesAreUnderstood(string text, bool expected)
    {
        using var machine = new Sandbox();
        using var user = new Sandbox();
        Set(machine.Root, "DisableUpload", text, RegistryValueKind.String);
        Assert.Equal(expected, AppPolicies.Read(machine.Root, user.Root).DisableUpload);
    }

    [Fact]
    public void ValuesOfTheWrongTypeAreIgnored()
    {
        using var machine = new Sandbox();
        using var user = new Sandbox();
        Set(machine.Root, "DisableUpload", new byte[] { 1 }, RegistryValueKind.Binary);
        Set(machine.Root, "UpdateFeedUrl", 5, RegistryValueKind.DWord);
        var policies = AppPolicies.Read(machine.Root, user.Root);
        Assert.False(policies.DisableUpload);
        Assert.Equal(string.Empty, policies.UpdateFeedUrl);
    }
}
