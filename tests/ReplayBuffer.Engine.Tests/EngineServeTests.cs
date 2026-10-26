using System.Diagnostics;
using System.Text.Json;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Engine.Tests;

/// <summary>Runs the built engine's <c>serve</c> on a throwaway test instance and talks to it over its pipe.</summary>
public sealed class EngineServeTests : IDisposable
{
    private readonly string _instance = ServeCommand.TestInstancePrefix + Guid.NewGuid().ToString("N");
    private readonly List<Process> _processes = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string EngineExe => Path.Combine(Path.GetDirectoryName(typeof(CommandLine).Assembly.Location)!, "ReplayBuffer.Engine.exe");

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
            new EngineClientOptions { OnEvent = e => settingsChanged.TrySetResult(e) },
            Token);

        Assert.Equal(engine.Id, client.State.Pid);
        Assert.Equal(0, client.State.Seq);

        var update = new SettingsUpdate(0, [SettingsOp.SetGlobal("saveCooldown", JsonSerializer.SerializeToElement(9, SettingsJsonContext.Default.Int32))]);
        Assert.Equal(1, (await client.SendAsync(SettingsMessages.Update, update, Token)).Revision);
        Assert.Equal(("settings.changed", 1L), ((await settingsChanged.Task).Type, (await settingsChanged.Task).Seq));
        Assert.True(File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), _instance, "settings.json")));

        var pending = client.SendAsync(EngineHost.DebugHold, Empty.Value, Token);
        await Task.Delay(200, Token);
        Assert.False(pending.IsCompleted);

        engine.Kill();

        var e = await Assert.ThrowsAsync<ErrorCodeException>(() => pending.WaitAsync(TimeSpan.FromSeconds(1), Token));
        Assert.Equal(ErrorCodes.EngineDisconnected, e.Code);
    }

    [Fact]
    public async Task ASecondEngineForTheSameInstanceFailsWithIpcPipeInUse()
    {
        await StartServeAsync();

        using var second = Start();
        var stderr = await second.StandardError.ReadToEndAsync(Token);
        await second.WaitForExitAsync(Token);

        Assert.Equal(1, second.ExitCode);
        Assert.Contains("error ipc-pipe-in-use", stderr);
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
