using System.Text.Json;
using Encore.Contracts.Settings;
using Encore.Engine.Settings;

namespace Encore.Engine.Tests;

public sealed class ProfileFolderStoreTests : IDisposable
{
    private const string GameId = @"c:\games\game.exe";

    private readonly string _folder = Directory.CreateTempSubdirectory("rb-settings-").FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void ACreatedProfileGetsAFolderThatPersistsAndSurvivesARename()
    {
        var store = SettingsStore.Open(_folder).Store;
        store.Update(new SettingsUpdate(0, [SettingsOp.SetProfile(GameId, ProfileAttributes.Name, Name("Halo: Reach"))]));
        Assert.Equal("Halo_ Reach", store.Current.Profiles[GameId].Folder);

        var reopened = SettingsStore.Open(_folder).Store;
        Assert.Equal("Halo_ Reach", reopened.Current.Profiles[GameId].Folder);

        reopened.Update(new SettingsUpdate(1, [SettingsOp.SetProfile(GameId, ProfileAttributes.Name, Name("Halo"))]));
        Assert.Equal("Halo", reopened.Current.Profiles[GameId].Name);
        Assert.Equal("Halo_ Reach", reopened.Current.Profiles[GameId].Folder);
    }

    [Fact]
    public void ALegacyProfileKeepsTheFolderItWasAllocatedWhenANewProfileTakesItsName()
    {
        File.WriteAllText(Path.Combine(_folder, SettingsStore.FileName),
            """{ "schemaVersion": 1, "revision": 1, "global": {}, "profiles": { "b": { "name": "Launcher", "ignored": false, "recordingsEnabled": false, "overrides": {} } } }""");
        var store = SettingsStore.Open(_folder).Store;

        store.Update(new SettingsUpdate(1, [SettingsOp.SetProfile("a", ProfileAttributes.Name, Name("Launcher"))]));

        Assert.Equal("Launcher", store.Current.Profiles["b"].Folder);
        Assert.Equal("Launcher (2)", store.Current.Profiles["a"].Folder);
    }

    private static JsonElement Name(string name) => JsonSerializer.SerializeToElement(name);
}
