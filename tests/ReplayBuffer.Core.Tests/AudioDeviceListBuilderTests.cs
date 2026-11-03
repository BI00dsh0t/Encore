using System.Collections.Immutable;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Audio;

namespace ReplayBuffer.Core.Tests;

public sealed class AudioDeviceListBuilderTests
{
    private static readonly AudioDevice Realtek = new("{0.0.0.00000000}.{realtek}", "Speakers", "Realtek", AudioFlow.Output, true);
    private static readonly AudioDevice Dac = new("{0.0.0.00000000}.{dac}", "Speakers", "USB DAC", AudioFlow.Output, true);
    private static readonly AudioDevice Mic = new("{0.0.1.00000000}.{mic}", "Microphone", "Realtek", AudioFlow.Input, true);

    [Fact]
    public void PresentDevicesAreListedAvailableOutputFirstThenByName()
    {
        var list = AudioDeviceListBuilder.Build([Mic, Realtek, Dac], SettingsDocument.Empty);

        Assert.Equal([Realtek, Dac, Mic], list.Devices);
    }

    [Fact]
    public void TwoDevicesWithTheSameNameAreToldApartByParent()
    {
        var list = AudioDeviceListBuilder.Build([Dac, Realtek], SettingsDocument.Empty);

        Assert.Equal(["Realtek", "USB DAC"], list.Devices.Select(d => d.ParentName));
        Assert.All(list.Devices, d => Assert.Equal("Speakers", d.FriendlyName));
    }

    [Fact]
    public void AnAbsentGlobalLayoutDeviceIsListedUnavailable()
    {
        var absent = Layout(Device("{0.0.0.00000000}.{gone}", "Headphones", "Arctis", AudioFlow.Output));
        var document = Document(global: absent);

        var list = AudioDeviceListBuilder.Build([Realtek], document);

        Assert.Equal(
            [new AudioDevice("{0.0.0.00000000}.{gone}", "Headphones", "Arctis", AudioFlow.Output, false), Realtek],
            list.Devices);
    }

    [Fact]
    public void APresentLayoutDeviceIsListedOnceAvailableMatchingIdIgnoringCase()
    {
        var document = Document(global: Layout(Device(Realtek.EndpointId.ToUpperInvariant(), "Old name", "Old parent", AudioFlow.Output)));

        var list = AudioDeviceListBuilder.Build([Realtek], document);

        Assert.Equal([Realtek], list.Devices);
    }

    [Fact]
    public void AnAbsentDeviceNoLayoutHoldsIsNotListed()
    {
        Assert.Equal([Mic], AudioDeviceListBuilder.Build([Mic], SettingsDocument.Empty).Devices);
    }

    [Fact]
    public void AnAbsentDeviceInGlobalAndAProfileIsListedOnce()
    {
        var device = Device("{0.0.1.00000000}.{usbmic}", "Yeti", "Blue", AudioFlow.Input);
        var document = Document(global: Layout(device), profile: Layout(device with { Key = Guid.NewGuid() }));

        var list = AudioDeviceListBuilder.Build([], document);

        Assert.Equal([new AudioDevice(device.EndpointId, "Yeti", "Blue", AudioFlow.Input, false)], list.Devices);
    }

    [Fact]
    public void AProfileOnlyLayoutDeviceIsListed()
    {
        var device = Device("{0.0.1.00000000}.{usbmic}", "Yeti", "Blue", AudioFlow.Input);

        var list = AudioDeviceListBuilder.Build([Mic], Document(global: null, profile: Layout(device)));

        Assert.Equal([Mic, new AudioDevice(device.EndpointId, "Yeti", "Blue", AudioFlow.Input, false)], list.Devices);
    }

    [Fact]
    public void ALayoutDevicePresentOnlyWithTheOtherFlowIsUnavailableForItsOwn()
    {
        // The id is present as an Output Device; the layout holds it as an Input Device.
        var document = Document(global: Layout(Device(Realtek.EndpointId, "Speakers", "Realtek", AudioFlow.Input)));

        var list = AudioDeviceListBuilder.Build([Realtek], document);

        Assert.Equal([Realtek, Realtek with { Flow = AudioFlow.Input, Available = false }], list.Devices);
    }

    [Fact]
    public void TiesAreBrokenByEndpointId()
    {
        var b = Realtek with { EndpointId = "b" };
        var a = Realtek with { EndpointId = "a" };

        Assert.Equal([a, b], AudioDeviceListBuilder.Build([b, a], SettingsDocument.Empty).Devices);
    }

    private static LayoutDevice Device(string id, string name, string parent, AudioFlow flow) =>
        new(Guid.NewGuid(), id, name, parent, flow, IsMic: flow == AudioFlow.Input);

    private static TrackLayout Layout(params LayoutDevice[] devices) =>
        new(devices, [new Track(Guid.NewGuid(), "All", [.. devices.Select(d => d.Key)], MuteAffected: false)]);

    private static SettingsDocument Document(TrackLayout? global, TrackLayout? profile = null)
    {
        var document = SettingsDocument.Empty;
        if (global is not null)
        {
            document = document with { Global = document.Global.Add(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(global)) };
        }

        if (profile is not null)
        {
            var game = new GameProfile
            {
                Name = "Game",
                Overrides = SettingsMaps.EmptyValues.Add(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(profile)),
            };
            document = document with { Profiles = SettingsMaps.EmptyProfiles.Add(@"c:\games\game.exe", game) };
        }

        return document;
    }
}
