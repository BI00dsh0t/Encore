using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;
using ReplayBuffer.Engine.Ipc;
using ReplayBuffer.Engine.Settings;

namespace ReplayBuffer.Engine.Tests;

/// <summary>The IPC server and client in one process, over a real pipe of a throwaway test instance.</summary>
public sealed class IpcTests : IAsyncLifetime
{
    private static readonly CaptureState Buffering = CaptureState.Initial with { Replay = ReplayState.Buffering };
    private static readonly CaptureState Recording = Buffering with { Recording = RecordingState.Running };

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-ipc-" + Guid.NewGuid().ToString("N"));
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
    public async Task TheSnapshotComesFirstAndEventsFollowInSeqOrder()
    {
        StartHost();
        _host.Hub.Publish(CaptureMessages.StateChanged, Buffering);
        _host.Hub.Publish(CaptureMessages.StateChanged, Recording);
        _host.Hub.Publish(CaptureMessages.StateChanged, Buffering);

        var (client, events) = await ConnectAsync();

        Assert.Equal(3, client.State.Seq);
        Assert.Equal(Buffering, client.State.Capture);
        Assert.Equal(Environment.ProcessId, client.State.Pid);

        _host.Hub.Publish(CaptureMessages.StateChanged, Recording);
        _host.Hub.Publish(FeedbackMessages.Raised, NewWarning("w1"));

        var fourth = await NextAsync(events);
        var fifth = await NextAsync(events);
        Assert.Equal((4L, 5L), (fourth.Seq, fifth.Seq));
        Assert.Equal(Recording, client.State.Capture);
        Assert.Equal("w1", Assert.Single(client.State.Warnings).Id);
        Assert.Equal(_host.Hub.State, client.State with { Settings = _host.Hub.State.Settings, Warnings = _host.Hub.State.Warnings, DetectedGames = _host.Hub.State.DetectedGames, Library = _host.Hub.State.Library });
    }

    [Fact]
    public async Task ACommandsEventsArriveBeforeItsReply()
    {
        StartHost();
        var order = new List<string>();
        var (client, _) = await ConnectAsync(e =>
        {
            lock (order)
            {
                order.Add(e.Type + "#" + e.Seq);
            }
        });

        var result = await client.SendAsync(SettingsMessages.Update, SetSaveCooldown(0, 7), Token);
        lock (order)
        {
            order.Add("reply");
        }

        Assert.Equal(1, result.Revision);
        Assert.Equal(["settings.changed#1", "reply"], order);
        Assert.Equal(1, client.State.Settings.Revision);
        Assert.Equal(7, client.State.Settings.Global["saveCooldown"].GetInt32());
    }

    [Fact]
    public async Task ARefusedCommandRepliesWithItsCodeAndPublishesNothing()
    {
        StartHost();
        var (client, _) = await ConnectAsync();

        var e = await Assert.ThrowsAsync<ErrorCodeException>(() => client.SendAsync(SettingsMessages.Update, SetSaveCooldown(5, 7), Token));

        Assert.Equal(ErrorCodes.SettingsConflict, e.Code);
        Assert.Equal(0, _host.Hub.State.Seq);
    }

    [Fact]
    public async Task AnUnknownCommandRepliesCommandUnknown()
    {
        StartHost();
        var (client, _) = await ConnectAsync();
        var unknown = new CommandType<Empty, Empty>("test.unknown", EngineJsonContext.Default.Empty, EngineJsonContext.Default.Empty);

        var e = await Assert.ThrowsAsync<ErrorCodeException>(() => client.SendAsync(unknown, Empty.Value, Token));

        Assert.Equal(ErrorCodes.CommandUnknown, e.Code);
    }

    [Theory]
    [InlineData("""{"v":1,"id":"1","type":"settings.update","payload":{"ops":[]}}""")]
    [InlineData("""{"v":1,"id":"1","type":"settings.update"}""")]
    public async Task ABadPayloadOrVersionRepliesInvalidArguments(string frame)
    {
        StartHost();
        await using var raw = await RawClient.ConnectAsync(_host.PipeName);
        Assert.Contains("\"state.snapshot\"", await raw.ReadLineAsync());

        await raw.WriteLineAsync(frame);
        using var reply = JsonDocument.Parse(await raw.ReadLineAsync() ?? "null");

        Assert.Equal("1", reply.RootElement.GetProperty("id").GetString());
        Assert.Equal(ErrorCodes.InvalidArguments, reply.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.False(_host.ShutdownRequested.IsCompleted);
    }

    [Fact]
    public async Task AHandlerCrashRepliesCommandFailedAndTheConnectionStays()
    {
        StartHost();
        var crash = new CommandType<Empty, Empty>("test.crash", EngineJsonContext.Default.Empty, EngineJsonContext.Default.Empty);
        _host.Router.Register(crash, (_, _) => throw new InvalidOperationException("boom"));
        var (client, _) = await ConnectAsync();

        var e = await Assert.ThrowsAsync<ErrorCodeException>(() => client.SendAsync(crash, Empty.Value, Token));

        Assert.Equal(ErrorCodes.CommandFailed, e.Code);
        Assert.Equal(1, (await client.SendAsync(SettingsMessages.Update, SetSaveCooldown(0, 3), Token)).Revision);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("""{"v":1,"type":"settings.update","payload":{}}""")]
    public async Task AMalformedFrameDropsOnlyThatConnection(string frame)
    {
        StartHost();
        await using var raw = await RawClient.ConnectAsync(_host.PipeName);
        Assert.Contains("\"state.snapshot\"", await raw.ReadLineAsync());

        await raw.WriteLineAsync(frame);

        Assert.Null(await raw.ReadLineAsync());
        var (client, _) = await ConnectAsync();
        Assert.Equal(1, (await client.SendAsync(SettingsMessages.Update, SetSaveCooldown(0, 3), Token)).Revision);
    }

    [Fact]
    public async Task APendingCommandFailsWithEngineDisconnectedWhenTheServerGoes()
    {
        StartHost();
        var (client, _) = await ConnectAsync();
        var pending = client.SendAsync(EngineHost.DebugHold, Empty.Value, Token);
        await Task.Delay(100, Token);
        Assert.False(pending.IsCompleted);

        await _host.DisposeAsync();

        var e = await Assert.ThrowsAsync<ErrorCodeException>(() => pending.WaitAsync(TimeSpan.FromSeconds(1), Token));
        Assert.Equal(ErrorCodes.EngineDisconnected, e.Code);
        Assert.False(client.IsConnected);
        var later = await Assert.ThrowsAsync<ErrorCodeException>(() => client.SendAsync(SettingsMessages.Update, SetSaveCooldown(0, 1), Token));
        Assert.Equal(ErrorCodes.EngineDisconnected, later.Code);
    }

    [Fact]
    public async Task AStalledClientIsDroppedWithoutHoldingUpTheEngineOrOtherClients()
    {
        StartHost();
        var (client, events) = await ConnectAsync();
        await using var stalled = await RawClient.ConnectAsync(_host.PipeName);
        await Eventually(() => _host.Server.ConnectionCount == 2);

        var slowest = TimeSpan.Zero;
        var watch = new Stopwatch();
        for (var batch = 0; batch < 10; batch++)
        {
            long seq = 0;
            for (var i = 0; i < 500; i++)
            {
                watch.Restart();
                seq = _host.Hub.Publish(CaptureMessages.StateChanged, i % 2 == 0 ? Buffering : Recording);
                slowest = TimeSpan.FromTicks(Math.Max(slowest.Ticks, watch.Elapsed.Ticks));
            }

            await Eventually(() => client.State.Seq == seq);
        }

        Assert.True(slowest < TimeSpan.FromMilliseconds(50), $"the slowest Publish took {slowest.TotalMilliseconds} ms");
        await Eventually(() => _host.Server.ConnectionCount == 1);
        Assert.True(client.IsConnected);

        var seqs = new List<long>();
        while (events.Reader.TryRead(out var e))
        {
            seqs.Add(e.Seq);
        }

        Assert.Equal(Enumerable.Range(1, 5000).Select(i => (long)i), seqs);
    }

    [Fact]
    public async Task ASecondServerForTheSamePipeFailsWithIpcPipeInUse()
    {
        StartHost();

        var e = Assert.Throws<ErrorCodeException>(() => new IpcServer(_host.PipeName, new IpcServerOptions(), NullLogger.Instance));

        Assert.Equal(ErrorCodes.IpcPipeInUse, e.Code);
        var (client, _) = await ConnectAsync();
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task AClientThatLeavesBeforeItIsAcceptedDoesNotStopTheServerAccepting()
    {
        // The bug that bit (full-run-flaky-tests): an App's connect attempt that timed out on a loaded
        // machine left before the server's wait took it, and the accept loop ended for good.
        StartHost();
        var pipe = _host.PipeName + "-early";
        await using var server = new IpcServer(pipe, new IpcServerOptions(), NullLogger.Instance);
        using (var early = new NamedPipeClientStream(".", pipe, PipeDirection.InOut))
        {
            await early.ConnectAsync(Token);
        }

        server.Start(_host.Hub, _host.Router);

        await using var client = await EngineClient.ConnectAsync(pipe, cancellationToken: Token);
        Assert.True(client.IsConnected);
    }

    [Fact]
    public async Task DismissingAWarningPublishesWarningDismissed()
    {
        StartHost();
        var warning = _host.Warnings.Raise("test-code", WarningSeverity.Warning, "test", "m", overlay: true);
        var fixedWarning = _host.Warnings.Raise("test-fixed", WarningSeverity.Error, "test", "m", overlay: true, dismissable: false);
        var (client, events) = await ConnectAsync();
        Assert.Equal(2, client.State.Warnings.Count);

        await client.SendAsync(FeedbackMessages.Dismiss, new WarningDismiss(warning.Id), Token);

        Assert.Equal("warning.dismissed", (await NextAsync(events)).Type);
        Assert.Equal(fixedWarning.Id, Assert.Single(client.State.Warnings).Id);
        Assert.Equal(
            ErrorCodes.WarningNotFound,
            (await Assert.ThrowsAsync<ErrorCodeException>(() => client.SendAsync(FeedbackMessages.Dismiss, new WarningDismiss(warning.Id), Token))).Code);
        Assert.Equal(
            ErrorCodes.WarningNotDismissable,
            (await Assert.ThrowsAsync<ErrorCodeException>(() => client.SendAsync(FeedbackMessages.Dismiss, new WarningDismiss(fixedWarning.Id), Token))).Code);
    }

    [Fact]
    public async Task AServerThatFailsTheCheckIsRefused()
    {
        StartHost();

        var e = await Assert.ThrowsAsync<ErrorCodeException>(() =>
            EngineClient.ConnectAsync(_host.PipeName, new EngineClientOptions { VerifyServer = _ => false }, Token));

        Assert.Equal(ErrorCodes.EngineUntrusted, e.Code);
    }

    [Fact]
    public async Task NoEngineFailsTheConnectWithEngineDisconnected()
    {
        var e = await Assert.ThrowsAsync<ErrorCodeException>(() =>
            EngineClient.ConnectAsync(PipeName.ForCurrentUser(TestInstance.Name), new EngineClientOptions { ConnectTimeout = TimeSpan.FromMilliseconds(200) }, Token));

        Assert.Equal(ErrorCodes.EngineDisconnected, e.Code);
    }

    [Fact]
    public async Task AnUnreadableSettingsFileIsAWarningInTheSnapshot()
    {
        Directory.CreateDirectory(TestInstance.RoamingDataFolder);
        await File.WriteAllTextAsync(Path.Combine(TestInstance.RoamingDataFolder, SettingsStore.FileName), "{ not json", Token);
        StartHost();

        var (client, _) = await ConnectAsync();

        var warning = Assert.Single(client.State.Warnings);
        Assert.Equal((ErrorCodes.SettingsUnreadable, "settings", true), (warning.Code, warning.Source, warning.Dismissable));
    }

    [Fact]
    public async Task ShutdownIsRequestedAfterItsReply()
    {
        StartHost();
        var (client, _) = await ConnectAsync();

        await client.SendAsync(EngineMessages.Shutdown, Empty.Value, Token);

        await _host.ShutdownRequested.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private static SettingsUpdate SetSaveCooldown(long expectedRevision, int seconds) =>
        new(expectedRevision, [SettingsOp.SetGlobal("saveCooldown", JsonSerializer.SerializeToElement(seconds, SettingsJsonContext.Default.Int32))]);

    private static Warning NewWarning(string id) =>
        new(id, "test-code", WarningSeverity.Info, "test", "m", DateTimeOffset.UtcNow, false, null, true);

    private static async Task<IpcEvent> NextAsync(Channel<IpcEvent> events) =>
        await events.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(15), "the condition never held");
            await Task.Delay(10, Token);
        }
    }

    private void StartHost(IpcServerOptions? options = null)
    {
        // No heartbeat and a fixed device list: these tests count every event and seq.
        _host = EngineHost.Start(
            TestInstance,
            NullLoggerFactory.Instance,
            options,
            new EngineHostOptions { Heartbeat = null, ProbeEncoders = false, AudioEndpoints = new FakeEndpointSource() });
        _disposables.Add(_host);
    }

    private async Task<(EngineClient Client, Channel<IpcEvent> Events)> ConnectAsync(Action<IpcEvent>? onEvent = null)
    {
        var events = Channel.CreateUnbounded<IpcEvent>();
        var client = await EngineClient.ConnectAsync(
            _host.PipeName,
            new EngineClientOptions
            {
                OnEvent = e =>
                {
                    onEvent?.Invoke(e);
                    events.Writer.TryWrite(e);
                },
            },
            Token);
        _disposables.Add(client);
        return (client, events);
    }

    /// <summary>A bare pipe client that sends and reads lines as they are.</summary>
    private sealed class RawClient : IAsyncDisposable
    {
        private readonly NamedPipeClientStream _pipe;
        private readonly StreamReader _reader;

        private RawClient(NamedPipeClientStream pipe)
        {
            _pipe = pipe;
            _reader = new StreamReader(pipe, Encoding.UTF8);
        }

        public static async Task<RawClient> ConnectAsync(string pipeName)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(TimeSpan.FromSeconds(5), Token);
            return new RawClient(pipe);
        }

        public async Task<string?> ReadLineAsync()
        {
            try
            {
                return await _reader.ReadLineAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(10), Token);
            }
            catch (IOException)
            {
                return null;
            }
        }

        public async Task WriteLineAsync(string line)
        {
            await _pipe.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"), Token);
        }

        public async ValueTask DisposeAsync()
        {
            _reader.Dispose();
            await _pipe.DisposeAsync();
        }
    }
}
