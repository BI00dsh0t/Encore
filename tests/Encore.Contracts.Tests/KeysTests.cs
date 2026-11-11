using System.Text.Json;
using System.Text.RegularExpressions;
using Encore.Contracts.Settings;

namespace Encore.Contracts.Tests;

public sealed partial class KeysTests
{
    [Fact]
    public void TheCatalogIsTheAd4KeySet()
    {
        Assert.Equal(
            [
                "video", "trackLayout", "bufferWindow", "replayStorage", "ringPath", "saveCooldown", "outputFolder",
                "hotkeys", "recordHotkeyMode", "monitorRecordingMonitor", "feedbackSounds", "feedbackSoundDevice",
                "cleanup", "manualGames", "startWithWindows", "overlayDot", "appTheme",
                "monitorRecordingScreens", "captureNewGames",
            ],
            Keys.All.Select(k => k.Name));
    }

    [Fact]
    public void CaptureNewGamesIsGlobalOnlyAndOnByDefault()
    {
        // Today's behaviour unless the builder turns it off.
        Assert.True(Keys.CaptureNewGames.Default);
        Assert.False(Keys.CaptureNewGames.Overridable);
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
    public void AVideoValueWithoutMatchSourceReadsAsOffAndOneWithItRoundTrips()
    {
        // An existing value has no matchSource, and writes back without one.
        using var old = JsonDocument.Parse("""{"codec":"h264","width":1920,"height":1080,"fps":60,"bitrateKbps":40000,"rateControl":"cbr"}""");
        Assert.True(Keys.Video.TryRead(old.RootElement, out var read));
        Assert.False(read.MatchSource);
        Assert.Equal(old.RootElement.GetRawText(), Keys.Video.ToJson(read).GetRawText());

        var on = Keys.Video.Default with { MatchSource = true };
        Assert.Contains("\"matchSource\":true", Keys.Video.ToJson(on).GetRawText(), StringComparison.Ordinal);
        Assert.True(Keys.Video.TryRead(Keys.Video.ToJson(on), out var back));
        Assert.Equal(on, back);
    }

    [Fact]
    public void ValuesAreCamelCaseWithEnumsAsCamelCaseStrings()
    {
        Assert.Equal(
            """{"codec":"h264","width":1920,"height":1080,"fps":60,"bitrateKbps":40000,"rateControl":"cbr"}""",
            Keys.Video.DefaultJson.GetRawText());
        Assert.Equal("\"nextSession\"", JsonSerializer.Serialize(AppliesAt.NextSession, SettingsJsonContext.Default.AppliesAt));
    }

    [Fact]
    public void AppThemeIsGlobalImmediateFluentByDefaultAndWritesGamingDarkCamelCase()
    {
        Assert.Equal(AppTheme.Fluent, Keys.AppTheme.Default);
        Assert.False(Keys.AppTheme.Overridable);
        Assert.Equal(AppliesAt.Immediate, Keys.AppTheme.AppliesAt);
        Assert.Equal("\"gamingDark\"", Keys.AppTheme.ToJson(AppTheme.GamingDark).GetRawText());
    }

    [Theory]
    [InlineData("video", """{"codec":"h264","width":1920,"height":1080,"fps":121,"bitrateKbps":40000,"rateControl":"cbr"}""")]
    [InlineData("video", """{"codec":"h264","width":1921,"height":1080,"fps":60,"bitrateKbps":40000,"rateControl":"cbr"}""")]
    [InlineData("ringPath", "\"relative\\\\ring\"")]
    [InlineData("manualGames", """["c:\\games\\a.exe","C:\\Games\\A.exe"]""")]
    [InlineData("monitorRecordingScreens", """{"mode":"selected","monitors":[]}""")]
    [InlineData("monitorRecordingScreens", """{"mode":"selected"}""")]
    [InlineData("monitorRecordingScreens", """{"mode":"selected","monitors":["a","A"]}""")]
    [InlineData("monitorRecordingScreens", """{"mode":"all","monitors":["a"]}""")]
    public void BadValuesAreRefused(string name, string json)
    {
        Assert.True(Keys.TryGet(name, out var key));
        using var parsed = JsonDocument.Parse(json);

        Assert.False(key.IsValid(parsed.RootElement));
    }

    [Theory]
    [InlineData("ringPath", "null")]
    [InlineData("ringPath", "\"D:\\\\ring\"")]
    [InlineData("monitorRecordingScreens", "null")]
    [InlineData("monitorRecordingScreens", """{"mode":"all"}""")]
    [InlineData("monitorRecordingScreens", """{"mode":"selected","monitors":["\\\\?\\DISPLAY#SAM7177#1","\\\\.\\DISPLAY2"]}""")]
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

    [Fact]
    public void HotkeysWithoutTheMonitorStartAndStopReadThemAsUnboundAndRoundTrip()
    {
        using var old = JsonDocument.Parse(
            """{"save":{"vk":121,"modifiers":1},"micMute":null,"recordToggle":null,"recordStart":null,"recordStop":null,"monitorRecordToggle":{"vk":120,"modifiers":5}}""");

        Assert.True(Keys.Hotkeys.TryRead(old.RootElement, out var read));
        Assert.Equal(new HotkeyBinding(120, HotkeyModifiers.Alt | HotkeyModifiers.Shift), read.MonitorRecordToggle);
        Assert.Null(read.MonitorRecordStart);
        Assert.Null(read.MonitorRecordStop);

        var bound = read with { MonitorRecordStart = new HotkeyBinding(0x41, HotkeyModifiers.Win), MonitorRecordStop = new HotkeyBinding(0x42, HotkeyModifiers.Win) };
        Assert.True(Keys.Hotkeys.TryRead(Keys.Hotkeys.ToJson(bound), out var again));
        Assert.Equal(bound, again);
        Assert.Equal(bound.MonitorRecordStop, again.For(HotkeyAction.MonitorRecordStop));
    }

    [GeneratedRegex("^[a-z][a-zA-Z0-9]*$")]
    private static partial Regex CamelCase();
}
