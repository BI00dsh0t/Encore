using Encore.Contracts.Settings;
using Encore.Engine.Settings;

namespace Encore.Engine.Tests;

public sealed class ProfileMatchTests
{
    [Fact]
    public void AStorePathWithANewVersionFindsTheSameProfile()
    {
        const string old = @"c:\program files\windowsapps\contoso.game_1.0.0.0_x64__abc123\game\game.exe";
        const string updated = @"c:\program files\windowsapps\contoso.game_1.1.0.0_x64__abc123\game\game.exe";
        const string elsewhere = @"d:\games\other\other.exe";
        var document = SettingsDocument.Empty with
        {
            Profiles = SettingsMaps.EmptyProfiles.Add(old, new GameProfile { Name = "Contoso Game", Ignored = true }),
        };

        Assert.Equal(old, ProfileMatch.Resolve(document, updated));
        Assert.Equal(old, ProfileMatch.Resolve(document, old));
        Assert.Equal(elsewhere, ProfileMatch.Resolve(document, elsewhere));
    }
}
