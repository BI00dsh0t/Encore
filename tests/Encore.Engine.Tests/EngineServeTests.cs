using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Engine.Hosting;

namespace Encore.Engine.Tests;

/// <summary>Runs the built engine's <c>serve</c> on a throwaway test instance and talks to it over its pipe.</summary>
public sealed class EngineServeTests : IDisposable
{
    private readonly string _instance = ServeCommand.TestInstancePrefix + Guid.NewGuid().ToString("N");
    private readonly List<Process> _processes = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string EngineExe => Path.Combine(EngineBinaries.Folder, "Encore.Engine.exe");

    public void Dispose()
    {
        foreach (var process in _processes)
        {
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(5000);
            }

            process.Dispose();
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
    public async Task TheEngineServesItsSnapshotAndCommandsAndAKillFailsThePendingCommand()
    {
        var engine = await StartServeAsync();
        var settingsChanged = new TaskCompletionSource<IpcEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var client = await EngineClient.ConnectAsync(
            PipeName.ForCurrentUser(_instance),
            new EngineClientOptions
            {
                // Heartbeats come and go between these; only settings.changed matters here.
                OnEvent = e =>
                {
                    if (e.Type == SettingsMessages.Changed.Name)
                    {
                        settingsChanged.TrySetResult(e);
                    }
                },
            },
            Token);

        Assert.Equal(engine.Id, client.State.Pid);
        Assert.Equal(0, client.State.Settings.Revision);
        var seqBefore = client.State.Seq;

        var update = new SettingsUpdate(0, [SettingsOp.SetGlobal("saveCooldown", JsonSerializer.SerializeToElement(9, SettingsJsonContext.Default.Int32))]);
        Assert.Equal(1, (await client.SendAsync(SettingsMessages.Update, update, Token)).Revision);
        var changed = await settingsChanged.Task;
        Assert.True(changed.Seq > seqBefore);
        Assert.Equal(1, client.State.Settings.Revision);
        Assert.True(File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), _instance, "settings.json")));

        var pending = client.SendAsync(DebugCommands.Hold, Empty.Value, Token);
        await Task.Delay(200, Token);
        Assert.False(pending.IsCompleted);

        // Kill only starts the termination, so wait for the process, then give the client's read a generous bound.
        engine.Kill();
        await engine.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(30), Token);

        var e = await Assert.ThrowsAsync<ErrorCodeException>(() => pending.WaitAsync(TimeSpan.FromSeconds(30), Token));
        Assert.Equal(ErrorCodes.EngineDisconnected, e.Code);
    }

    [Fact]
    public async Task ASecondEngineForTheSameInstanceFailsWithInstanceRunning()
    {
        var first = await StartServeAsync();

        using var second = Start();
        var stderr = await second.StandardError.ReadToEndAsync(Token);
        await second.WaitForExitAsync(Token);

        Assert.Equal(1, second.ExitCode);
        Assert.Contains("error instance-running", stderr);
        Assert.False(first.HasExited);
        await using var client = await EngineClient.ConnectAsync(PipeName.ForCurrentUser(_instance), cancellationToken: Token);
        Assert.Equal(first.Id, client.State.Pid);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task TheHeartbeatArrivesEveryTwoSecondsAndStopsOnDebugHang()
    {
        await StartServeAsync();
        var heartbeats = Channel.CreateUnbounded<DateTimeOffset>();
        await using var client = await EngineClient.ConnectAsync(
            PipeName.ForCurrentUser(_instance),
            new EngineClientOptions
            {
                OnEvent = e =>
                {
                    if (e.Type == EngineMessages.Heartbeat.Name)
                    {
                        heartbeats.Writer.TryWrite(DateTimeOffset.UtcNow);
                    }
                },
            },
            Token);

        var first = await heartbeats.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        var second = await heartbeats.Reader.ReadAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Token);
        var gap = second - first;
        Assert.InRange(gap, TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(2.5));
        Assert.NotNull(client.State.HeartbeatAt);

        var hang = client.SendAsync(DebugCommands.Hang, Empty.Value, Token);
        await Task.Delay(200, Token);
        while (heartbeats.Reader.TryRead(out _))
        {
        }

        await Task.Delay(Contracts.Supervision.SilenceLimit / 2, Token);
        Assert.False(heartbeats.Reader.TryRead(out _), "a heartbeat arrived after debug.hang");
        Assert.False(hang.IsCompleted);
    }

    [Fact]
    public async Task EngineShutdownAnswersThenExits()
    {
        var engine = await StartServeAsync();
        await using var client = await EngineClient.ConnectAsync(PipeName.ForCurrentUser(_instance), cancellationToken: Token);

        await client.SendAsync(EngineMessages.Shutdown, Empty.Value, Token);

        await engine.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(0, engine.ExitCode);
        await client.Completion.WaitAsync(TimeSpan.FromSeconds(5), Token);
    }

    private Process Start()
    {
        var start = new ProcessStartInfo(EngineExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("serve");
        start.ArgumentList.Add("--instance");
        start.ArgumentList.Add(_instance);
        return Process.Start(start)!;
    }

    private async Task<Process> StartServeAsync()
    {
        var process = Start();
        _processes.Add(process);
        // Console log lines come first; the engine prints this one once its pipe is serving.
        string? line;
        do
        {
            line = await process.StandardOutput.ReadLineAsync(Token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), Token);
        }
        while (line is not null && !line.StartsWith("listening ", StringComparison.Ordinal));

        Assert.Equal("listening " + PipeName.ForCurrentUser(_instance), line);

        // Keep draining stdout so the engine never blocks on a full pipe.
        _ = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        return process;
    }
}
