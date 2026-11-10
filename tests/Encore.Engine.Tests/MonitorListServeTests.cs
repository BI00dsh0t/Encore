using Microsoft.Extensions.Logging.Abstractions;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Platform.Capture;

namespace Encore.Engine.Tests;

/// <summary><c>monitors.list</c> over a real pipe of a throwaway test instance (6.11).</summary>
public sealed class MonitorListServeTests : IAsyncLifetime
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(5);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-monitors-" + Guid.NewGuid().ToString("N"));
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
    public async Task EveryAttachedMonitorComesBackNamedAndSizedWithThePrimaryFirst()
    {
        var host = EngineHost.Start(TestInstance, NullLoggerFactory.Instance, hostOptions: new EngineHostOptions { Heartbeat = null, ProbeEncoders = false });
        _disposables.Add(host);
        var client = await EngineClient.ConnectAsync(host.PipeName, new EngineClientOptions(), Token);
        _disposables.Add(client);

        var list = await client.SendAsync(RecordingMessages.MonitorsList, Empty.Value, Token).WaitAsync(Within, Token);

        Assert.Equal(
            MonitorFinder.All().Select(m => m.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase),
            list.Monitors.Select(m => m.DeviceName).ToHashSet(StringComparer.OrdinalIgnoreCase));
        Assert.Single(list.Monitors, m => m.Primary);
        Assert.True(list.Monitors[0].Primary);
        Assert.All(list.Monitors, m => Assert.True(m.FriendlyName.Length > 0 && m.Width > 0 && m.Height > 0, $"{m}"));
    }

    [Fact]
    public async Task ADisplayChangePublishesTheNewListAndMonitorsListReadsTheDisplayPort()
    {
        var dell = new DisplayMonitor(@"\\.\DISPLAY1", "DELL", 2560, 1440, true);
        var lg = new DisplayMonitor(@"\\.\DISPLAY2", "LG", 1920, 1080, false);
        var displays = new FakeDisplays([dell, lg]);
        var host = EngineHost.Start(TestInstance, NullLoggerFactory.Instance, hostOptions: new EngineHostOptions { Heartbeat = null, ProbeEncoders = false, Displays = displays });
        _disposables.Add(host);
        var client = await EngineClient.ConnectAsync(host.PipeName, new EngineClientOptions(), Token);
        _disposables.Add(client);
        Assert.Equal([dell, lg], (await client.SendAsync(RecordingMessages.MonitorsList, Empty.Value, Token).WaitAsync(Within, Token)).Monitors);

        // The LG is unplugged (T-03): monitors.changed carries the list, and monitors.list agrees.
        displays.Change([dell]);
        var deadline = DateTime.UtcNow + Within;
        while (client.State.Monitors is null && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Token);
        }

        Assert.Equal([dell], client.State.Monitors?.Monitors);
        Assert.Equal([dell], (await client.SendAsync(RecordingMessages.MonitorsList, Empty.Value, Token).WaitAsync(Within, Token)).Monitors);
    }
}
