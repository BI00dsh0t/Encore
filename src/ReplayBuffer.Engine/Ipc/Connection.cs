using System.IO.Pipes;
using Microsoft.Extensions.Logging;
using ReplayBuffer.Contracts.Ipc;

namespace ReplayBuffer.Engine.Ipc;

/// <summary>
/// One client connection (AD-3). Its writer loop is the only code that writes the pipe, fed by a
/// <see cref="WriterQueue"/>; its reader loop dispatches each command on its own, so a slow command
/// holds up nothing else. A full queue, a lagging client, a malformed frame or a broken pipe drops
/// the connection; the engine carries on.
/// </summary>
internal sealed class Connection : IFrameSink
{
    private readonly NamedPipeServerStream _pipe;
    private readonly EventHub _hub;
    private readonly CommandRouter _router;
    private readonly ILogger _log;
    private readonly int _maxFrameBytes;
    private readonly WriterQueue _queue;
    private readonly CancellationTokenSource _stop = new();
    private int _dropped;
    private Task _run = Task.CompletedTask;

    public Connection(int id, NamedPipeServerStream pipe, EventHub hub, CommandRouter router, IpcServerOptions options, ILogger log)
    {
        Id = id;
        _pipe = pipe;
        _hub = hub;
        _router = router;
        _log = log;
        _maxFrameBytes = options.MaxFrameBytes;
        _queue = new WriterQueue(options.MaxQueued, options.Time);
    }

    public int Id { get; }

    public TimeSpan Lag => _queue.Lag;

    /// <summary>Completes when the connection has ended and its pipe is closed.</summary>
    public Task Completion => _run;

    public void Start() => _run = RunAsync();

    public void Enqueue(byte[] frame, string? coalesceKey)
    {
        if (!_queue.TryEnqueue(frame, coalesceKey) && Volatile.Read(ref _dropped) == 0)
        {
            // Called under the hub's lock: the drop itself runs elsewhere.
            ThreadPool.QueueUserWorkItem(static c => c.Drop("its queue is full"), this, preferLocal: false);
        }
    }

    /// <summary>Ends the connection at once, discarding what is queued.</summary>
    public void Drop(string reason)
    {
        if (Interlocked.Exchange(ref _dropped, 1) != 0)
        {
            return;
        }

        _log.LogInformation("IPC connection {Id} ends: {Reason}", Id, reason);
        _queue.Close();
        _stop.Cancel();
    }

    /// <summary>Writes what is queued, for up to <paramref name="drain"/>, then ends the connection.</summary>
    public async Task CloseAsync(TimeSpan drain)
    {
        _queue.Complete();
        await Task.WhenAny(_run, Task.Delay(drain)).ConfigureAwait(false);
        Drop("the engine is shutting down");
        await _run.ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        _log.LogDebug("IPC connection {Id} opened", Id);
        _hub.Attach(this);
        var writer = WriteLoopAsync();
        var reader = ReadLoopAsync();
        try
        {
            var ended = await Task.WhenAny(writer, reader).ConfigureAwait(false);
            if (Volatile.Read(ref _dropped) == 0)
            {
                Drop(ended == reader ? "the client closed it" : "its writer ended");
            }

            await Task.WhenAll(writer, reader).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException or InvalidDataException)
        {
            if (Volatile.Read(ref _dropped) == 0)
            {
                Drop(e.Message);
            }
        }
        finally
        {
            _hub.Detach(this);
            Drop("it ended");
            await _pipe.DisposeAsync().ConfigureAwait(false);

            // _stop is not disposed: Drop may still be called from the lag check or a shutdown, and it has no timer to free.
            _log.LogDebug("IPC connection {Id} closed", Id);
        }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            while (await _queue.DequeueAsync(_stop.Token).ConfigureAwait(false) is { } frame)
            {
                await _pipe.WriteAsync(frame, _stop.Token).ConfigureAwait(false);
                _queue.Written();
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            Drop("writing failed: " + e.Message);
        }
    }

    private async Task ReadLoopAsync()
    {
        await using var reader = new FrameReader(_pipe, _maxFrameBytes);
        try
        {
            while (await reader.ReadAsync(_stop.Token).ConfigureAwait(false) is { } envelope)
            {
                if (envelope.Id is null)
                {
                    Drop($"it sent '{envelope.Type}' without an id");
                    return;
                }

                _ = Task.Run(() => HandleAsync(envelope), CancellationToken.None);
            }
        }
        catch (InvalidDataException e)
        {
            Drop("it sent a malformed frame: " + e.Message);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException)
        {
            Drop("reading failed: " + e.Message);
        }
    }

    private async Task HandleAsync(Envelope envelope)
    {
        try
        {
            var reply = await _router.DispatchAsync(envelope, _stop.Token).ConfigureAwait(false);
            Enqueue(reply.Frame, null);
            reply.AfterReply?.Invoke();
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            // The connection ended while the command ran; there is nobody to reply to.
        }
    }
}
