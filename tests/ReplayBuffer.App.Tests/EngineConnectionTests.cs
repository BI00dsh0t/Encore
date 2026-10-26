using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ReplayBuffer.App.Engine;
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
    public async Task AnEngineThatDiesLeavesTheAppDisconnectedAndQuitStillEnds()
    {
        var connection = await ConnectAsync();
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.Changed += () =>
        {
            if (connection.Status == EngineStatus.Disconnected)
            {
                disconnected.TrySetResult();
            }
        };

        using (var engine = Process.GetProcessById(connection.State!.Pid))
        {
            engine.Kill();
        }

        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5), Token);
        var sender = new SettingsSender(connection);
        var failed = await sender.UpdateAsync(connection.State.Settings, [SettingsValues.SetGlobal(Keys.StartWithWindows, true)], Token);
        Assert.Equal(ErrorCodes.EngineDisconnected, Assert.IsType<SettingsSendResult.Failed>(failed).Code);
        Assert.True(await connection.ShutdownAsync(TimeSpan.FromSeconds(5)));
    }

    private EngineConnection Create(string enginePath)
    {
        var connection = new EngineConnection(new EngineConnectionOptions
        {
            InstanceName = _instance,
            EnginePath = enginePath,
            EngineArguments = ["serve", "--instance", _instance],
            LaunchTimeout = TimeSpan.FromSeconds(30),
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
