using Encore.Contracts.Messages;
using Encore.Engine.Ipc;

namespace Encore.Engine.Hosting;

/// <summary>Publishes <c>engine.heartbeat</c> at an interval until stopped, so the App can tell a hung engine.</summary>
internal sealed class HeartbeatPublisher(EventHub hub, TimeProvider time) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task _running = Task.CompletedTask;

    public void Start(TimeSpan interval) => _running = RunAsync(interval);

    /// <summary>Stops publishing at once; a simulated hang uses it.</summary>
    public void Stop() => _stop.Cancel();

    public Task StopAsync() => _stop.CancelAsync();

    /// <summary>Stops publishing and waits for the loop to end.</summary>
    public async Task EndAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _running.ConfigureAwait(false);
    }

    public void Dispose() => _stop.Dispose();

    private async Task RunAsync(TimeSpan interval)
    {
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                hub.Publish(EngineMessages.Heartbeat, new EngineHeartbeat(time.GetUtcNow()));
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }
}
