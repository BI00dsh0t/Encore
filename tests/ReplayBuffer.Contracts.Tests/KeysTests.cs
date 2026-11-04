using System.Text.Json;
using System.Text.RegularExpressions;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Contracts.Tests;

public sealed partial class KeysTests
{
    [Fact]
    public void TheCatalogIsTheAd4KeySet()
    {
        Assert.Equal(
            [
                "video", "trackLayout", "bufferWindow", "replayStorage", "ringPath", "saveCooldown", "outputFolder",
                "hotkeys", "recordHotkeyMode", "monitorRecordingMonitor", "feedbackSounds", "feedbackSoundDevice",
                "cleanup", "manualGames", "startWithWindows", "overlayDot",
            ],
            Keys.All.Select(k => k.Name));
    }

    [Fact]
    public void NamesAreUniqueAndCamelCase()
    {
        Assert.Equal(Keys.All.Count, Keys.All.Select(k => k.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(Keys.All, k => Assert.Matches(CamelCase(), k.Name));
    }

    [Fact]
    public void EveryDefaultIsPresentValidAndRoundTrips()
    {
        Assert.All(Keys.All, key =>
        {
            Assert.NotEqual(JsonValueKind.Undefined, key.DefaultJson.ValueKind);
            Assert.True(key.TryNormalize(key.DefaultJson, out var normalized), key.Name);
            Assert.Equal(key.DefaultJson.GetRawText(), normalized.GetRawText());
        });
    }

    [Fact]
    public void OnlyTheThreeMediaKeysAreOverridable()
    {
        Assert.Equal(
            ["bufferWindow", "trackLayout", "video"],
            Keys.All.Where(k => k.Overridable).Select(k => k.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ProfileAttributeNamesAreNotKeyNames()
    {
        Assert.Empty(ProfileAttributes.All.Intersect(Keys.All.Select(k => k.Name), StringComparer.Ordinal));
        Assert.All(ProfileAttributes.All, a => Assert.False(Keys.TryGet(a, out _)));
    }

    [Fact]
    public void TryGetFindsEveryKeyByExactName()
    {
        Assert.All(Keys.All, k => Assert.Same(k, Keys.TryGet(k.Name, out var found) ? found : null));
        Assert.False(Keys.TryGet("Video", out _));
    }

    [Fact]
    public void TheDecidedDefaults()
    {
        Assert.Equal(new VideoSettings(VideoCodec.H264, 1920, 1080, 60, 40_000, RateControl.Cbr), Keys.Video.Default);
        Assert.Equal(300, Keys.BufferWindow.Default);
        Assert.Equal(5, Keys.SaveCooldown.Default);
        Assert.Equal(new HotkeyBinding(0x79, HotkeyModifiers.Alt), Keys.Hotkeys.Default.Save);
        Assert.Equal(new HotkeyBinding(0x78, HotkeyModifiers.Alt), Keys.Hotkeys.Default.RecordToggle);
        Assert.Equal(new HotkeyBinding(0x77, HotkeyModifiers.Alt), Keys.Hotkeys.Default.MicMute);
        Assert.Equal(new HotkeyBinding(0x78, HotkeyModifiers.Alt | HotkeyModifiers.Shift), Keys.Hotkeys.Default.MonitorRecordToggle);
        Assert.Null(Keys.Hotkeys.Default.RecordStart);
        Assert.Null(Keys.Hotkeys.Default.RecordStop);

        var track = Assert.Single(Keys.TrackLayout.Default.Tracks);
        Assert.Equal(Keys.EverythingTrackId, track.Id);
        Assert.True(track.MuteAffected);
        Assert.Empty(Keys.TrackLayout.Default.Devices);
    }

    [Fact]
    public void ValuesAreCamelCaseWithEnumsAsCamelCaseStrings()
    {
        Assert.Equal(
            """{"codec":"h264","width":1920,"height":1080,"fps":60,"bitrateKbps":40000,"rateControl":"cbr"}""",
            Keys.Video.DefaultJson.GetRawText());
        Assert.Equal("\"nextSession\"", JsonSerializer.Serialize(AppliesAt.NextSession, SettingsJsonContext.Default.AppliesAt));
    }

    [Theory]
    [InlineData("video", """{"codec":"h264","width":1920,"height":1080,"fps":121,"bitrateKbps":40000,"rateControl":"cbr"}""")]
    [InlineData("video", """{"codec":"h264","width":1921,"height":1080,"fps":60,"bitrateKbps":40000,"rateControl":"cbr"}""")]
    [InlineData("ringPath", "\"relative\\\\ring\"")]
    [InlineData("manualGames", """["c:\\games\\a.exe","C:\\Games\\A.exe"]""")]
    public void BadValuesAreRefused(string name, string json)
    {
        Assert.True(Keys.TryGet(name, out var key));
        using var parsed = JsonDocument.Parse(json);

        Assert.False(key.IsValid(parsed.RootElement));
    }

    [Theory]
    [InlineData("ringPath", "null")]
    [InlineData("ringPath", "\"D:\\\\ring\"")]
    public void GoodValuesAreAccepted(string name, string json)
    {
        Assert.True(Keys.TryGet(name, out var key));
        using var parsed = JsonDocument.Parse(json);

        Assert.True(key.IsValid(parsed.RootElement));
    }

    [Fact]
    public void NormalizingWritesEnumsInCanonicalCase()
    {
        using var parsed = JsonDocument.Parse("""{"codec":"HEVC","width":2560,"height":1440,"fps":120,"bitrateKbps":80000,"rateControl":"VBR"}""");

        Assert.True(Keys.Video.TryNormalize(parsed.RootElement, out var normalized));
        Assert.Equal("""{"codec":"hevc","width":2560,"height":1440,"fps":120,"bitrateKbps":80000,"rateControl":"vbr"}""", normalized.GetRawText());
    }

    [GeneratedRegex("^[a-z][a-zA-Z0-9]*$")]
    private static partial Regex CamelCase();
}
