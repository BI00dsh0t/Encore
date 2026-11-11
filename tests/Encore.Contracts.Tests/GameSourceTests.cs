using System.Text.Json;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Contracts.Tests;

/// <summary>A game's source is additive: documents and messages from before it read unchanged.</summary>
public sealed class GameSourceTests
{
    [Fact]
    public void AnOldProfileAndAnOldDetectedGameReadWithNoSourceAndANewProfileKeepsIt()
    {
        const string oldDocument = """
            { "schemaVersion": 1, "revision": 3, "global": {}, "profiles": { "c:\\game.exe": { "name": "Game" } } }
            """;
        var json = SettingsJsonContext.Default;
        var document = SettingsMaps.WithOrdinalKeys(JsonSerializer.Deserialize(oldDocument, json.SettingsDocument)!);
        Assert.Null(document.Profiles[@"c:\game.exe"].Source);
        Assert.DoesNotContain("source", JsonSerializer.Serialize(document, json.SettingsDocument), StringComparison.Ordinal);

        const string oldDetected = """{ "profileId": "c:\\game.exe", "name": "Game", "processId": 7 }""";
        var detected = JsonSerializer.Deserialize(oldDetected, GameJsonContext.Default.DetectedGame);
        Assert.Equal(new DetectedGame(@"c:\game.exe", "Game", 7), detected);

        var written = JsonSerializer.Serialize(new GameProfile { Name = "Game", Source = GameSources.Steam }, json.GameProfile);
        Assert.Equal(GameSources.Steam, JsonSerializer.Deserialize(written, json.GameProfile)!.Source);
    }
}
