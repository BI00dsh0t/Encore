using Microsoft.Extensions.Logging.Abstractions;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Platform.Capture;

namespace ReplayBuffer.Engine.Tests;

/// <summary><c>monitors.list</c> over a real pipe of a throwaway test instance (6.11).</summary>
public sealed class MonitorListServeTests : IAsyncLifetime
{
    private static readonly TimeSpan Within = TimeSpan.FromSeconds(5);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rb-monitors-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _disposables = [];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private Instance TestInstance => new(ServeCommand.TestInstancePrefix + Path.GetFileName(_folder), Path.Combine(_folder, "roaming"), Path.Combine(_folder, "local"));

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
}
