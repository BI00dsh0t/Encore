using Encore.Contracts.Messages;
using Encore.Engine.Ipc;
using Encore.Engine.Sessions;

namespace Encore.Engine.Hosting;

/// <summary>Probes the encoders on a pool thread and publishes the result once; a failed probe publishes no codecs.</summary>
internal sealed class EncoderProbe(VideoEncoderCatalog encoders, EventHub hub) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private Task _running = Task.CompletedTask;

    public void Start() => _running = RunAsync();

    /// <summary>Stops between codecs; the one under way finishes first.</summary>
    public async Task EndAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        await _running.ConfigureAwait(false);
    }

    public void Dispose() => _stop.Dispose();

    private async Task RunAsync()
    {
        var token = _stop.Token;
        try
        {
            var options = await Task.Run(() => encoders.ProbeAll(token), token).ConfigureAwait(false);
            hub.Publish(CaptureMessages.VideoOptionsChanged, options);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }
}
