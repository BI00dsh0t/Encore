using System.Diagnostics;
using System.IO;
using System.Text.Json.Serialization;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;

namespace Encore.App.Tests;

[CollectionDefinition(nameof(SupervisionCollection), DisableParallelization = true)]
public sealed class SupervisionCollection;

/// <summary>Two-way supervision end to end with the real App and engine, a fresh pair per test. Timed, so they run alone.</summary>
[Collection(nameof(SupervisionCollection))]
public sealed class SupervisionTests : IAsyncDisposable
{
    /// <summary>How soon a killed or hung process must be running and connected again.</summary>
    private static readonly TimeSpan BackWithin = TimeSpan.FromSeconds(10);

    private static readonly CommandType<Empty, Empty> DebugHang = new("debug.hang", EngineJsonContext.Default.Empty, EngineJsonContext.Default.Empty);

    private static readonly CommandType<DebugDrop, Empty> DebugDropCommand = new("debug.drop", DebugDropJsonContext.Default.DebugDrop, DebugDropJsonContext.Default.Empty);

    private readonly string _instance = Instance.TestPrefix + Guid.NewGuid().ToString("N");
    private readonly HashSet<int> _pids = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string AppExe => Path.Combine(AppContext.BaseDirectory, "Encore.App.exe");

    private static string EngineExe => Path.Combine(AppContext.BaseDirectory, "Encore.Engine.exe");

    public async ValueTask DisposeAsync()
    {
        // The tray-equivalent shutdown first, so neither side relaunches the other.
        try
        {
            await using var client = await EngineClient.ConnectAsync(
                PipeName.ForCurrentUser(_instance),
                new EngineClientOptions { ConnectTimeout = TimeSpan.FromMilliseconds(500) });
            await client.SendAsync(EngineMessages.Shutdown, Empty.Value).WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(1000);
        }
        catch (Exception e) when (e is ErrorCodeException or TimeoutException)
        {
            // No engine left.
        }

        foreach (var pid in _pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                if (process.ProcessName is "Encore.App" or "Encore.Engine")
                {
                    process.Kill();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or TimeoutException)
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
    public async Task AKilledEngineIsRelaunchedAndReportsEngineRestarted()
    {
        var (app, engine) = await StartPairAsync();

        Kill(engine);

        var state = await WaitForStateAsync(
            s => s.Pid != engine && s.AppPid == app.Id && HasWarning(s, ErrorCodes.EngineRestarted),
            BackWithin);
        Assert.False(app.HasExited);
        Assert.Contains("unexpectedly", Assert.Single(state.Warnings, w => w.Code == ErrorCodes.EngineRestarted).Message);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AHungEngineIsKilledAndRelaunched()
    {
        var (app, engine) = await StartPairAsync();
        using var old = Process.GetProcessById(engine);
        await using var client = await ConnectAsync();

        _ = client.SendAsync(DebugHang, Empty.Value, CancellationToken.None);

        var state = await WaitForStateAsync(
            s => s.Pid != engine && s.AppPid == app.Id && HasWarning(s, ErrorCodes.EngineRestarted),
            BackWithin);
        Assert.True(old.HasExited);
        Assert.Contains("answering", Assert.Single(state.Warnings, w => w.Code == ErrorCodes.EngineRestarted).Message);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task ASimulatedEngineHangIsKilledAndRelaunchedAsStoppedAnswering()
    {
        var (app, engine) = await StartPairAsync();
        using var old = Process.GetProcessById(engine);
        await using (var client = await ConnectAsync())
        {
            await client.SendAsync(DevMessages.Simulate, new DevSimulate(SimulatedEvent.EngineHang), Token).WaitAsync(TimeSpan.FromSeconds(5), Token);
        }

        var state = await WaitForStateAsync(
            s => s.Pid != engine && s.AppPid == app.Id && HasWarning(s, ErrorCodes.EngineRestarted),
            TimeSpan.FromSeconds(20));
        Assert.True(old.HasExited);
        Assert.Contains("answering", Assert.Single(state.Warnings, w => w.Code == ErrorCodes.EngineRestarted).Message);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task ADroppedConnectionToALiveEngineReconnectsWithNoRelaunch()
    {
        var (app, engine) = await StartPairAsync();
        using var engineProcess = Process.GetProcessById(engine);
        await DropAsync(refuseNew: false);

        // Had the App not reconnected, it would kill the engine as hung once ReplyLimit (10 s) passed.
        await Task.Delay(Supervision.ReplyLimit + TimeSpan.FromSeconds(3), Token);

        Assert.False(engineProcess.HasExited);
        var state = await WaitForStateAsync(_ => true, TimeSpan.FromSeconds(5));
        Assert.Equal((engine, app.Id), (state.Pid, state.AppPid));
        Assert.DoesNotContain(state.Warnings, w => w.Code == ErrorCodes.EngineRestarted);
        Assert.False(app.HasExited);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task ALiveEngineThatRefusesNewConnectionsIsKilledAsHungAfterTenSeconds()
    {
        var (app, engine) = await StartPairAsync();
        using var engineProcess = Process.GetProcessById(engine);
        var dropped = Stopwatch.StartNew();
        await DropAsync(refuseNew: true);

        // Up to 3 s to see the engine is still alive, then 10 s of failed reconnects, then the relaunch.
        var state = await WaitForStateAsync(
            s => s.Pid != engine && s.AppPid == app.Id && HasWarning(s, ErrorCodes.EngineRestarted),
            TimeSpan.FromSeconds(25));
        Assert.True(dropped.Elapsed >= Supervision.ReplyLimit, $"relaunched after only {dropped.Elapsed.TotalSeconds:0.#} s");
        Assert.True(engineProcess.HasExited);
        Assert.Contains("answering", Assert.Single(state.Warnings, w => w.Code == ErrorCodes.EngineRestarted).Message);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AKilledAppIsRelaunchedByTheEngine()
    {
        var (app, engine) = await StartPairAsync();

        app.Kill();

        var state = await WaitForStateAsync(
            s => s.AppPid is { } pid && pid != app.Id && HasWarning(s, ErrorCodes.AppRestarted),
            BackWithin);
        Assert.Equal(engine, state.Pid);
        using var relaunched = Process.GetProcessById(state.AppPid!.Value);
        Assert.Equal("Encore.App", relaunched.ProcessName);
        Assert.False(relaunched.HasExited);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task ASecondAppAndASecondEngineExitAndThePairRunsOn()
    {
        var (app, engine) = await StartPairAsync();

        using var secondApp = Start(AppExe, "--tray", "--instance", _instance);
        await secondApp.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(0, secondApp.ExitCode);

        using var secondEngine = Start(EngineExe, "serve", "--instance", _instance);
        var stderr = await secondEngine.StandardError.ReadToEndAsync(Token);
        await secondEngine.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(1, secondEngine.ExitCode);
        Assert.Contains("error instance-running", stderr);

        // Nothing restarted: the same pair, still attached.
        var state = await WaitForStateAsync(_ => true, TimeSpan.FromSeconds(5));
        Assert.Equal((engine, app.Id), (state.Pid, state.AppPid));
        Assert.DoesNotContain(state.Warnings, w => w.Code is ErrorCodes.AppRestarted or ErrorCodes.EngineRestarted);
        Assert.False(app.HasExited);
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Heavy")]
    public async Task AShutdownEndsThePairAndNeitherIsRelaunched()
    {
        var (app, engine) = await StartPairAsync();
        using var engineProcess = Process.GetProcessById(engine);
        await using var client = await ConnectAsync();

        await client.SendAsync(EngineMessages.Shutdown, Empty.Value, Token);

        await engineProcess.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        await app.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(10), Token);
        Assert.Equal(0, engineProcess.ExitCode);
        Assert.Equal(0, app.ExitCode);

        // Long enough for a relaunch at backoff 0; then neither instance mutex is held by anything.
        await Task.Delay(TimeSpan.FromSeconds(3), Token);
        using (var appMutex = SingleInstance.TryAcquire(SingleInstance.App, _instance))
        using (var engineMutex = SingleInstance.TryAcquire(SingleInstance.Engine, _instance))
        {
            Assert.NotNull(appMutex);
            Assert.NotNull(engineMutex);
        }

        await Assert.ThrowsAsync<ErrorCodeException>(() => ConnectAsync(TimeSpan.FromMilliseconds(300)));
    }

    private async Task DropAsync(bool refuseNew)
    {
        await using var client = await ConnectAsync();
        try
        {
            await client.SendAsync(DebugDropCommand, new DebugDrop(refuseNew), Token);
        }
        catch (ErrorCodeException e) when (e.Code == ErrorCodes.EngineDisconnected)
        {
            // The drop may beat the reply.
        }
    }

    private static bool HasWarning(StateSnapshot state, string code) => state.Warnings.Any(w => w.Code == code);

    private static Process Start(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = exe == EngineExe,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return Process.Start(start)!;
    }

    private static void Kill(int pid)
    {
        using var process = Process.GetProcessById(pid);
        process.Kill();
    }

    /// <summary>Starts the App in the tray and waits until the engine it launched has its attach.</summary>
    private async Task<(Process App, int Engine)> StartPairAsync()
    {
        var app = Start(AppExe, "--tray", "--instance", _instance);
        _pids.Add(app.Id);
        var state = await WaitForStateAsync(s => s.AppPid == app.Id, TimeSpan.FromSeconds(30));
        return (app, state.Pid);
    }

    private async Task<EngineClient> ConnectAsync(TimeSpan? timeout = null)
    {
        var client = await EngineClient.ConnectAsync(
            PipeName.ForCurrentUser(_instance),
            new EngineClientOptions { ConnectTimeout = timeout ?? TimeSpan.FromSeconds(5) },
            Token);
        _pids.Add(client.State.Pid);
        return client;
    }

    /// <summary>Connects afresh until a snapshot satisfies <paramref name="condition"/>, within <paramref name="within"/>.</summary>
    private async Task<StateSnapshot> WaitForStateAsync(Func<StateSnapshot, bool> condition, TimeSpan within)
    {
        var elapsed = Stopwatch.StartNew();
        StateSnapshot? last = null;
        while (elapsed.Elapsed < within)
        {
            try
            {
                await using var client = await ConnectAsync(TimeSpan.FromMilliseconds(500));
                last = client.State;
                if (last.AppPid is { } appPid)
                {
                    _pids.Add(appPid);
                }

                if (condition(last))
                {
                    return last;
                }
            }
            catch (ErrorCodeException)
            {
                // Between engines.
            }

            await Task.Delay(100, Token);
        }

        Assert.Fail($"No snapshot matched within {within.TotalSeconds:0} s; the last was {last}.");
        return null!;
    }
}

/// <summary>The test-only <c>debug.drop</c>'s arguments, as the engine reads them.</summary>
internal sealed record DebugDrop(bool RefuseNew);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DebugDrop))]
[JsonSerializable(typeof(Empty))]
internal sealed partial class DebugDropJsonContext : JsonSerializerContext;
