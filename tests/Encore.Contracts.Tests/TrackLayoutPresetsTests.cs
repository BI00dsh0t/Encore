using System.Text.Json;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.Contracts.Tests;

/// <summary>Story 10.4: the presets' shape from a fabricated device list, and <c>isDefault</c> on the wire.</summary>
public sealed class TrackLayoutPresetsTests
{
    private static readonly AudioDevice Speakers = new("{out-1}", "Speakers", "Realtek", AudioFlow.Output, true, IsDefault: true);
    private static readonly AudioDevice Headset = new("{out-2}", "Headset", "Arctis", AudioFlow.Output, true);
    private static readonly AudioDevice Yeti = new("{in-1}", "Microphone", "Yeti", AudioFlow.Input, true, IsDefault: true);
    private static readonly AudioDevice[] Devices = [Speakers, Headset, Yeti];

    [Fact]
    public void SimpleBuildsOneEverythingTrackFromTheDefaultsKeepingTrackOnesIdAndAHeldDevice()
    {
        var held = new LayoutDevice(Guid.NewGuid(), "{OUT-1}", "Speakers", "Realtek", AudioFlow.Output, IsMic: false);
        var other = new LayoutDevice(Guid.NewGuid(), "{out-2}", "Headset", "Arctis", AudioFlow.Output, IsMic: false);
        var firstId = Guid.NewGuid();
        var current = new TrackLayout([held, other], [new Track(firstId, "Mine", [held.Key, other.Key], false), new Track(Guid.NewGuid(), "Two", [other.Key], false)]);

        var layout = TrackLayoutPresets.Build(TrackLayoutPreset.Simple, current, Devices);

        // The held default keeps its record; Headset, not a default, is dropped with its Track.
        Assert.Equal(held, layout.Devices[0]);
        Assert.Equal(("{in-1}", true), (layout.Devices[1].EndpointId, layout.Devices[1].IsMic));
        Assert.Equal(2, layout.Devices.Count);
        var track = Assert.Single(layout.Tracks);
        Assert.Equal((firstId, "Everything", true), (track.Id, track.Name, track.MuteAffected));
        Assert.Equal(layout.Devices.Select(d => d.Key), track.DeviceKeys);
        Assert.True(Keys.TrackLayout.IsValid(Keys.TrackLayout.ToJson(layout)));
    }

    [Fact]
    public void ForEditingBuildsThreeTracksAndLeavesOutAMissingDefault()
    {
        var layout = TrackLayoutPresets.Build(TrackLayoutPreset.ForEditing, Keys.TrackLayout.Default, Devices);

        var output = layout.Devices.Single(d => d.Flow == AudioFlow.Output).Key;
        var input = layout.Devices.Single(d => d.Flow == AudioFlow.Input).Key;
        Assert.Equal(
            [(Keys.EverythingTrackId, "Everything", true), (layout.Tracks[1].Id, "Game and chat", false), (layout.Tracks[2].Id, "Microphone", true)],
            layout.Tracks.Select(t => (t.Id, t.Name, t.MuteAffected)));
        Assert.Equal([[output, input], [output], [input]], layout.Tracks.Select(t => t.DeviceKeys.ToArray()));
        Assert.True(Keys.TrackLayout.IsValid(Keys.TrackLayout.ToJson(layout)));

        // No microphone: its source is left out, and the Microphone Track stays, silent.
        var noMic = TrackLayoutPresets.Build(TrackLayoutPreset.ForEditing, Keys.TrackLayout.Default, [Speakers, Yeti with { IsDefault = false }]);
        Assert.Equal([Speakers.EndpointId], noMic.Devices.Select(d => d.EndpointId));
        Assert.Equal([1, 1, 0], noMic.Tracks.Select(t => t.DeviceKeys.Count));
    }

    [Fact]
    public void IsDefaultIsWrittenOnlyWhenTrueAndReadsFalseWhenMissing()
    {
        var json = JsonSerializer.Serialize(new AudioDeviceList([Speakers, Headset]), AudioJsonContext.Default.AudioDeviceList);

        Assert.Contains("\"available\":true,\"isDefault\":true}", json, StringComparison.Ordinal);
        Assert.Contains("\"flow\":\"output\",\"available\":true}", json, StringComparison.Ordinal);
        Assert.Equal([Speakers, Headset], JsonSerializer.Deserialize(json, AudioJsonContext.Default.AudioDeviceList)!.Devices);
    }
}
