using Encore.App.Settings;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;

namespace Encore.App.Tests;

/// <summary>The Audio Tracks editor's draft model, its presets and its probe meters.</summary>
public sealed class AudioTracksDraftTests
{
    private static readonly AudioDevice Speakers = new("{out-1}", "Speakers", "Realtek", AudioFlow.Output, true, IsDefault: true);
    private static readonly AudioDevice Chat = new("{out-2}", "Chat", "BEACN", AudioFlow.Output, true);
    private static readonly AudioDevice Yeti = new("{in-1}", "Microphone", "Yeti", AudioFlow.Input, true, IsDefault: true);

    [Fact]
    public void ADeviceIsOneRecordAcrossTracksAndOneOnNoTrackIsDroppedAtSave()
    {
        var draft = AudioTracksDraft.From(Keys.TrackLayout.Default);
        var everything = draft.Tracks[0];
        var second = draft.AddTrack("Game and chat");

        var mic = draft.AddSource(everything, Yeti)!;
        var speakers = draft.AddSource(everything, Speakers)!;
        Assert.Equal(speakers, draft.AddSource(second, Speakers with { EndpointId = "{OUT-1}" }));
        draft.AddSource(second, Chat);
        Assert.Null(draft.AddSource(second, Chat));

        // An added input is a Mic Device; "Treat as a microphone" changes the one record.
        Assert.True(mic.IsMic);
        Assert.False(speakers.IsMic);
        draft.SetMic(speakers.Key, true);
        Assert.All(draft.Tracks.SelectMany(draft.SourcesOf).Where(d => d.Key == speakers.Key), d => Assert.True(d.IsMic));

        var chat = draft.SourcesOf(second)[1];
        draft.RemoveSource(second, chat.Key);
        var layout = draft.ToLayout();

        Assert.Equal([mic.Key, speakers.Key], layout.Devices.Select(d => d.Key));
        Assert.Equal([[mic.Key, speakers.Key], [speakers.Key]], layout.Tracks.Select(t => t.DeviceKeys.ToArray()));
        Assert.Equal(Keys.EverythingTrackId, layout.Tracks[0].Id);
        Assert.True(Keys.TrackLayout.IsValid(Keys.TrackLayout.ToJson(layout)));
    }

    [Fact]
    public void APresetReplacesTheTracksKeepingTrackOnesId()
    {
        var firstId = Guid.NewGuid();
        var draft = AudioTracksDraft.From(new TrackLayout([], [new Track(firstId, "Mine", [], MuteAffected: false)]));

        draft.ApplyPreset(TrackLayoutPreset.ForEditing, [Speakers, Chat, Yeti]);
        var layout = draft.ToLayout();

        Assert.Equal([firstId, layout.Tracks[1].Id, layout.Tracks[2].Id], layout.Tracks.Select(t => t.Id));
        Assert.Equal(["Everything", "Game and chat", "Microphone"], layout.Tracks.Select(t => t.Name));
        Assert.Equal([Speakers.EndpointId, Yeti.EndpointId], layout.Devices.Select(d => d.EndpointId));
    }

    [Fact]
    public void ProbesAreAddedOnlyToTheWatchedCopyWithStableIds()
    {
        var draft = AudioTracksDraft.From(Keys.TrackLayout.Default);
        draft.AddSource(draft.Tracks[0], Speakers);

        var watched = MeterProbes.WithProbes(draft.ToLayout(), [Speakers, Chat, Yeti with { Available = false }]);

        // The draft's Track, then one probe per source and per available picker device.
        Assert.Equal(
            [Keys.EverythingTrackId, MeterProbes.TrackId("{out-1}", AudioFlow.Output), MeterProbes.TrackId("{out-2}", AudioFlow.Output)],
            watched.Tracks.Select(t => t.Id));
        Assert.Equal(MeterProbes.TrackId("{OUT-2}", AudioFlow.Output), watched.Tracks[2].Id);
        Assert.Equal(2, watched.Devices.Count);

        var levels = new MeterLevels([new TrackLevel(Keys.EverythingTrackId, 0.1f), new TrackLevel(watched.Tracks[2].Id, 0.7f)]);
        Assert.Equal(0.7f, MeterProbes.LevelOf(levels, "{out-2}", AudioFlow.Output));
        Assert.Equal(0f, MeterProbes.LevelOf(levels, "{in-1}", AudioFlow.Input));

        // The saved layout has no probe.
        Assert.Single(draft.ToLayout().Tracks);
        Assert.Single(draft.ToLayout().Devices);
    }
}
