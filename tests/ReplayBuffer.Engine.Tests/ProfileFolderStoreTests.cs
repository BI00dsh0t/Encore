using System.Text.Json;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Engine.Settings;

namespace ReplayBuffer.Engine.Tests;

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

    private static JsonElement Name(string name) => JsonSerializer.SerializeToElement(name);
}
