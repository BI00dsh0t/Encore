using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Core.Sessions;

namespace ReplayBuffer.Engine.Tests;

/// <summary>
/// The Track meters over a real pipe of a throwaway test instance (R6), on a <see cref="ToneHub"/>:
/// device T and mic M each play a 0.5-amplitude tone; mic mute starts on (no <c>runtime.json</c>).
/// </summary>
public sealed class MeterServeTests : IAsyncLifetime
{
    private const double Amplitude = 0.5;
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(2);

    private static readonly LayoutDevice Tone = new(Guid.NewGuid(), "T", "Speakers", "Realtek", AudioFlow.Output, IsMic: false);
    private static readonly LayoutDevice Mic = new(Guid.NewGuid(), "M", "Microphone", "Realtek", AudioFlow.Input, IsMic: true);
    private static readonly Track Game = new(Guid.NewGuid(), "Game", [Tone.Key], MuteAffected: false);
    private static readonly Track Chat = new(Guid.NewGuid(), "Chat", [Mic.Key], MuteAffected: true);
    private static readonly Track MicRaw = new(Guid.NewGuid(), "MicRaw", [Mic.Key], MuteAffected: false);
    private static readonly Track Silent = new(Guid.NewGuid(), "Empty", [], MuteAffected: false);
    private static readonly TrackLayout Draft = new([Tone, Mic], [Game, Chat, MicRaw, Silent]);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-meters-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _disposables = [];
    private readonly ToneHub _tones = new(new Dictionary<string, (double, double)> { ["T"] = (Amplitude, 440), ["M"] = (Amplitude, 1000) });
    private EngineHost _host = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Instance TestInstance => new(ServeCommand.TestInstancePrefix + Path.GetFileName(_folder).Replace(".", "-"), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _host = EngineHost.Start(
            TestInstance,
            NullLoggerFactory.Instance,
            hostOptions: new EngineHostOptions { Heartbeat = null, ProbeEncoders = false, AudioEndpoints = new FakeEndpointSource(), MeterAudio = _tones });
        _disposables.Add(_host);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            await disposable.DisposeAsync();
        }

        _tones.Dispose();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task ADraftIsMeteredPerTrackFollowsMicMuteAndStopsOnUnwatch()
    {
        var (client, events) = await ConnectAsync();
        Assert.Empty(client.State.Meters.Tracks);

        await client.SendAsync(AudioMessages.Watch, new MetersWatch(Draft), Token);

        // Muted: Chat holds only the muted mic; MicRaw has it unaffected; Empty has no devices.
        var levels = await LevelsAsync(events, l => Near(l[0]) && Near(l[2]));
        Assert.Equal([Game.Id, Chat.Id, MicRaw.Id, Silent.Id], levels.Select(l => l.TrackId));
        Assert.Equal(0, levels[1].Peak);
        Assert.Equal(0, levels[3].Peak);

        // At most 20 a second (one more for a window that straddles a tick).
        var clock = Stopwatch.StartNew();
        var count = 0;
        while (clock.Elapsed < TimeSpan.FromSeconds(1))
        {
            var next = await events.Reader.ReadAsync(Token).AsTask().WaitAsync(Within, Token);
            if (next.Type == AudioMessages.Levels.Name && clock.Elapsed <= TimeSpan.FromSeconds(1))
            {
                count++;
            }
        }

        Assert.InRange(count, 1, 21);

        // Unmuted, Chat moves.
        Assert.True(_host.Sessions.Post(new HotkeyPressed(HotkeyAction.MicMute, Stopwatch.GetTimestamp())));
        levels = await LevelsAsync(events, l => Near(l[1].Peak));
        Assert.True(Near(levels[0].Peak) && Near(levels[2].Peak));
        Assert.Equal(0, levels[3].Peak);

        await client.SendAsync(AudioMessages.Unwatch, Empty.Value, Token);
        Assert.Equal(0, _tones.ActiveSubscriptions);
        var unwatched = Stopwatch.StartNew();
        while (client.State.Meters.Tracks.Count > 0 && unwatched.Elapsed < Within)
        {
            await Task.Delay(20, Token);
        }

        Assert.Empty(client.State.Meters.Tracks);

        // Unwatching again is a no-op.
        await client.SendAsync(AudioMessages.Unwatch, Empty.Value, Token);
    }

    [Fact]
    public async Task ALayoutNamingAMissingDeviceIsRefusedAndKeepsThePreviousWatchUntilTheNextOne()
    {
        var (client, events) = await ConnectAsync();
        await client.SendAsync(AudioMessages.Watch, new MetersWatch(Draft), Token);
        await LevelsAsync(events, l => Near(l[0].Peak));

        var bad = new TrackLayout([Tone], [new Track(Guid.NewGuid(), "Broken", [Tone.Key, Guid.NewGuid()], MuteAffected: false)]);
        var refused = await Assert.ThrowsAsync<ErrorCodeException>(() => client.SendAsync(AudioMessages.Watch, new MetersWatch(bad), Token));

        Assert.Equal(ErrorCodes.InvalidArguments, refused.Code);
        Assert.Equal(2, _tones.ActiveSubscriptions);
        var levels = await LevelsAsync(events, l => Near(l[0].Peak));
        Assert.Equal(Game.Id, levels[0].TrackId);

        // A new watch replaces it; a device that can't open is left out and the rest still meter.
        var absent = new LayoutDevice(Guid.NewGuid(), "absent", "Headset", "Arctis", AudioFlow.Output, IsMic: false);
        var both = new Track(Guid.NewGuid(), "Both", [Tone.Key, absent.Key], MuteAffected: false);
        await client.SendAsync(AudioMessages.Watch, new MetersWatch(new TrackLayout([Tone, absent], [both])), Token);
        Assert.Equal(1, _tones.ActiveSubscriptions);
        levels = await LevelsAsync(events, l => Near(l[0].Peak), tracks: 1);
        Assert.Equal(both.Id, levels[0].TrackId);
    }

    [Fact]
    public async Task ProbeTracksGiveALevelPerDeviceAndOpenEachDeviceOnce()
    {
        // Story 10.4: the card's source T and the picker's M, each probed; M isn't on any Track.
        var (client, events) = await ConnectAsync();
        var picker = new AudioDevice("M", "Microphone", "Realtek", AudioFlow.Input, Available: true, IsDefault: true);
        var watched = MeterProbes.WithProbes(new TrackLayout([Tone], [Game]), [picker]);

        await client.SendAsync(AudioMessages.Watch, new MetersWatch(watched), Token);

        // Mic mute is on, but a probe shows the device as it sounds.
        var levels = new MeterLevels(await LevelsAsync(events, l => Near(l[0]) && Near(l[1]) && Near(l[2]), tracks: 3));
        Assert.Equal(Game.Id, levels.Tracks[0].TrackId);
        Assert.True(Near(MeterProbes.LevelOf(levels, "T", AudioFlow.Output)));
        Assert.True(Near(MeterProbes.LevelOf(levels, "m", AudioFlow.Input)));
        Assert.Equal(2, _tones.ActiveSubscriptions);
    }

    [Fact]
    public async Task TheWatchStopsWhenTheLastClientDisconnects()
    {
        // Story 10.4: a crashed App leaves no probed device open.
        var (client, events) = await ConnectAsync();
        await client.SendAsync(AudioMessages.Watch, new MetersWatch(Draft), Token);
        await LevelsAsync(events, l => Near(l[0]));
        Assert.Equal(2, _tones.ActiveSubscriptions);

        await client.DisposeAsync();

        var clock = Stopwatch.StartNew();
        while (_tones.ActiveSubscriptions > 0 && clock.Elapsed < Within)
        {
            await Task.Delay(20, Token);
        }

        Assert.Equal(0, _tones.ActiveSubscriptions);
    }

    private static bool Near(TrackLevel level) => Near(level.Peak);

    private static bool Near(float peak) => Math.Abs(peak - Amplitude) < 0.05;

    /// <summary>The first <c>meters.levels</c> of <paramref name="tracks"/> Tracks within <see cref="Within"/> that <paramref name="match"/> accepts.</summary>
    private static async Task<IReadOnlyList<TrackLevel>> LevelsAsync(Channel<IpcEvent> events, Func<IReadOnlyList<TrackLevel>, bool> match, int tracks = 4)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(Within);
        while (true)
        {
            var next = await events.Reader.ReadAsync(timeout.Token);
            if (next.Type != AudioMessages.Levels.Name)
            {
                continue;
            }

            var levels = AudioMessages.Levels.Read(next.Payload).Tracks;
            if (levels.Count == tracks && match(levels))
            {
                return levels;
            }
        }
    }

    private async Task<(EngineClient Client, Channel<IpcEvent> Events)> ConnectAsync()
    {
        var events = Channel.CreateUnbounded<IpcEvent>();
        var client = await EngineClient.ConnectAsync(_host.PipeName, new EngineClientOptions { OnEvent = e => events.Writer.TryWrite(e) }, Token);
        _disposables.Add(client);
        return (client, events);
    }
}
