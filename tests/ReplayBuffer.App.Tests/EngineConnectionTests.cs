using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ReplayBuffer.App.Engine;
using ReplayBuffer.App.Overlay;
using ReplayBuffer.App.Settings;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.App.Tests;

/// <summary>
/// The App's connection and settings sender against the real engine, launched by the connection on a
/// throwaway test instance.
/// </summary>
public sealed class EngineConnectionTests : IAsyncDisposable
{
    private readonly string _instance = "ReplayBuffer-Test-" + Guid.NewGuid().ToString("N");
    private readonly List<EngineConnection> _connections = [];
    private readonly List<int> _enginePids = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string EngineExe => Path.Combine(AppContext.BaseDirectory, "ReplayBuffer.Engine.exe");

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in _connections)
        {
            await connection.DisposeAsync();
        }

        foreach (var pid in _enginePids.Distinct())
        {
            try
            {
                using var engine = Process.GetProcessById(pid);
                if (engine.ProcessName == "ReplayBuffer.Engine")
                {
                    engine.Kill();
                    await engine.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (ArgumentException)
            {
                // Already gone.
            }
        }

        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            var folder = Path.Combine(Environment.GetFolderPath(root), _instance);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TheSenderCommitsAnUpdateSeesSettingsChangedAndGetsAConflictOnAStaleRevision()
    {
        var connection = await ConnectAsync();
        var observed = new TaskCompletionSource<SettingsChanged>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var observer = await EngineClient.ConnectAsync(
            PipeName.ForCurrentUser(_instance),
            new EngineClientOptions
            {
                OnEvent = e =>
                {
                    if (e.Type == SettingsMessages.Changed.Name)
                    {
                        observed.TrySetResult(SettingsMessages.Changed.Read(e.Payload));
                    }
                },
            },
            Token);
        var sender = new SettingsSender(connection);
        var basis = connection.State!.Settings;
        Assert.Equal(0, basis.Revision);

        var committed = await sender.UpdateAsync(basis, [SettingsValues.SetGlobal(Keys.StartWithWindows, true)], Token);

        Assert.Equal(new SettingsSendResult.Committed(1), committed);
        var changed = await observed.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(1, changed.Revision);
        Assert.True(SettingsValues.Global(changed.Document, Keys.StartWithWindows));
        Assert.True(SettingsValues.Global(connection.State.Settings, Keys.StartWithWindows));

        var stale = await sender.UpdateAsync(basis, [SettingsValues.SetGlobal(Keys.StartWithWindows, false)], Token);

        var conflict = Assert.IsType<SettingsSendResult.Conflict>(stale);
        Assert.Equal(1, conflict.Current.Revision);
        Assert.True(SettingsValues.Global(conflict.Current, Keys.StartWithWindows));

        var refused = await sender.UpdateAsync(conflict.Current, [SettingsValues.SetGlobal(Keys.BufferWindow, 0)], Token);

        Assert.Equal(ErrorCodes.SettingInvalidValue, Assert.IsType<SettingsSendResult.Failed>(refused).Code);
        Assert.Equal(1, connection.State.Settings.Revision);
    }

    [Fact]
    public async Task ShutdownEndsTheEngineProcess()
    {
        var connection = await ConnectAsync();
        using var engine = Process.GetProcessById(connection.State!.Pid);

        Assert.True(await connection.ShutdownAsync(TimeSpan.FromSeconds(5)));

        Assert.True(engine.HasExited);
        Assert.Equal(EngineStatus.Disconnected, connection.Status);
    }

    [Fact]
    public async Task ShutdownGivesUpWhenTheEngineDoesNotEndInTime()
    {
        var connection = await ConnectAsync();

        Assert.False(await connection.ShutdownAsync(TimeSpan.Zero));
    }

    [Fact]
    public async Task ASecondAppConnectsToTheRunningEngineWithoutLaunchingOne()
    {
        var first = await ConnectAsync();

        var second = await ConnectAsync(enginePath: Path.Combine(AppContext.BaseDirectory, "missing", "ReplayBuffer.Engine.exe"));

        Assert.Equal(first.State!.Pid, second.State!.Pid);
        Assert.Equal(first.State.EngineInstanceId, second.State.EngineInstanceId);
    }

    [Fact]
    public async Task WithNoEngineToConnectToOrLaunchTheStatusIsNotRunning()
    {
        var connection = Create(Path.Combine(AppContext.BaseDirectory, "missing", "ReplayBuffer.Engine.exe"));

        await connection.ConnectAsync(Token);

        Assert.Equal(EngineStatus.NotRunning, connection.Status);
        Assert.Contains("couldn't be started", connection.Problem);
        Assert.Null(connection.State);
        var e = await Assert.ThrowsAsync<ErrorCodeException>(() => connection.SendAsync(EngineMessages.Shutdown, Empty.Value, Token));
        Assert.Equal(ErrorCodes.EngineDisconnected, e.Code);
        Assert.True(await connection.ShutdownAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task ASupervisedEngineThatDiesIsRelaunchedAndTheNewEngineRaisesEngineRestarted()
    {
        var connection = Create(EngineExe);
        connection.StartSupervising();
        await Eventually(() => connection.Status == EngineStatus.Connected && connection.State?.AppPid == Environment.ProcessId);
        var first = connection.State!.Pid;
        _enginePids.Add(first);

        using (var engine = Process.GetProcessById(first))
        {
            engine.Kill();
        }

        await Eventually(() => connection.Status == EngineStatus.Connected && connection.State?.Pid != first
            && connection.State!.Warnings.Any(w => w.Code == ErrorCodes.EngineRestarted));
        _enginePids.Add(connection.State!.Pid);
        var sender = new SettingsSender(connection);
        var committed = await sender.UpdateAsync(connection.State.Settings, [SettingsValues.SetGlobal(Keys.StartWithWindows, true)], Token);
        Assert.IsType<SettingsSendResult.Committed>(committed);

        using var second = Process.GetProcessById(connection.State.Pid);
        Assert.True(await connection.ShutdownAsync(TimeSpan.FromSeconds(5)));
        Assert.True(second.HasExited);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AFourthCrashWithinTenMinutesIsACrashLoopUntilRestart()
    {
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var connection = Create(EngineExe, log.Enqueue);
        connection.StartSupervising();
        for (var kill = 1; kill <= Supervision.CrashLoopRestarts + 1; kill++)
        {
            await Eventually(() => connection.Status == EngineStatus.Connected && connection.State?.AppPid == Environment.ProcessId);
            var pid = connection.State!.Pid;
            _enginePids.Add(pid);
            using var engine = Process.GetProcessById(pid);
            engine.Kill();
            await engine.WaitForExitAsync(Token);

            // Wait for the connection to leave the killed engine before the next round reads its state.
            await Eventually(() => connection.Status != EngineStatus.Connected || connection.State?.Pid != pid);
        }

        await Eventually(() => connection.Status == EngineStatus.CrashLoop);
        Assert.Contains(log, line => line.Contains(ErrorCodes.EngineCrashLoop, StringComparison.Ordinal));
        Assert.Contains(ErrorCodes.EngineCrashLoop, connection.Problem);
        var none = await Assert.ThrowsAsync<ErrorCodeException>(() =>
            EngineClient.ConnectAsync(PipeName.ForCurrentUser(_instance), new EngineClientOptions { ConnectTimeout = TimeSpan.FromMilliseconds(500) }, Token));
        Assert.Equal(ErrorCodes.EngineDisconnected, none.Code);

        connection.RestartAfterCrashLoop();

        await Eventually(() => connection.Status == EngineStatus.Connected && connection.State?.AppPid == Environment.ProcessId);
        _enginePids.Add(connection.State!.Pid);
        Assert.DoesNotContain(connection.State.Warnings, w => w.Code == ErrorCodes.EngineRestarted);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AnEngineThatCrashesBeforeServingEndsInACrashLoop()
    {
        // An "engine" that exits 1 at once: every launch fails before the pipe serves.
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var connection = new EngineConnection(new EngineConnectionOptions
        {
            InstanceName = _instance,
            EnginePath = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            EngineArguments = ["/c", "exit", "1"],
            AttemptTimeout = TimeSpan.FromMilliseconds(200),
            Log = log.Enqueue,
        });
        _connections.Add(connection);
        connection.StartSupervising();

        await Eventually(() => connection.Status == EngineStatus.CrashLoop);

        Assert.Contains(log, line => line.Contains(ErrorCodes.EngineCrashLoop, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AServerThatIsNotTheTrustedEngineIsRefusedAndNeverLaunchedOver()
    {
        // The real engine serves the pipe, but the App trusts only an image elsewhere.
        var trusted = await ConnectAsync();
        var connection = new EngineConnection(new EngineConnectionOptions
        {
            InstanceName = _instance,
            // A launch would replace the problem with "couldn't be started".
            EnginePath = Path.Combine(AppContext.BaseDirectory, "missing", "ReplayBuffer.Engine.exe"),
            EngineArguments = ["serve", "--instance", _instance],
            TrustedEnginePath = Path.Combine(AppContext.BaseDirectory, "elsewhere", "ReplayBuffer.Engine.exe"),
        });
        _connections.Add(connection);

        await connection.ConnectAsync(Token);

        Assert.Equal(EngineStatus.NotRunning, connection.Status);
        Assert.Contains(ErrorCodes.EngineUntrusted, connection.Problem);
        Assert.Null(connection.State);
        Assert.Equal(EngineStatus.Connected, trusted.Status);
    }

    [Fact]
    public async Task TheSimulatorsDiskFullShowsTheRedXUntilARecordingStarts()
    {
        var connection = await ConnectAsync();
        // One action at a time, as the UI thread would run them: session events and the message timer both post.
        var gate = new Lock();
        var model = new OverlayViewModel(connection, action =>
        {
            lock (gate)
            {
                action();
            }
        });

        await connection.SendAsync(DevMessages.Simulate, new DevSimulate(SimulatedEvent.DiskFull), Token);
        await Eventually(() => model.DiskFull);

        await connection.SendAsync(DevMessages.Simulate, new DevSimulate(SimulatedEvent.RecordingStarted), Token);
        await Eventually(() => !model.DiskFull && model.Dot == OverlayDot.Solid);
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(15), "the condition never held");
            await Task.Delay(20, Token);
        }
    }

    private EngineConnection Create(string enginePath, Action<string>? log = null)
    {
        var connection = new EngineConnection(new EngineConnectionOptions
        {
            InstanceName = _instance,
            EnginePath = enginePath,
            EngineArguments = ["serve", "--instance", _instance],
            LaunchTimeout = TimeSpan.FromSeconds(30),
            Log = log,
        });
        _connections.Add(connection);
        return connection;
    }

    private async Task<EngineConnection> ConnectAsync(string? enginePath = null)
    {
        var connection = Create(enginePath ?? EngineExe);
        await connection.ConnectAsync(Token);
        Assert.True(connection.Status == EngineStatus.Connected, connection.Problem);
        _enginePids.Add(connection.State!.Pid);
        return connection;
    }
}
