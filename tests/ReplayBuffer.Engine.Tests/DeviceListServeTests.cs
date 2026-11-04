using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Audio;

namespace ReplayBuffer.Engine.Tests;

/// <summary>An endpoint source whose present list a test sets, raising the change itself.</summary>
internal sealed class FakeEndpointSource(params AudioDevice[] present) : IAudioEndpointSource
{
    private IReadOnlyList<AudioDevice> _present = present;

    public event Action? EndpointsChanged;

    /// <summary>When set, <see cref="ListPresent"/> throws it.</summary>
    public Exception? Failure { get; set; }

    public int Lists { get; private set; }

    public IReadOnlyList<AudioDevice> ListPresent()
    {
        Lists++;
        return Failure is { } failure ? throw failure : _present;
    }

    /// <summary>Sets the present list and raises the change, as the watcher's thread would.</summary>
    public void Change(params AudioDevice[] present)
    {
        _present = present;
        Raise();
    }

    public void Raise() => EndpointsChanged?.Invoke();
}

/// <summary>The device list over a real pipe of a throwaway test instance, with a fake endpoint source.</summary>
public sealed class DeviceListServeTests : IAsyncLifetime
{
    private static readonly AudioDevice Realtek = new("{0.0.0.00000000}.{realtek}", "Speakers", "Realtek", AudioFlow.Output, true);
    private static readonly AudioDevice Dac = new("{0.0.0.00000000}.{dac}", "Speakers", "USB DAC", AudioFlow.Output, true);
    private static readonly AudioDevice Mic = new("{0.0.1.00000000}.{mic}", "Microphone", "Realtek", AudioFlow.Input, true);
    private static readonly AudioDevice Headset = new("{0.0.1.00000000}.{headset}", "Headset Mic", "Arctis", AudioFlow.Input, true);
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(2);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-devices-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _disposables = [];
    private EngineHost _host = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Instance TestInstance => new(ServeCommand.TestInstancePrefix + Path.GetFileName(_folder).Replace(".", "-"), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            await disposable.DisposeAsync();
        }

        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task TheSnapshotListsThePresentDevicesWithoutAnEvent()
    {
        StartHost(new FakeEndpointSource(Mic, Dac, Realtek));

        var (client, _) = await ConnectAsync();

        Assert.Equal(0, client.State.Seq);
        Assert.Equal([Realtek, Dac, Mic], client.State.Devices.Devices);
        Assert.Equal(["Realtek", "USB DAC"], client.State.Devices.Devices.Where(d => d.FriendlyName == "Speakers").Select(d => d.ParentName));
        Assert.Equal(client.State.Devices, await client.SendAsync(AudioMessages.List, Empty.Value, Token));
    }

    [Fact]
    public async Task AddingRemovingOrDisablingADevicePublishesTheNewList()
    {
        var source = new FakeEndpointSource(Realtek, Mic);
        StartHost(source);
        var (client, events) = await ConnectAsync();

        source.Change(Realtek, Mic, Headset);
        await AssertChangedAsync(client, events, 1, [Realtek, Headset, Mic]);

        source.Change(Realtek, Headset);
        await AssertChangedAsync(client, events, 2, [Realtek, Headset]);

        // A disabled endpoint isn't active: it leaves the present list.
        source.Change(Headset);
        await AssertChangedAsync(client, events, 3, [Headset]);
    }

    [Fact]
    public async Task AFailedListingKeepsThePreviousListAndTheEngineServes()
    {
        var source = new FakeEndpointSource(Realtek, Mic);
        StartHost(source);
        var (client, events) = await ConnectAsync();

        source.Failure = new InvalidOperationException("COM failed");
        source.Change(Mic);

        await Task.Delay(300, Token);
        Assert.False(events.Reader.TryRead(out _));
        Assert.Equal([Realtek, Mic], (await client.SendAsync(AudioMessages.List, Empty.Value, Token)).Devices);

        source.Failure = null;
        source.Raise();
        await AssertChangedAsync(client, events, 1, [Mic]);
    }

    [Fact]
    public async Task AStartupListingFailureServesAnEmptyList()
    {
        StartHost(new FakeEndpointSource(Realtek) { Failure = new InvalidOperationException("no audio") });

        var (client, _) = await ConnectAsync();

        Assert.Empty(client.State.Devices.Devices);
        Assert.Empty((await client.SendAsync(AudioMessages.List, Empty.Value, Token)).Devices);
    }

    [Fact]
    public async Task AnAbsentLayoutDeviceIsListedUnavailableAndAnUnconfiguredOneIsNot()
    {
        var source = new FakeEndpointSource(Realtek, Mic);
        StartHost(source);
        var absent = new LayoutDevice(Guid.NewGuid(), "{0.0.1.00000000}.{yeti}", "Yeti", "Blue", AudioFlow.Input, IsMic: true);
        _host.Settings.Update(SetLayout(0, absent));

        // Mic vanishes and no layout holds it.
        source.Change(Realtek);
        var (client, _) = await ConnectAsync();

        Assert.Equal([Realtek, new AudioDevice(absent.EndpointId, "Yeti", "Blue", AudioFlow.Input, false)], client.State.Devices.Devices);
    }

    [Fact]
    public async Task EditingALayoutShowsAndHidesItsAbsentDevices()
    {
        StartHost(new FakeEndpointSource(Realtek));
        var (client, events) = await ConnectAsync();
        var absent = new LayoutDevice(Guid.NewGuid(), "{0.0.0.00000000}.{gone}", "Headphones", "Arctis", AudioFlow.Output, IsMic: false);
        var unavailable = new AudioDevice(absent.EndpointId, "Headphones", "Arctis", AudioFlow.Output, false);

        await client.SendAsync(SettingsMessages.Update, SetLayout(0, absent), Token);
        Assert.Equal("settings.changed", (await NextAsync(events)).Type);
        await AssertChangedAsync(client, events, 2, [unavailable, Realtek]);

        // A layout that holds only a present device changes the settings, not the list.
        var present = new LayoutDevice(Guid.NewGuid(), Realtek.EndpointId, "Speakers", "Realtek", AudioFlow.Output, IsMic: false);
        await client.SendAsync(SettingsMessages.Update, SetLayout(1, absent, present), Token);
        Assert.Equal("settings.changed", (await NextAsync(events)).Type);
        await client.SendAsync(SettingsMessages.Update, SetLayout(2, present), Token);
        Assert.Equal("settings.changed", (await NextAsync(events)).Type);
        await AssertChangedAsync(client, events, 5, [Realtek]);

        await client.SendAsync(SettingsMessages.Update, SetLayout(3), Token);
        Assert.Equal("settings.changed", (await NextAsync(events)).Type);
        await Task.Delay(300, Token);
        Assert.False(events.Reader.TryRead(out _));
    }

    private static SettingsUpdate SetLayout(long expectedRevision, params LayoutDevice[] devices) =>
        new(expectedRevision, [SettingsOp.SetGlobal(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(new TrackLayout(devices, [new Track(Guid.NewGuid(), "All", [.. devices.Select(d => d.Key)], MuteAffected: false)])))]);

    private static async Task<IpcEvent> NextAsync(Channel<IpcEvent> events) =>
        await events.Reader.ReadAsync(Token).AsTask().WaitAsync(Within, Token);

    private static async Task AssertChangedAsync(EngineClient client, Channel<IpcEvent> events, long seq, AudioDevice[] expected)
    {
        var clock = Stopwatch.StartNew();
        var changed = await NextAsync(events);
        Assert.True(clock.Elapsed < Within);
        Assert.Equal(("devices.changed", seq), (changed.Type, changed.Seq));
        Assert.Equal(expected, client.State.Devices.Devices);
        Assert.Equal(client.State.Devices, await client.SendAsync(AudioMessages.List, Empty.Value, Token));
    }

    private void StartHost(IAudioEndpointSource source)
    {
        _host = EngineHost.Start(
            TestInstance,
            NullLoggerFactory.Instance,
            hostOptions: new EngineHostOptions { Heartbeat = null, ProbeEncoders = false, AudioEndpoints = source });
        _disposables.Add(_host);
    }

    private async Task<(EngineClient Client, Channel<IpcEvent> Events)> ConnectAsync()
    {
        var events = Channel.CreateUnbounded<IpcEvent>();
        var client = await EngineClient.ConnectAsync(_host.PipeName, new EngineClientOptions { OnEvent = e => events.Writer.TryWrite(e) }, Token);
        _disposables.Add(client);
        return (client, events);
    }
}
