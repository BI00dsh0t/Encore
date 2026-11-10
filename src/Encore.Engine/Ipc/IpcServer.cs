using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using Microsoft.Extensions.Logging;
using Encore.Contracts;

namespace Encore.Engine.Ipc;

/// <summary>The engine's pipe server; the constructor claims the pipe for the current user only. See docs/code-notes.md.</summary>
internal sealed class IpcServer : IAsyncDisposable
{
    private const int BufferBytes = 64 * 1024;
    private static readonly TimeSpan LagCheckInterval = TimeSpan.FromMilliseconds(250);

    private readonly IpcServerOptions _options;
    private readonly ILogger _log;
    private readonly PipeSecurity _security;
    private readonly ConcurrentDictionary<int, Connection> _connections = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _stopAccepting = new();
    private NamedPipeServerStream? _listener;
    private Task _accept = Task.CompletedTask;
    private Task _lagCheck = Task.CompletedTask;
    private int _nextId;
    private int _disposed;

    /// <exception cref="ErrorCodeException"><see cref="ErrorCodes.IpcPipeInUse"/>: the pipe already exists.</exception>
    public IpcServer(string pipeName, IpcServerOptions options, ILogger log)
    {
        PipeName = pipeName;
        _options = options;
        _log = log;
        _security = CurrentUserOnly();
        try
        {
            _listener = Create(first: true);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            throw new ErrorCodeException(ErrorCodes.IpcPipeInUse, $"The engine pipe '{pipeName}' is already in use: {e.Message}", e);
        }
    }

    public string PipeName { get; }

    public int ConnectionCount => _connections.Count;

    /// <summary>Raised on the closing connection's thread when the last connection closes; handlers must not block.</summary>
    public event Action? LastDisconnected;

    /// <summary>Ends every connection at once; their clients may connect again.</summary>
    public void DropAll(string reason)
    {
        foreach (var connection in _connections.Values)
        {
            connection.Drop(reason);
        }
    }

    /// <summary>Stops accepting connections, so the pipe name disappears while the engine lives on; for tests.</summary>
    public async Task StopAcceptingAsync()
    {
        await _stopAccepting.CancelAsync().ConfigureAwait(false);
        await _accept.ConfigureAwait(false);
        if (Interlocked.Exchange(ref _listener, null) is { } listener)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Starts accepting clients.</summary>
    public void Start(EventHub hub, CommandRouter router)
    {
        _accept = AcceptLoopAsync(hub, router);
        _lagCheck = LagCheckLoopAsync();
        _log.LogInformation("IPC listening on {Pipe}", PipeName);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        await _accept.ConfigureAwait(false);
        await _lagCheck.ConfigureAwait(false);
        if (Interlocked.Exchange(ref _listener, null) is { } listener)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
        }

        await Task.WhenAll(_connections.Values.Select(c => c.CloseAsync(_options.Drain))).ConfigureAwait(false);
        _stop.Dispose();
        _stopAccepting.Dispose();
    }

    private async Task AcceptLoopAsync(EventHub hub, CommandRouter router)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token, _stopAccepting.Token);
        try
        {
            while (true)
            {
                if (await AcceptOneAsync(stop.Token).ConfigureAwait(false) is { } pipe)
                {
                    Serve(pipe, hub, router);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            _log.LogError(e, "IPC stopped accepting connections");
        }
    }

    /// <summary>Waits for a client; null when it left before the wait took it. The next listener exists on return.</summary>
    private async Task<NamedPipeServerStream?> AcceptOneAsync(CancellationToken stop)
    {
        var connected = _listener!;
        try
        {
            await connected.WaitForConnectionAsync(stop).ConfigureAwait(false);
        }
        catch (IOException e) when (!stop.IsCancellationRequested)
        {
            // A client connected and left before the wait took it; a new pipe instance replaces this one, created first.
            _log.LogWarning("A client left before its connection was accepted: {Message}", e.Message);
            try
            {
                _listener = Create(first: false);
            }
            finally
            {
                await connected.DisposeAsync().ConfigureAwait(false);
            }

            return null;
        }

        // The next listener exists before this one is handed off, so the name is never free.
        _listener = Create(first: false);
        return connected;
    }

    private void Serve(NamedPipeServerStream pipe, EventHub hub, CommandRouter router)
    {
        var connection = new Connection(Interlocked.Increment(ref _nextId), pipe, hub, router, _options, _log);
        _connections[connection.Id] = connection;
        connection.Start();
        _ = connection.Completion.ContinueWith(
            (_, state) => Forget((int)state!),
            connection.Id,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void Forget(int connectionId)
    {
        if (_connections.TryRemove(connectionId, out Connection? _) && _connections.IsEmpty)
        {
            LastDisconnected?.Invoke();
        }
    }

    private async Task LagCheckLoopAsync()
    {
        using var timer = new PeriodicTimer(LagCheckInterval, _options.Time);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                foreach (var connection in _connections.Values)
                {
                    if (connection.Lag > _options.MaxLag)
                    {
                        connection.Drop($"it is more than {_options.MaxLag.TotalSeconds:0.###} s behind");
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
    }

    private NamedPipeServerStream Create(bool first) => NamedPipeServerStreamAcl.Create(
        PipeName,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
        BufferBytes,
        BufferBytes,
        _security);

    private static PipeSecurity CurrentUserOnly()
    {
        var user = Contracts.Ipc.PipeName.CurrentUserSid;
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        return security;
    }
}
