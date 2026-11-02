using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Sessions;

namespace ReplayBuffer.Core.Tests;

public sealed class SessionAudioTests
{
    private static readonly LayoutDevice Speakers = Device("{0.0.0}.{speakers}", "Speakers", AudioFlow.Output);
    private static readonly LayoutDevice Chat = Device("{0.0.0}.{chat}", "Chat", AudioFlow.Output);
    private static readonly LayoutDevice Mic = Device("{0.0.1}.{mic}", "Mic", AudioFlow.Input);

    private static LayoutDevice Device(string endpointId, string name, AudioFlow flow) =>
        new(Guid.NewGuid(), endpointId, name, "Parent " + name, flow, IsMic: flow == AudioFlow.Input);

    private static Track Track(string name, params LayoutDevice[] devices) =>
        new(Guid.NewGuid(), name, [.. devices.Select(d => d.Key)], MuteAffected: false);

    [Fact]
    public void TracksKeepLayoutOrderAndDevicesFollowFirstReference()
    {
        var layout = new TrackLayout([Mic, Chat, Speakers], [Track("Game", Speakers), Track("Chat", Chat), Track("Mic", Mic)]);

        var audio = SessionAudio.FromLayout(layout);

        Assert.Equal(["Game", "Chat", "Mic"], audio.Tracks.Select(t => t.Name));
        Assert.Equal(["Speakers", "Chat", "Mic"], audio.Devices.Select(d => d.FriendlyName));
        Assert.Equal([[0], [1], [2]], audio.Tracks.Select(t => t.DeviceIndexes.ToArray()));
        Assert.Equal(new SessionAudioDevice(Mic.EndpointId, AudioFlow.Input, "Mic"), audio.Devices[2]);
    }

    [Fact]
    public void ADeviceSharedByTracksIsCapturedOnce()
    {
        var layout = new TrackLayout([Speakers, Mic], [Track("A", Speakers), Track("B", Speakers, Mic)]);

        var audio = SessionAudio.FromLayout(layout);

        Assert.Equal(2, audio.Devices.Count);
        Assert.Equal([0], audio.Tracks[0].DeviceIndexes);
        Assert.Equal([0, 1], audio.Tracks[1].DeviceIndexes);
    }

    [Fact]
    public void TwoKeysForOneEndpointAndFlowAreOneDevice()
    {
        var again = Speakers with { Key = Guid.NewGuid(), FriendlyName = "Speakers again" };
        var layout = new TrackLayout([Speakers, again], [Track("A", Speakers), Track("B", again, Speakers)]);

        var audio = SessionAudio.FromLayout(layout);

        Assert.Single(audio.Devices);
        Assert.Equal([0], audio.Tracks[1].DeviceIndexes);
    }

    [Fact]
    public void ADeviceNoTrackUsesIsNotCaptured()
    {
        var layout = new TrackLayout([Chat, Speakers, Mic], [Track("Game", Speakers)]);

        var audio = SessionAudio.FromLayout(layout);

        Assert.Equal(Speakers.EndpointId, Assert.Single(audio.Devices).EndpointId);
    }

    [Fact]
    public void ATrackWithNoDevicesIsRefused()
    {
        var e = Assert.Throws<ErrorCodeException>(() => SessionAudio.FromLayout(Keys.TrackLayout.Default));

        Assert.Equal(ErrorCodes.InvalidArguments, e.Code);
        Assert.StartsWith("Track 'Everything' has no devices", e.Message);
    }

    [Fact]
    public void OnePerDeviceNamesEachTrackAfterItsDevice()
    {
        SessionAudioDevice[] devices =
        [
            new("out", AudioFlow.Output, "Speakers"),
            new("in", AudioFlow.Input, "Mic"),
        ];

        var audio = SessionAudio.OnePerDevice(devices);

        Assert.Equal(devices, audio.Devices);
        Assert.Equal(["Speakers", "Mic"], audio.Tracks.Select(t => t.Name));
        Assert.Equal([[0], [1]], audio.Tracks.Select(t => t.DeviceIndexes.ToArray()));
    }
}
