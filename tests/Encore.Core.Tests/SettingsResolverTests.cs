using System.Collections.Immutable;
using System.Text.Json;
using Encore.Contracts.Settings;
using Encore.Core.Settings;

namespace Encore.Core.Tests;

public sealed class SettingsResolverTests
{
    private const string GameId = @"c:\games\game.exe";

    private static readonly VideoSettings ProfileVideo = new(VideoCodec.Hevc, 2560, 1440, 120, 80_000, RateControl.Vbr);
    private static readonly VideoSettings GlobalVideo = new(VideoCodec.Av1, 1280, 720, 30, 10_000, RateControl.Cbr);

    [Fact]
    public void AnEmptyDocumentResolvesToTheCatalogDefaults()
    {
        var effective = SettingsResolver.Resolve(SettingsDocument.Empty, null);

        Assert.Null(effective.ProfileId);
        Assert.Equal(0, effective.SettingsRevision);
        AssertDefaults(effective, except: []);
    }

    [Fact]
    public void AProfileOverrideWinsAndEveryOtherKeyIsGlobalOrDefault()
    {
        var document = Document(
            global: Values((Keys.BufferWindow.Name, Keys.BufferWindow.ToJson(120)), (Keys.Video.Name, Keys.Video.ToJson(GlobalVideo))),
            profile: Values((Keys.Video.Name, Keys.Video.ToJson(ProfileVideo))));

        var effective = SettingsResolver.Resolve(document, GameId);

        Assert.Equal(GameId, effective.ProfileId);
        Assert.Equal(7, effective.SettingsRevision);
        Assert.Equal(ProfileVideo, effective.Video);
        Assert.Equal(120, effective.BufferWindow);
        AssertDefaults(effective, except: ["video", "bufferWindow"]);
    }

    [Fact]
    public void WithoutAProfileTheGlobalValuesApply()
    {
        var document = Document(
            global: Values((Keys.Video.Name, Keys.Video.ToJson(GlobalVideo))),
            profile: Values((Keys.Video.Name, Keys.Video.ToJson(ProfileVideo))));

        Assert.Equal(GlobalVideo, SettingsResolver.Resolve(document, null).Video);
        Assert.Equal(GlobalVideo, SettingsResolver.Resolve(document, @"c:\games\other.exe").Video);
    }

    [Fact]
    public void AnOverrideOfAGlobalOnlyKeyIsIgnored()
    {
        var document = Document(
            global: Values((Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(10))),
            profile: Values((Keys.SaveCooldown.Name, Keys.SaveCooldown.ToJson(1))));

        Assert.Equal(10, SettingsResolver.Resolve(document, GameId).SaveCooldown);
    }

    [Fact]
    public void ABadStoredValueFallsBackToTheNextLayer()
    {
        var document = Document(
            global: Values((Keys.Video.Name, Keys.Video.ToJson(GlobalVideo)), (Keys.BufferWindow.Name, Json("\"long\"")), (Keys.ReplayStorage.Name, Json("\"tape\""))),
            profile: Values((Keys.Video.Name, Json("""{"codec":"h264"}""")), (Keys.BufferWindow.Name, Json("0"))));

        var effective = SettingsResolver.Resolve(document, GameId);

        Assert.Equal(GlobalVideo, effective.Video);
        Assert.Equal(Keys.BufferWindow.Default, effective.BufferWindow);
        Assert.Equal(Keys.ReplayStorage.Default, effective.ReplayStorage);
    }

    [Fact]
    public void UnknownKeysAreIgnored()
    {
        var document = Document(global: Values(("futureKey", Json("42"))), profile: Values(("futureKey", Json("1"))));

        AssertDefaults(SettingsResolver.Resolve(document, GameId), except: []);
    }

    private static void AssertDefaults(EffectiveSettings effective, string[] except)
    {
        var defaults = SettingsResolver.Resolve(SettingsDocument.Empty, null);
        var actual = JsonSerializer.SerializeToElement(effective, SettingsJsonContext.Default.EffectiveSettings);
        var expected = JsonSerializer.SerializeToElement(defaults, SettingsJsonContext.Default.EffectiveSettings);

        foreach (var key in Keys.All.Where(k => !except.Contains(k.Name)))
        {
            Assert.Equal(key.DefaultJson.GetRawText(), expected.GetProperty(key.Name).GetRawText());
            Assert.Equal(key.DefaultJson.GetRawText(), actual.GetProperty(key.Name).GetRawText());
        }
    }

    private static SettingsDocument Document(
        ImmutableSortedDictionary<string, JsonElement> global,
        ImmutableSortedDictionary<string, JsonElement> profile) => new()
        {
            Revision = 7,
            Global = global,
            Profiles = SettingsMaps.EmptyProfiles.Add(GameId, new GameProfile { Name = "Game", Overrides = profile }),
        };

    private static ImmutableSortedDictionary<string, JsonElement> Values(params (string Key, JsonElement Value)[] values) =>
        values.Aggregate(SettingsMaps.EmptyValues, (map, v) => map.SetItem(v.Key, v.Value));

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
