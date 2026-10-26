using System.Text.Json;
using Microsoft.Win32;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Tests;

/// <summary>Runs against a throwaway HKCU key, never the real Run key.</summary>
public sealed class StartupRegistrationTests : IDisposable
{
    private const string AppPath = @"C:\Users\u\AppData\Local\Programs\ReplayBuffer\ReplayBuffer.App.exe";
    private const string Command = $"\"{AppPath}\" --tray";

    private static readonly Instance Installed = new(Instance.InstalledName, @"C:\roaming\ReplayBuffer", @"C:\local\ReplayBuffer");
    private static readonly Instance Dev = new(Instance.DevName, @"C:\roaming\ReplayBuffer-Dev", @"C:\local\ReplayBuffer-Dev");

    private readonly string _root = $@"Software\ReplayBuffer-Test-{Guid.NewGuid():N}";
    private readonly RunKey _key;

    public StartupRegistrationTests() => _key = new RunKey($@"{_root}\Run", "ReplayBuffer");

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
        _key.Write(@"""C:\Old\ReplayBuffer.App.exe"" --tray");

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

    [Fact]
    public void StartingWithTheSettingOffAndNoValueIsFine()
    {
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

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ATestInstanceLeavesAPresetValueUntouched(bool on)
    {
        _key.Write("preset");
        var startup = new StartupRegistration(Instance.ForTest("ReplayBuffer-Test-startup"), _key, AppPath);

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
    public void AnUnchangedOffIsNotDeletedAgain()
    {
        var startup = new StartupRegistration(Installed, _key, AppPath);
        startup.Sync(Document(1, false));
        _key.Write("written elsewhere");

        startup.Sync(Document(2, false));

        Assert.Equal("written elsewhere", _key.Read());
    }

    [Fact]
    public void AMissingSettingIsTheDefaultOff()
    {
        _key.Write(Command);

        new StartupRegistration(Installed, _key, AppPath).Sync(SettingsDocument.Empty);

        Assert.False(Keys.StartWithWindows.Default);
        Assert.Null(_key.Read());
    }

    private static SettingsDocument Document(long revision, bool startWithWindows) => SettingsDocument.Empty with
    {
        Revision = revision,
        Global = SettingsMaps.EmptyValues.Add(Keys.StartWithWindows.Name, JsonSerializer.SerializeToElement(startWithWindows, SettingsJsonContext.Default.Boolean)),
    };
}
