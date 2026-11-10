using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Sessions;
using Encore.Engine.Settings;

namespace Encore.Engine.Tests;

/// <summary>Mic mute over a real pipe: a press toggles the state and <c>runtime.json</c>; with no file the engine starts muted.</summary>
public sealed class MicMuteServeTests : IAsyncLifetime
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(5);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-micmute-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _disposables = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Instance TestInstance => new(ServeCommand.TestInstancePrefix + Path.GetFileName(_folder).Replace(".", "-"), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));

    private string RuntimeFile => Path.Combine(TestInstance.LocalDataFolder, RuntimeStateStore.FileName);

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
    public async Task AnUnmuteIsPublishedSavedAndKeptAcrossARestartUntilTheFileIsDeleted()
    {
        // A cold start: no runtime.json, so the first snapshot is muted.
        var host = await StartHostAsync();
        var (client, events) = await ConnectAsync(host);
        Assert.True(client.State.Capture.MicMuted);
        Assert.False(File.Exists(RuntimeFile));

        Assert.True(host.Sessions.Post(new HotkeyPressed(HotkeyAction.MicMute, Stopwatch.GetTimestamp())));

        // One capture.stateChanged and one hotkey.pressed, the state on disk before either is seen.
        var received = new[] { await NextAsync(events), await NextAsync(events) };
        var changed = Assert.Single(received, e => e.Type == CaptureMessages.StateChanged.Name);
        Assert.Single(received, e => e.Type == HotkeyMessages.Pressed.Name);
        Assert.False(CaptureMessages.StateChanged.Read(changed.Payload).MicMuted);
        Assert.False(client.State.Capture.MicMuted);
        Assert.Equal(HotkeyAction.MicMute, client.State.LastHotkeyPress?.Action);
        Assert.Equal("""{"micMuted":false}""", File.ReadAllText(RuntimeFile));

        // A restart starts unmuted, in its first snapshot.
        await StopAsync(host, client);
        host = await StartHostAsync();
        (client, _) = await ConnectAsync(host);
        Assert.False(client.State.Capture.MicMuted);

        // With runtime.json deleted, the next run starts muted again.
        await StopAsync(host, client);
        File.Delete(RuntimeFile);
        host = await StartHostAsync();
        (client, _) = await ConnectAsync(host);
        Assert.True(client.State.Capture.MicMuted);
        Assert.Equal(0, client.State.Seq);
    }

    private static async Task<IpcEvent> NextAsync(Channel<IpcEvent> events) =>
        await events.Reader.ReadAsync(Token).AsTask().WaitAsync(Within, Token);

    private async Task StopAsync(EngineHost host, EngineClient client)
    {
        _disposables.Remove(client);
        _disposables.Remove(host);
        await client.DisposeAsync();
        await host.DisposeAsync();
    }

    private Task<EngineHost> StartHostAsync()
    {
        var host = EngineHost.Start(
            TestInstance,
            NullLoggerFactory.Instance,
            hostOptions: new EngineHostOptions { Heartbeat = null, ProbeEncoders = false, AudioEndpoints = new FakeEndpointSource() });
        _disposables.Add(host);
        return Task.FromResult(host);
    }

    private async Task<(EngineClient Client, Channel<IpcEvent> Events)> ConnectAsync(EngineHost host)
    {
        var events = Channel.CreateUnbounded<IpcEvent>();
        var client = await EngineClient.ConnectAsync(host.PipeName, new EngineClientOptions { OnEvent = e => events.Writer.TryWrite(e) }, Token);
        _disposables.Add(client);
        return (client, events);
    }
}
