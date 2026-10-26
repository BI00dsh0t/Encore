using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Text.Json;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Contracts.Settings;

namespace ReplayBuffer.Contracts.Ipc;

/// <summary>An event as received: its type, <c>seq</c> and raw payload.</summary>
public sealed record IpcEvent(string Type, long Seq, JsonElement Payload);

/// <summary>How an <see cref="EngineClient"/> connects and where it reports.</summary>
public sealed record EngineClientOptions
{
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Checks the process serving the pipe (AD-3: its image is in the install folder). Runs after
    /// connecting and before anything is read; <c>false</c> fails the connect with
    /// <see cref="ErrorCodes.EngineUntrusted"/>.
    /// </summary>
    public Func<NamedPipeClientStream, bool>? VerifyServer { get; init; }

    /// <summary>
    /// Called for each event after the snapshot, in <c>seq</c> order, after <see cref="EngineClient.State"/>
    /// includes it. Unregistered event types are passed on too. It runs on the client's read loop:
    /// a reply that follows the event is not delivered until it returns, and an exception from it
    /// ends the connection.
    /// </summary>
    public Action<IpcEvent>? OnEvent { get; init; }

    /// <summary>Called once when the connection ends, with the exception that ended it, if any.</summary>
    public Action<Exception?>? OnDisconnected { get; init; }
}

/// <summary>
/// A connection to the engine (AD-3). <see cref="ConnectAsync"/> returns once the snapshot has
/// arrived; <see cref="State"/> then follows every event. Each command gets one reply; when the
/// connection ends, every pending command fails with <see cref="ErrorCodes.EngineDisconnected"/>.
/// There is no reconnect: a new connection is a new client, with a fresh snapshot.
/// </summary>
public sealed class EngineClient : IAsyncDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly FrameReader _reader;
    private readonly EngineClientOptions _options;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Envelope>> _pending = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private StateSnapshot _state;
    private long _nextId;
    private int _closed;
    private int _disposed;
    private Task _readLoop = Task.CompletedTask;

    private EngineClient(NamedPipeClientStream pipe, FrameReader reader, EngineClientOptions options, StateSnapshot snapshot)
    {
        _pipe = pipe;
        _reader = reader;
        _options = options;
        _state = snapshot;
    }

    /// <summary>The engine's state: the snapshot plus every event since.</summary>
    public StateSnapshot State => Volatile.Read(ref _state);

    /// <summary>Completes when the connection has ended.</summary>
    public Task Completion => _completion.Task;

    public bool IsConnected => Volatile.Read(ref _closed) == 0;

    /// <summary>Connects to the engine's pipe and waits for its snapshot.</summary>
    /// <exception cref="ErrorCodeException">
    /// <see cref="ErrorCodes.EngineDisconnected"/> when no engine answers or the connection drops
    /// before the snapshot; <see cref="ErrorCodes.EngineUntrusted"/> when the server check fails.
    /// </exception>
    public static async Task<EngineClient> ConnectAsync(string pipeName, EngineClientOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        options ??= new EngineClientOptions();

        // CurrentUserOnly also checks that the pipe's owner is this user.
        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        FrameReader? reader = null;
        try
        {
            try
            {
                await pipe.ConnectAsync(options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException)
            {
                throw new ErrorCodeException(ErrorCodes.EngineDisconnected, $"No engine answered on '{pipeName}': {e.Message}", e);
            }

            if (options.VerifyServer is { } verify && !verify(pipe))
            {
                throw new ErrorCodeException(ErrorCodes.EngineUntrusted, $"The process serving '{pipeName}' failed the server check.");
            }

            reader = new FrameReader(pipe, Protocol.MaxClientFrameBytes);
            var snapshot = await ReadSnapshotAsync(reader, pipeName, options.ConnectTimeout, cancellationToken).ConfigureAwait(false);
            var client = new EngineClient(pipe, reader, options, snapshot);
            client._readLoop = Task.Run(client.ReadLoopAsync, CancellationToken.None);
            return client;
        }
        catch
        {
            // The pipe first, so a snapshot read that timed out ends before its reader completes.
            await pipe.DisposeAsync().ConfigureAwait(false);
            if (reader is not null)
            {
                await reader.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    /// <summary>Sends a command and waits for its reply.</summary>
    /// <exception cref="ErrorCodeException">The engine replied with an error, or the connection ended first (<see cref="ErrorCodes.EngineDisconnected"/>).</exception>
    public async Task<TResult> SendAsync<TArgs, TResult>(CommandType<TArgs, TResult> command, TArgs args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var id = Interlocked.Increment(ref _nextId).ToString(CultureInfo.InvariantCulture);
        var frame = FrameWriter.Command(id, command.Name, args, command.ArgsType);
        var reply = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = reply;
        try
        {
            // Registered first, then checked: either this sees the close, or the close sees this entry.
            if (!IsConnected)
            {
                throw Disconnected();
            }

            await WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            var envelope = await reply.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            if (envelope.Error is { } error)
            {
                throw new ErrorCodeException(error.Code, error.Message);
            }

            if (envelope.Payload is not { } payload)
            {
                throw new ErrorCodeException(ErrorCodes.InvalidArguments, $"The reply to '{command.Name}' has no payload.");
            }

            try
            {
                return payload.Deserialize(command.ResultType)
                    ?? throw new JsonException($"The reply to '{command.Name}' has a null payload.");
            }
            catch (JsonException e)
            {
                throw new ErrorCodeException(ErrorCodes.InvalidArguments, $"The reply to '{command.Name}' doesn't fit its result type: {e.Message}", e);
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        Close(null);
        await _readLoop.ConfigureAwait(false);
        await _reader.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
        _writeLock.Dispose();
    }

    private static async Task<StateSnapshot> ReadSnapshotAsync(FrameReader reader, string pipeName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Envelope? first;
        try
        {
            first = await reader.ReadAsync(cancellationToken).AsTask().WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or TimeoutException or InvalidDataException)
        {
            throw new ErrorCodeException(ErrorCodes.EngineDisconnected, $"The engine on '{pipeName}' sent no snapshot: {e.Message}", e);
        }

        if (first is not { Type: EngineMessages.StateSnapshotType, Id: null, Payload: { } payload })
        {
            throw new ErrorCodeException(
                ErrorCodes.EngineDisconnected,
                first is null ? $"The engine on '{pipeName}' closed before its snapshot." : $"The engine on '{pipeName}' sent '{first.Type}' before its snapshot.");
        }

        try
        {
            var snapshot = payload.Deserialize(EngineJsonContext.Default.StateSnapshot) ?? throw new JsonException("The snapshot is null.");
            return snapshot with { Settings = SettingsMaps.WithOrdinalKeys(snapshot.Settings) };
        }
        catch (JsonException e)
        {
            throw new ErrorCodeException(ErrorCodes.EngineDisconnected, $"The engine on '{pipeName}' sent an unreadable snapshot: {e.Message}", e);
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? failure = null;
        try
        {
            while (await _reader.ReadAsync(_stop.Token).ConfigureAwait(false) is { } envelope)
            {
                if (envelope.Id is { } id)
                {
                    if (_pending.TryGetValue(id, out var reply))
                    {
                        reply.TrySetResult(envelope);
                    }
                }
                else
                {
                    OnEvent(envelope);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            failure = e;
        }

        Close(failure);
    }

    private void OnEvent(Envelope envelope)
    {
        if (envelope.Seq is not { } seq || envelope.Payload is not { } payload)
        {
            throw new InvalidDataException($"Event '{envelope.Type}' has no seq or payload.");
        }

        var state = _state;
        if (seq <= state.Seq)
        {
            return;
        }

        var type = MessageRegistry.FindEvent(envelope.Type!);
        var next = type is null ? state : type.ApplyJson(state, payload);
        Volatile.Write(ref _state, next with { Seq = seq });
        _options.OnEvent?.Invoke(new IpcEvent(envelope.Type!, seq, payload));
    }

    private async Task WriteAsync(byte[] frame, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Never cancelled part-way: half a frame would corrupt the stream for every later command.
            await _pipe.WriteAsync(frame, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            Close(e);
            throw Disconnected(e);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void Close(Exception? failure)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        _stop.Cancel();
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(Disconnected(failure));
        }

        _pipe.Dispose();
        try
        {
            _options.OnDisconnected?.Invoke(failure);
        }
        finally
        {
            _completion.TrySetResult();
        }
    }

    private static ErrorCodeException Disconnected(Exception? inner = null) =>
        new(ErrorCodes.EngineDisconnected, "The connection to the engine ended.", inner);
}
