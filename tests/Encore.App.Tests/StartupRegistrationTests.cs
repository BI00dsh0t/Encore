using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using Encore.Contracts;
using Encore.Contracts.Settings;
using Encore.Platform.Desktop;

namespace Encore.App.Tests;

/// <summary>Runs against a throwaway HKCU key, never the real Run key.</summary>
public sealed class StartupRegistrationTests : IDisposable
{
    private const string AppPath = @"C:\Users\u\AppData\Local\Programs\Encore\Encore.App.exe";
    private const string Command = $"\"{AppPath}\" --tray";

    private static readonly Instance Installed = new(Instance.InstalledName, @"C:\roaming\Encore", @"C:\local\Encore");
    private static readonly Instance Dev = new(Instance.DevName, @"C:\roaming\Encore-Dev", @"C:\local\Encore-Dev");

    private readonly string _root = $@"Software\Encore-Test-{Guid.NewGuid():N}";
    private readonly RunKey _key;

    public StartupRegistrationTests() => _key = new RunKey($@"{_root}\Run", "Encore");

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);

    [Fact]
    public void TheCommandIsTheQuotedAppPathAndTray() =>
        Assert.Equal(Command, new StartupRegistration(Installed, _key, AppPath).Command);

    [Fact]
    public void TurningOnWritesTheRunValue()
    {
        var startup = new StartupRegistration(Installed, _key, AppPath);
        startup.Sync(Document(1, false));
        Assert.Null(_key.Read());

        startup.Sync(Document(2, true));

        Assert.Equal(Command, _key.Read());
    }

    [Fact]
    public void TurningOffDeletesTheRunValue()
    {
        var startup = new StartupRegistration(Installed, _key, AppPath);
        startup.Sync(Document(1, true));
        Assert.Equal(Command, _key.Read());

        startup.Sync(Document(2, false));

        Assert.Null(_key.Read());
    }

    [Fact]
    public void StartingWithTheSettingOnWritesAMissingValue()
    {
        new StartupRegistration(Installed, _key, AppPath).Sync(Document(5, true));

        Assert.Equal(Command, _key.Read());
    }

    [Fact]
    public void StartingWithTheSettingOnFixesAValueThatPointsElsewhere()
    {
        _key.Write(@"""C:\Old\Encore.App.exe"" --tray");

        new StartupRegistration(Installed, _key, AppPath).Sync(Document(5, true));

        Assert.Equal(Command, _key.Read());
    }

    [Fact]
    public void StartingWithTheSettingOffDeletesAStaleValue()
    {
        _key.Write(Command);

        new StartupRegistration(Installed, _key, AppPath).Sync(Document(5, false));

        Assert.Null(_key.Read());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ADevInstanceLeavesAPresetValueUntouched(bool on)
    {
        _key.Write("preset");
        var startup = new StartupRegistration(Dev, _key, AppPath);

        startup.Sync(Document(1, on));
        startup.Sync(Document(2, !on));

        Assert.False(startup.Applies);
        Assert.Equal("preset", _key.Read());
    }

    [Fact]
    public void AnUnchangedValueIsNotRewritten()
    {
        var startup = new StartupRegistration(Installed, _key, AppPath);
        startup.Sync(Document(1, true));
        _key.Write("changed elsewhere");

        // Another committed update, such as a different key, with startWithWindows still on.
        startup.Sync(Document(2, true));

        Assert.Equal("changed elsewhere", _key.Read());
    }

    [Fact]
    public void ARefusedUpdateLeavesTheValueAlone()
    {
        var startup = new StartupRegistration(Installed, _key, AppPath);
        var committed = Document(3, false);
        startup.Sync(committed);

        // The toggle flipped on, the engine refused it, and the committed document is unchanged.
        startup.Sync(committed);

        Assert.Null(_key.Read());
    }

    [Fact]
    public void AFailedWriteIsSwallowedAndRetriedOnTheNextCommit()
    {
        _key.Write("old");
        var user = WindowsIdentity.GetCurrent().User!;
        var deny = new RegistryAccessRule(user, RegistryRights.SetValue, AccessControlType.Deny);
        using (var run = Registry.CurrentUser.OpenSubKey(_key.SubKey, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadKey)!)
        {
            var security = run.GetAccessControl();
            security.AddAccessRule(deny);
            run.SetAccessControl(security);
        }

        var startup = new StartupRegistration(Installed, _key, AppPath);
        try
        {
            startup.Sync(Document(1, true));
            Assert.Equal("old", _key.Read());
        }
        finally
        {
            using var run = Registry.CurrentUser.OpenSubKey(_key.SubKey, RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions | RegistryRights.ReadKey)!;
            var security = run.GetAccessControl();
            security.RemoveAccessRule(deny);
            run.SetAccessControl(security);
        }

        // A heartbeat re-raises the same failed revision: no new attempt, though the write would now work.
        startup.Sync(Document(1, true));
        Assert.Equal("old", _key.Read());

        startup.Sync(Document(2, true));
        Assert.Equal(Command, _key.Read());
    }

    private static SettingsDocument Document(long revision, bool startWithWindows) => SettingsDocument.Empty with
    {
        Revision = revision,
        Global = SettingsMaps.EmptyValues.Add(Keys.StartWithWindows.Name, JsonSerializer.SerializeToElement(startWithWindows, SettingsJsonContext.Default.Boolean)),
    };
}
