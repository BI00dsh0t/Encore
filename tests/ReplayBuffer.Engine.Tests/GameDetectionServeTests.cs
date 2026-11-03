using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Engine.Supervision;
using ReplayBuffer.Platform.Windows;

namespace ReplayBuffer.Engine.Tests;

/// <summary>Game Detection over a real pipe of a throwaway test instance, with a fake foreground window probe.</summary>
public sealed class GameDetectionServeTests : IAsyncLifetime
{
    private const int GamePid = 4321;
    private const int AppPid = 4322;
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(5);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-games-" + Guid.NewGuid().ToString("N"));
    private readonly FakeProbe _probe = new();
    private EngineHost _host = null!;
    private EngineClient _client = null!;
    private Channel<IpcEvent> _events = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string GamePath => Path.Combine(_folder, "Game.EXE");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllBytesAsync(GamePath, [], Token);
        var instance = new Instance(Instance.TestPrefix + Path.GetFileName(_folder), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));
        _host = EngineHost.Start(
            instance,
            NullLoggerFactory.Instance,
            hostOptions: new EngineHostOptions
            {
                Heartbeat = null,
                ProbeEncoders = false,
                AppLauncher = new StayingLauncher(),
                GameWindows = _probe,
                GameScanInterval = TimeSpan.FromMilliseconds(50),
            });
        _events = Channel.CreateUnbounded<IpcEvent>();
        _client = await EngineClient.ConnectAsync(_host.PipeName, new EngineClientOptions { OnEvent = e => _events.Writer.TryWrite(e) }, Token);

        // Past the first poll, so the games the tests add aren't a cold start.
        await _probe.PolledAsync(2);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _host.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task ADetectedGameBecomesTheTargetAndItsExitClearsIt()
    {
        var game = _probe.Add(GamePid, GamePath);
        _probe.Foreground = new GameWindow(0x10, GamePid);

        await NextAsync("game.detected");
        var detected = await NextAsync("target.changed");
        var expected = new TargetGame(GamePaths.Canonicalize(GamePath), "Game.EXE", GamePid);
        Assert.Equal(new TargetChanged(null, expected, TargetChangeReason.Detected), GameMessages.TargetChanged.Read(detected.Payload));
        Assert.Equal(expected.ProfileId.ToLowerInvariant(), expected.ProfileId);
        Assert.Equal(expected, _client.State!.TargetGame);

        game.HasExited = true;
        await NextAsync("game.exited");
        Assert.Empty(_client.State!.DetectedGames!);
        var exited = await NextAsync("target.changed");
        Assert.Equal(new TargetChanged(expected, null, TargetChangeReason.Exit), GameMessages.TargetChanged.Read(exited.Payload));
        Assert.Null(_client.State!.TargetGame);
    }

    [Fact]
    public async Task AHandAddedGameWithACaptionedWindowIsDetected()
    {
        var update = new SettingsUpdate(0, [SettingsOp.SetGlobal("manualGames", JsonSerializer.SerializeToElement<IReadOnlyList<string>>([GamePath], SettingsJsonContext.Default.IReadOnlyListString))]);
        await _client.SendAsync(SettingsMessages.Update, update, Token);
        await NextAsync("settings.changed");

        _probe.Add(GamePid, GamePath);
        _probe.AddWindow(new GameWindow(0x40, GamePid));

        var detected = GameMessages.Detected.Read((await NextAsync("game.detected")).Payload);
        var changed = GameMessages.TargetChanged.Read((await NextAsync("target.changed")).Payload);
        var expected = new DetectedGame(GamePaths.Canonicalize(GamePath), "Game.EXE", GamePid);
        Assert.Equal(expected, detected);
        Assert.Equal(new TargetChanged(null, new TargetGame(expected.ProfileId, expected.Name, GamePid), TargetChangeReason.Detected), changed);
        Assert.Equal([expected], _client.State!.DetectedGames!);
    }

    [Fact]
    public async Task TheTargetExitingHandsOverToABackgroundListedGame()
    {
        var update = new SettingsUpdate(0, [SettingsOp.SetGlobal("manualGames", JsonSerializer.SerializeToElement<IReadOnlyList<string>>([GamePath], SettingsJsonContext.Default.IReadOnlyListString))]);
        await _client.SendAsync(SettingsMessages.Update, update, Token);
        await NextAsync("settings.changed");
        var a = _probe.Add(GamePid, GamePath);
        _probe.Foreground = new GameWindow(0x10, GamePid);
        await NextAsync("game.detected");
        var target = GameMessages.TargetChanged.Read((await NextAsync("target.changed")).Payload).To;

        _probe.Add(GamePid + 2, GamePath);
        _probe.AddWindow(new GameWindow(0x50, GamePid + 2));
        var other = GameMessages.Detected.Read((await NextAsync("game.detected")).Payload);
        await _probe.PolledAsync(2);
        _probe.Foreground = null;
        a.HasExited = true;

        await NextAsync("game.exited");
        var changed = GameMessages.TargetChanged.Read((await NextAsync("target.changed")).Payload);
        Assert.Equal(new TargetChanged(target, new TargetGame(other.ProfileId, other.Name, GamePid + 2), TargetChangeReason.Exit), changed);
    }

    [Fact]
    public async Task TheAttachedAppAndTheEngineAreNeverProposed()
    {
        await _client.SendAsync(EngineMessages.Attach, new AppAttach(AppPid), Token);
        Assert.Equal("app.attached", (await NextAsync("app.attached")).Type);
        var app = _probe.Add(AppPid, GamePath);
        var engine = _probe.Add(Environment.ProcessId, GamePath);

        _probe.Foreground = new GameWindow(0x20, AppPid);
        await _probe.PolledAsync(3);
        _probe.Foreground = new GameWindow(0x30, Environment.ProcessId);
        await _probe.PolledAsync(3);

        Assert.Equal((0, 0), (app.Opens, engine.Opens));
        Assert.False(_events.Reader.TryRead(out _));
        Assert.Null(_host.Hub.State.TargetGame);
    }

    private async Task<IpcEvent> NextAsync(string type)
    {
        var next = await _events.Reader.ReadAsync(Token).AsTask().WaitAsync(Within, Token);
        Assert.Equal(type, next.Type);
        return next;
    }

    private sealed class FakeProbe : IGameWindowProbe
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<int, FakeProcess> _processes = [];
        private readonly List<GameWindow> _windows = [];
        private GameWindow? _foreground;
        private int _polls;

        public GameWindow? Foreground
        {
            get
            {
                lock (_lock)
                {
                    return _foreground;
                }
            }

            set
            {
                lock (_lock)
                {
                    _foreground = value;
                }
            }
        }

        public FakeProcess Add(int pid, string imagePath)
        {
            lock (_lock)
            {
                return _processes[pid] = new FakeProcess(imagePath);
            }
        }

        public void AddWindow(GameWindow window)
        {
            lock (_lock)
            {
                _windows.Add(window);
            }
        }

        public GameWindow? ForegroundGameWindow()
        {
            Interlocked.Increment(ref _polls);
            return Foreground;
        }

        public GameWindow? ForegroundWindow() => Foreground;

        public IReadOnlyList<GameWindow> Windows()
        {
            lock (_lock)
            {
                return [.. _windows];
            }
        }

        public IGameProcess? Open(int processId)
        {
            lock (_lock)
            {
                if (!_processes.TryGetValue(processId, out var process))
                {
                    return null;
                }

                Interlocked.Increment(ref process.Opens);
                return process;
            }
        }

        /// <summary>Waits until the monitor has polled <paramref name="count"/> more times.</summary>
        public async Task PolledAsync(int count)
        {
            var target = Volatile.Read(ref _polls) + count;
            while (Volatile.Read(ref _polls) < target)
            {
                await Task.Delay(20, Token);
            }
        }
    }

    private sealed class FakeProcess(string imagePath) : IGameProcess
    {
        public int Opens;
        private volatile bool _hasExited;

        public string ImagePath { get; } = imagePath;

        public DateTime StartTime { get; } = DateTime.UtcNow;

        public bool HasExited
        {
            get => _hasExited;
            set => _hasExited = value;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>An App launcher whose Apps never exit.</summary>
    private sealed class StayingLauncher : IAppLauncher
    {
        public IWatchedProcess? Open(int pid) => new Staying(pid);

        public IWatchedProcess Start() => new Staying(0);

        private sealed class Staying(int id) : IWatchedProcess
        {
            public int Id { get; } = id;

            public Task<int> Exited { get; } = new TaskCompletionSource<int>().Task;

            public void Dispose()
            {
            }
        }
    }
}
