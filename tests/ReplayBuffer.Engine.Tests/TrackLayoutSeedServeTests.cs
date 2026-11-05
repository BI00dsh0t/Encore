using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Engine.Settings;

namespace ReplayBuffer.Engine.Tests;

/// <summary>
/// The first-run seed (story 10.4, R6) on a throwaway test instance with fake endpoints: the Simple
/// preset from the Windows defaults when settings.json has no Global <c>trackLayout</c>, and nothing otherwise.
/// </summary>
public sealed class TrackLayoutSeedServeTests : IAsyncLifetime
{
    private static readonly AudioDevice Speakers = new("{0.0.0.00000000}.{speakers}", "Speakers", "Realtek", AudioFlow.Output, true, IsDefault: true);
    private static readonly AudioDevice Dac = new("{0.0.0.00000000}.{dac}", "Speakers", "USB DAC", AudioFlow.Output, true);
    private static readonly AudioDevice Yeti = new("{0.0.1.00000000}.{yeti}", "Microphone", "Yeti", AudioFlow.Input, true, IsDefault: true);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-seed-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _disposables = [];

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
    public async Task AnEmptySettingsFolderIsSeededWithTheDefaultsOnOneEverythingTrack()
    {
        var document = await StartAsync(Speakers, Dac, Yeti);

        Assert.Equal(1, document.Revision);
        var layout = Layout(document);
        Assert.Equal([(Speakers.EndpointId, false), (Yeti.EndpointId, true)], layout.Devices.Select(d => (d.EndpointId, d.IsMic)));
        var track = Assert.Single(layout.Tracks);
        Assert.Equal((Keys.EverythingTrackId, "Everything", true), (track.Id, track.Name, track.MuteAffected));
        Assert.Equal(layout.Devices.Select(d => d.Key), track.DeviceKeys);

        // It is in the file, not only in memory.
        Assert.True(SettingsStore.ReadDocument(TestInstance.RoamingDataFolder).Global.ContainsKey(Keys.TrackLayout.Name));
    }

    [Fact]
    public async Task NoDefaultInputSeedsPlaybackOnly()
    {
        var layout = Layout(await StartAsync(Speakers, Yeti with { IsDefault = false }));

        Assert.Equal([Speakers.EndpointId], layout.Devices.Select(d => d.EndpointId));
        Assert.Equal([layout.Devices[0].Key], Assert.Single(layout.Tracks).DeviceKeys);
    }

    [Fact]
    public async Task NoDefaultDeviceWritesNothingSoALaterStartRetries()
    {
        // A failed or empty listing would seed the catalog default; QA 10.4 finding 1.
        var document = await StartAsync();

        Assert.Equal(0, document.Revision);
        Assert.False(document.Global.ContainsKey(Keys.TrackLayout.Name));
        Assert.False(File.Exists(Path.Combine(TestInstance.RoamingDataFolder, SettingsStore.FileName)));
    }

    [Fact]
    public async Task AnExistingLayoutIsLeftUnchanged()
    {
        var mine = new TrackLayout([], [new Track(Guid.NewGuid(), "Mine", [], MuteAffected: false)]);
        var existing = SettingsStore.Open(TestInstance.RoamingDataFolder).Store
            .Update(new SettingsUpdate(0, [SettingsOp.SetGlobal(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(mine))]));

        var document = await StartAsync(Speakers, Yeti);

        Assert.Equal(existing.Revision, document.Revision);
        Assert.Equal(existing.Global[Keys.TrackLayout.Name].GetRawText(), document.Global[Keys.TrackLayout.Name].GetRawText());
    }

    private static TrackLayout Layout(SettingsDocument document)
    {
        Assert.True(Keys.TrackLayout.TryRead(document.Global[Keys.TrackLayout.Name], out var layout));
        return layout;
    }

    /// <summary>Starts the engine with the seed on and returns the settings its first snapshot carries.</summary>
    private async Task<SettingsDocument> StartAsync(params AudioDevice[] present)
    {
        var host = EngineHost.Start(
            TestInstance,
            NullLoggerFactory.Instance,
            hostOptions: new EngineHostOptions { Heartbeat = null, ProbeEncoders = false, AudioEndpoints = new FakeEndpointSource(present), SeedTrackLayout = true });
        _disposables.Add(host);
        var client = await EngineClient.ConnectAsync(host.PipeName, new EngineClientOptions(), Token);
        _disposables.Add(client);
        return client.State.Settings;
    }
}
