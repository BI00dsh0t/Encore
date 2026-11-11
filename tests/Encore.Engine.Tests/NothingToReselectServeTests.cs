using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Library;
using Encore.Core.Sessions;
using Encore.Engine.Settings;
using Encore.Platform.Audio;
using Encore.Platform.Audio.Tests;
using Encore.Platform.Windows;
using Encore.Platform.Windows.Stores;
using PlatformFlow = Encore.Platform.Audio.AudioFlow;
using SettingsFlow = Encore.Contracts.Settings.AudioFlow;

namespace Encore.Engine.Tests;

/// <summary>Nothing needs re-selecting after a reboot or a mixer restart; in process, with fake audio clients and endpoints.</summary>
public sealed class NothingToReselectServeTests : IAsyncLifetime
{
    private const int GamePid = 4321;
    private const string Game = "{0.0.0.00000000}.{aaaaaaaa-0000-0000-0000-0000000000a1}";
    private const string Mic = "{0.0.1.00000000}.{bbbbbbbb-0000-0000-0000-0000000000b1}";
    private const string GameRenumbered = "{0.0.0.00000000}.{aaaaaaaa-0000-0000-0000-0000000000a2}";
    private const string MicRenumbered = "{0.0.1.00000000}.{bbbbbbbb-0000-0000-0000-0000000000b2}";
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(5);

    private static readonly LayoutDevice GameDevice = new(Guid.NewGuid(), Game, "Game", "BEACN", SettingsFlow.Output, IsMic: false);
    private static readonly LayoutDevice MicDevice = new(Guid.NewGuid(), Mic, "Mic", "BEACN", SettingsFlow.Input, IsMic: true);
    private static readonly TrackLayout Layout = new(
        [GameDevice, MicDevice],
        [new Track(Guid.NewGuid(), "Game", [GameDevice.Key], MuteAffected: false), new Track(Guid.NewGuid(), "Mic", [MicDevice.Key], MuteAffected: false)]);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-reselect-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _disposables = [];
    private Instance _instance = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string Output => Path.Combine(_folder, "out");

    private string GamePath => Path.Combine(_folder, "Game.EXE");

    // The test game is a Steam game, so it is captured at once.
    private IReadOnlyList<StoreFolder> SteamFolder() => [new StoreFolder(StoreLibraries.Normalize(_folder), GameStore.Steam)];

    private string SettingsFile => Path.Combine(_instance.RoamingDataFolder, SettingsStore.FileName);

    private string RuntimeFile => Path.Combine(_instance.LocalDataFolder, RuntimeStateStore.FileName);

    private string LibraryFile => Path.Combine(_instance.RoamingDataFolder, "library.json");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(Output, "Game"));
        await File.WriteAllBytesAsync(GamePath, [], Token);
        _instance = new Instance(Instance.TestPrefix + Guid.NewGuid().ToString("N"), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));
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
    public async Task EveryPersistedValueIsUnchangedAfterAColdStartAndTheGameIsTargetedAgainWithItsProfile()
    {
        var probe = new GameDetectionServeTests.FakeProbe();
        var endpoints = new FakeEndpointSource(
            new AudioDevice(Game, "Game", "BEACN", SettingsFlow.Output, Available: true),
            new AudioDevice(Mic, "Mic", "BEACN", SettingsFlow.Input, Available: true));
        var (host, client) = await StartAsync(Options(probe, endpoints));
        await probe.PolledAsync(2);

        // The game is detected, its Game Profile auto-created and given an override and an attribute.
        probe.Add(GamePid, GamePath);
        probe.Foreground = new GameWindow(0x10, GamePid);
        var profileId = GamePaths.Canonicalize(GamePath);
        await UntilAsync(() => client.State.TargetGame?.ProfileId == profileId, "the game targeted");
        await UpdateAsync(
            client,
            SettingsOp.SetGlobal(Keys.OutputFolder.Name, Keys.OutputFolder.ToJson(Output)),
            SettingsOp.SetGlobal(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(Layout)),
            SettingsOp.SetGlobal(Keys.BufferWindow.Name, Keys.BufferWindow.ToJson(45)),
            SettingsOp.SetGlobal(Keys.StartWithWindows.Name, Keys.StartWithWindows.ToJson(true)),
            SettingsOp.SetGlobal(Keys.Hotkeys.Name, Keys.Hotkeys.ToJson(new HotkeySettings(new HotkeyBinding(0x85, 7), new HotkeyBinding(0x86, 7), null, null, null, null))),
            SettingsOp.SetProfile(profileId, ProfileAttributes.RecordingsEnabled, JsonSerializer.SerializeToElement(true)),
            SettingsOp.SetProfile(profileId, Keys.BufferWindow.Name, Keys.BufferWindow.ToJson(20)));

        // Mic mute off (runtime.json), and a Clip made a Favourite (library.json).
        Assert.True(client.State.Capture.MicMuted);
        Assert.True(host.Sessions.Post(new HotkeyPressed(HotkeyAction.MicMute, Stopwatch.GetTimestamp())));
        await UntilAsync(() => !client.State.Capture.MicMuted, "mic unmuted");
        await UntilAsync(() => client.State.LibraryFolder == Output, "the Library on the Output Folder");
        var clip = Path.Combine(Output, "Game", "Game_2026-11-04_10-00-00.mp4");
        host.Library.BeginWrite(clip, profileId);
        await File.WriteAllBytesAsync(clip, new byte[64], Token);
        host.Library.EndWrite(clip, WriteOutcome.Written);
        await UntilAsync(() => client.State.Library.Count == 1 && !client.State.Library[0].BeingWritten, "the Clip listed");
        var item = client.State.Library[0];
        await client.SendAsync(LibraryMessages.SetFavourite, new LibrarySetFavourite(item.Path, item.Size, item.Modified, true), Token);
        await UntilAsync(() => client.State.Library[0].Favourite, "the Favourite");
        await UntilAsync(() => File.Exists(LibraryFile) && System.Text.Encoding.UTF8.GetString(ReadShared(LibraryFile)).Contains("true", StringComparison.Ordinal), "library.json written");

        var settings = client.State.Settings;
        var files = Snapshot();
        await StopAsync(host, client);

        // A reboot: a cold start on the same folders, with the game launched again afterwards.
        probe = new GameDetectionServeTests.FakeProbe();
        (_, client) = await StartAsync(Options(probe, endpoints));
        Assert.Equal(Json(settings), Json(client.State.Settings));
        Assert.False(client.State.Capture.MicMuted);
        Assert.True(Assert.Single(client.State.Library).Favourite);
        Assert.Contains(client.State.Hotkeys.Bindings, b => b.Action == HotkeyAction.Save && b.Binding == new HotkeyBinding(0x85, 7));
        Assert.Equal(files, Snapshot());

        await probe.PolledAsync(2);
        probe.Add(GamePid + 1, GamePath);
        probe.Foreground = new GameWindow(0x20, GamePid + 1);
        await UntilAsync(() => client.State.TargetGame?.ProfileId == profileId, "the game targeted after the restart");
        Assert.Single(client.State.Settings.Profiles);
        Assert.Equal(settings.Revision, client.State.Settings.Revision);
    }

    [Fact]
    public async Task AMixerRestartRebindsEveryTrackWithSilenceOnlyInTheGapAndLeavesTheSettingsAlone()
    {
        var source = new FakeEndpointList();
        var bank = new FakeDeviceBank();
        bank.Define(Game, "Game", "BEACN", PlatformFlow.Output, 0.2, 440);
        bank.Define(Mic, "Mic", "BEACN", PlatformFlow.Input, 0.2, 1000);
        bank.Define(GameRenumbered, "Game", "BEACN", PlatformFlow.Output, 0.2, 440);
        bank.Define(MicRenumbered, "Mic", "BEACN", PlatformFlow.Input, 0.2, 1000);
        source.Add(Game, "Game", "BEACN", SettingsFlow.Output, raise: false);
        source.Add(Mic, "Mic", "BEACN", SettingsFlow.Input, raise: false);
        AudioDeviceHub? hub = null;
        var (_, client) = await StartAsync(new EngineHostOptions { AudioHub = loggers => hub = new AudioDeviceHub(loggers, bank.Create, source) });
        await UpdateAsync(client, SettingsOp.SetGlobal(Keys.TrackLayout.Name, Keys.TrackLayout.ToJson(Layout)));
        var settings = ReadShared(SettingsFile);

        // The Track meters hold both devices open, as a session would; neither Track is mute-affected.
        await client.SendAsync(AudioMessages.Watch, new MetersWatch(Layout), Token);
        await UntilAsync(() => BothLoud(client), "both Tracks metered");

        // The mixer restarts and its endpoints return under the same ids, then under new ids.
        foreach (var (game, mic) in new[] { (Game, Mic), (GameRenumbered, MicRenumbered) })
        {
            foreach (var id in new[] { Game, Mic, GameRenumbered, MicRenumbered })
            {
                bank.Unplug(id);
                source.Remove(id);
            }

            await UntilAsync(() => hub!.DroppedDeviceCount == 2, "both devices dropped");
            await UntilAsync(() => client.State.Meters.Tracks.All(t => t.Peak == 0), "silence in the gap");
            bank.SetOpenable(game, true);
            bank.SetOpenable(mic, true);
            source.Add(game, "Game", "BEACN", SettingsFlow.Output);
            source.Add(mic, "Mic", "BEACN", SettingsFlow.Input);
            await UntilAsync(() => hub!.DroppedDeviceCount == 0, "both devices back");
            await UntilAsync(() => BothLoud(client), "both Tracks metered again");
            Assert.Equal(2, hub!.OpenDeviceCount);
        }

        // A default-device change lists the same endpoints: nothing drops or reopens.
        var opens = bank.Opens(GameRenumbered) + bank.Opens(MicRenumbered);
        source.Raise();
        await Task.Delay(300, Token);
        Assert.Equal(0, hub!.DroppedDeviceCount);
        Assert.Equal(opens, bank.Opens(GameRenumbered) + bank.Opens(MicRenumbered));

        // A new watch from the saved layout (old ids) opens the renumbered endpoints by name.
        await client.SendAsync(AudioMessages.Unwatch, Empty.Value, Token);
        await UntilAsync(() => hub.OpenDeviceCount == 0, "the meters closed");
        await client.SendAsync(AudioMessages.Watch, new MetersWatch(Layout), Token);
        await UntilAsync(() => hub.OpenDeviceCount == 2 && BothLoud(client), "the saved layout metered on the new ids");

        // Nothing was written: the Track Layout on disk still names the ids the user chose.
        Assert.Equal(settings, ReadShared(SettingsFile));
        Assert.True(Keys.TrackLayout.TryRead(client.State.Settings.Global[Keys.TrackLayout.Name], out var saved));
        Assert.Equal(Json(Layout), Json(saved));
        Assert.DoesNotContain(client.State.Warnings, w => w.Code == ErrorCodes.AudioDeviceFailed);
    }

    private static bool BothLoud(EngineClient client) =>
        client.State.Meters.Tracks is { Count: 2 } tracks && tracks.All(t => t.Peak > 0.1f);

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, typeof(T), SettingsJsonContext.Default);

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(clock.Elapsed < Within, $"Timed out waiting for {what}.");
            await Task.Delay(20, Token);
        }
    }

    private static async Task UpdateAsync(EngineClient client, params SettingsOp[] ops)
    {
        var result = await client.SendAsync(SettingsMessages.Update, new SettingsUpdate(client.State.Settings.Revision, ops), Token);
        await UntilAsync(() => client.State.Settings.Revision >= result.Revision, "settings.changed");
    }

    /// <summary>Every persisted file's bytes.</summary>
    private Dictionary<string, string> Snapshot() =>
        new[] { SettingsFile, RuntimeFile, LibraryFile }.ToDictionary(f => Path.GetFileName(f), f => Convert.ToBase64String(ReadShared(f)));

    /// <summary>A file the engine may be replacing: read with sharing, and retried for up to 2 s while the swap holds it.</summary>
    private static byte[] ReadShared(string path)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var copy = new MemoryStream();
                stream.CopyTo(copy);
                return copy.ToArray();
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && clock.Elapsed < TimeSpan.FromSeconds(2))
            {
                Thread.Sleep(20);
            }
        }
    }

    private EngineHostOptions Options(GameDetectionServeTests.FakeProbe probe, FakeEndpointSource endpoints) =>
        new() { GameWindows = probe, GameStores = SteamFolder, AudioEndpoints = endpoints };

    private async Task<(EngineHost Host, EngineClient Client)> StartAsync(EngineHostOptions options)
    {
        var host = EngineHost.Start(
            _instance,
            NullLoggerFactory.Instance,
            hostOptions: options with
            {
                Heartbeat = null,
                ProbeEncoders = false,
                AppLauncher = new GameDetectionServeTests.StayingLauncher(),
                GameScanInterval = TimeSpan.FromMilliseconds(50),
            });
        _disposables.Add(host);
        var client = await EngineClient.ConnectAsync(host.PipeName, cancellationToken: Token);
        _disposables.Add(client);
        return (host, client);
    }

    private async Task StopAsync(EngineHost host, EngineClient client)
    {
        _disposables.Remove(client);
        _disposables.Remove(host);
        await client.DisposeAsync();
        await host.DisposeAsync();
    }
}
