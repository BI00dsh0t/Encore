using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Engine.Sessions;

namespace Encore.Engine.Tests;

/// <c>dev.simulate</c> over a real pipe: each faked outcome reaches the snapshot; the installed instance refuses it.
public sealed class DevSimulateServeTests : IAsyncLifetime
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(5);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-devsim-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _disposables = [];

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
    public async Task EachSimulatedEventReachesTheSnapshot()
    {
        var host = EngineHost.Start(
            TestInstance,
            NullLoggerFactory.Instance,
            hostOptions: new EngineHostOptions { Heartbeat = null, ProbeEncoders = false, AudioEndpoints = new FakeEndpointSource() });
        _disposables.Add(host);
        var events = Channel.CreateUnbounded<IpcEvent>();
        var client = await EngineClient.ConnectAsync(host.PipeName, new EngineClientOptions { OnEvent = e => events.Writer.TryWrite(e) }, Token);
        _disposables.Add(client);

        await SimulateAsync(client, SimulatedEvent.RecordingStarted);
        await UntilAsync(client, events, s => s.Capture.Recording == RecordingState.Running && Has(s, ErrorCodes.RecordingStarted));
        var started = client.State.Warnings.Single(w => w.Code == ErrorCodes.RecordingStarted);
        Assert.Equal(("recordingStarted", true, WarningSeverity.Info), (started.Sound, started.Overlay, started.Severity));

        await SimulateAsync(client, SimulatedEvent.RecordingStopped);
        await UntilAsync(client, events, s => s.Capture.Recording == RecordingState.Off && Has(s, ErrorCodes.RecordingStopped));

        await SimulateAsync(client, SimulatedEvent.DiskFull);
        await UntilAsync(client, events, s => Has(s, ErrorCodes.DiskFull));
        Assert.Equal("diskFull", client.State.Warnings.Single(w => w.Code == ErrorCodes.DiskFull).Sound);

        await SimulateAsync(client, SimulatedEvent.SaveFailed);
        await UntilAsync(client, events, s => s.LastClipFailure?.Reason == ErrorCodes.SaveFailed && Has(s, ErrorCodes.ClipFailed));
        Assert.Equal("Clip not saved: save failed", client.State.Warnings.Single(w => w.Code == ErrorCodes.ClipFailed).Message);

        await SimulateAsync(client, SimulatedEvent.ClipSaved);
        await UntilAsync(client, events, s => Has(s, ErrorCodes.ClipSaved) && !Has(s, ErrorCodes.ClipFailed));
        var saved = client.State.Warnings.Single(w => w.Code == ErrorCodes.ClipSaved);
        Assert.Equal(("Clip saved", true, WarningSeverity.Info), (saved.Message, saved.Overlay, saved.Severity));
    }

    [Fact]
    public void TheInstalledInstanceRefusesIt()
    {
        var calls = 0;
        var simulator = new DevSimulator(
            new Instance(Instance.InstalledName, _folder, _folder),
            _ => calls++,
            _ => calls++,
            _ => calls++,
            () => calls++,
            _ => calls++,
            () => calls++,
            () => calls++,
            () => calls++);

        var refused = Assert.Throws<ErrorCodeException>(() => simulator.Simulate(new DevSimulate(SimulatedEvent.DiskFull)));
        var hang = Assert.Throws<ErrorCodeException>(() => simulator.Simulate(new DevSimulate(SimulatedEvent.EngineHang)));
        var gpuReset = Assert.Throws<ErrorCodeException>(() => simulator.Simulate(new DevSimulate(SimulatedEvent.GpuReset)));

        Assert.Equal(ErrorCodes.NotDevInstance, refused.Code);
        Assert.Equal(ErrorCodes.NotDevInstance, hang.Code);
        Assert.Equal(ErrorCodes.NotDevInstance, gpuReset.Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void AGpuResetReachesItsActionOnADevInstance()
    {
        var resets = 0;
        var simulator = new DevSimulator(
            TestInstance,
            _ => { },
            _ => { },
            _ => { },
            () => { },
            _ => { },
            () => { },
            () => { },
            () => resets++);

        simulator.Simulate(new DevSimulate(SimulatedEvent.GpuReset));

        Assert.Equal(1, resets);
    }

    private static bool Has(StateSnapshot snapshot, string code) => snapshot.Warnings.Any(w => w.Code == code);

    private static Task SimulateAsync(EngineClient client, SimulatedEvent simulated) =>
        client.SendAsync(DevMessages.Simulate, new DevSimulate(simulated), Token).WaitAsync(Within, Token);

    /// <summary>Reads events until <paramref name="done"/> holds for the client's snapshot.</summary>
    private static async Task UntilAsync(EngineClient client, Channel<IpcEvent> events, Func<StateSnapshot, bool> done)
    {
        while (!done(client.State))
        {
            await events.Reader.ReadAsync(Token).AsTask().WaitAsync(Within, Token);
        }
    }
}
