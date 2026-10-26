using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Engine.Supervision;

namespace ReplayBuffer.Engine.Tests;

/// <summary>The engine's half of supervision, in process, with a fake App launcher.</summary>
public sealed class AppSupervisorTests : IAsyncLifetime
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-sup-" + Guid.NewGuid().ToString("N"));
    private readonly FakeLauncher _launcher = new();
    private EngineHost _host = null!;
    private EngineClient _client = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        var instance = new Instance(Instance.TestPrefix + Path.GetFileName(_folder), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));
        _host = EngineHost.Start(instance, NullLoggerFactory.Instance, hostOptions: new EngineHostOptions { Heartbeat = null, AppLauncher = _launcher });
        _client = await EngineClient.ConnectAsync(_host.PipeName, cancellationToken: Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
        await _host.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    [Theory]
    [InlineData(EngineRestartReason.Crashed)]
    [InlineData(EngineRestartReason.Hung)]
    public async Task AnAttachWithAReasonRaisesEngineRestarted(EngineRestartReason reason)
    {
        await _client.SendAsync(EngineMessages.Attach, new AppAttach(100, reason), Token);

        await Eventually(() => _client.State.AppPid == 100);
        var warning = Assert.Single(_client.State.Warnings);
        Assert.Equal((ErrorCodes.EngineRestarted, "supervision", WarningSeverity.Warning, false, true), (warning.Code, warning.Source, warning.Severity, warning.Overlay, warning.Dismissable));
    }

    [Fact]
    public async Task AnAttachWithNoReasonRaisesNothing()
    {
        await _client.SendAsync(EngineMessages.Attach, new AppAttach(100), Token);

        await Eventually(() => _client.State.AppPid == 100);
        Assert.Empty(_client.State.Warnings);
    }

    [Fact]
    public async Task AnAttachedAppThatExitsIsRelaunchedAndRaisesAppRestarted()
    {
        await _client.SendAsync(EngineMessages.Attach, new AppAttach(100), Token);
        await Eventually(() => _launcher.Opened.ContainsKey(100));

        _launcher.Opened[100].Exit(unchecked((int)0xC0000005));

        await Eventually(() => _launcher.Started.Count == 1);
        await Eventually(() => _client.State.Warnings.Any(w => w.Code == ErrorCodes.AppRestarted));

        // The relaunched App attaches; when it dies too, it is relaunched again (after the 1 s backoff).
        var relaunched = _launcher.Started.Single();
        await _client.SendAsync(EngineMessages.Attach, new AppAttach(relaunched.Id), Token);
        await Eventually(() => _client.State.AppPid == relaunched.Id);
        relaunched.Exit(0);
        await Eventually(() => _launcher.Started.Count == 2);
    }

    [Fact]
    public async Task AStartedAppThatExitsCleanlyBeforeAttachingIsNotRelaunched()
    {
        await _client.SendAsync(EngineMessages.Attach, new AppAttach(100), Token);
        await Eventually(() => _launcher.Opened.ContainsKey(100));
        _launcher.Opened[100].Exit(1);
        await Eventually(() => _launcher.Started.Count == 1);

        // Another copy holds the App mutex, so the started one exits 0.
        _launcher.Started.Single().Exit(0);
        await Task.Delay(1500, Token);

        Assert.Single(_launcher.Started);
    }

    [Fact]
    public async Task AFailedStartIsRetriedOnBackoff()
    {
        _launcher.FailStarts = 1;
        await _client.SendAsync(EngineMessages.Attach, new AppAttach(100), Token);
        await Eventually(() => _launcher.Opened.ContainsKey(100));

        _launcher.Opened[100].Exit(1);

        await Eventually(() => _launcher.Started.Count == 1);
        Assert.Equal(2, _launcher.StartCalls);
    }

    [Fact]
    public async Task AfterShutdownAnAppThatExitsIsNotRelaunched()
    {
        await _client.SendAsync(EngineMessages.Attach, new AppAttach(100), Token);
        await Eventually(() => _launcher.Opened.ContainsKey(100));

        await _client.SendAsync(EngineMessages.Shutdown, Empty.Value, Token);
        _launcher.Opened[100].Exit(0);
        await Task.Delay(500, Token);

        Assert.Empty(_launcher.Started);
        Assert.DoesNotContain(_client.State.Warnings, w => w.Code == ErrorCodes.AppRestarted);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(10), "the condition never held");
            await Task.Delay(10, Token);
        }
    }

    private sealed class FakeLauncher : IAppLauncher
    {
        private int _nextPid = 1000;
        private int _startCalls;

        public ConcurrentDictionary<int, FakeProcess> Opened { get; } = new();

        public ConcurrentQueue<FakeProcess> Started { get; } = new();

        public int FailStarts { get; set; }

        public int StartCalls => Volatile.Read(ref _startCalls);

        public IWatchedProcess? Open(int pid) => Opened.GetOrAdd(pid, id => new FakeProcess(id));

        public IWatchedProcess Start()
        {
            if (Interlocked.Increment(ref _startCalls) <= FailStarts)
            {
                throw new InvalidOperationException("The App didn't start.");
            }

            var process = new FakeProcess(Interlocked.Increment(ref _nextPid));
            Started.Enqueue(process);
            return process;
        }
    }

    private sealed class FakeProcess(int id) : IWatchedProcess
    {
        private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id { get; } = id;

        public Task<int> Exited => _exited.Task;

        public void Exit(int code) => _exited.TrySetResult(code);

        public void Dispose()
        {
        }
    }
}
