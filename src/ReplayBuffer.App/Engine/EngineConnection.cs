using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;

namespace ReplayBuffer.App.Engine;

/// <summary>Where the App stands with the engine.</summary>
internal enum EngineStatus
{
    Connecting,
    Connected,
    NotRunning,
    Disconnected,
}

/// <summary>What the App's views need from the engine: its state, and a way to send commands.</summary>
internal interface IEngineSession
{
    EngineStatus Status { get; }

    /// <summary>Why the last attempt to reach the engine failed, if it did.</summary>
    string? Problem { get; }

    /// <summary>The engine's state (AD-3), or <c>null</c> before the first connection.</summary>
    StateSnapshot? State { get; }

    /// <summary>Raised, on a background thread, when <see cref="Status"/> or <see cref="State"/> changes.</summary>
    event Action? Changed;

    /// <exception cref="ErrorCodeException">The engine refused the command, or there is no connection (<see cref="ErrorCodes.EngineDisconnected"/>).</exception>
    Task<TResult> SendAsync<TArgs, TResult>(CommandType<TArgs, TResult> command, TArgs args, CancellationToken cancellationToken = default);
}

internal sealed record EngineConnectionOptions
{
    public required string InstanceName { get; init; }

    /// <summary>The engine executable, launched when no engine answers.</summary>
    public required string EnginePath { get; init; }

    public IReadOnlyList<string> EngineArguments { get; init; } = ["serve"];

    /// <summary>How long to keep trying to connect after launching the engine.</summary>
    public TimeSpan LaunchTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long one connection attempt waits for the pipe and the snapshot.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// The App's connection to its engine: connects, launching the engine when none answers, and
/// shuts it down on Quit (AD-16). There is no reconnect or restart; supervision is story 8's.
/// </summary>
internal sealed class EngineConnection(EngineConnectionOptions options) : IEngineSession, IAsyncDisposable
{
    private EngineClient? _client;
    private Process? _launched;
    private volatile EngineStatus _status = EngineStatus.Connecting;
    private volatile string? _problem;

    public event Action? Changed;

    public EngineStatus Status => _status;

    public StateSnapshot? State => Volatile.Read(ref _client)?.State;

    public string? Problem => _problem;

    /// <summary>
    /// Connects, launching the engine once when no engine answers. Ends <see cref="EngineStatus.Connected"/>,
    /// or <see cref="EngineStatus.NotRunning"/> with a <see cref="Problem"/>.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetStatus(EngineStatus.Connecting);
        var clientOptions = new EngineClientOptions
        {
            ConnectTimeout = options.AttemptTimeout,
            OnEvent = _ => Changed?.Invoke(),
            OnDisconnected = _ => SetStatus(EngineStatus.Disconnected),
        };
        var pipeName = PipeName.ForCurrentUser(options.InstanceName);
        var sinceLaunch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                Volatile.Write(ref _client, await EngineClient.ConnectAsync(pipeName, clientOptions, cancellationToken).ConfigureAwait(false));
                _problem = null;
                SetStatus(EngineStatus.Connected);
                return;
            }
            catch (ErrorCodeException e) when (e.Code == ErrorCodes.EngineDisconnected)
            {
                _problem = e.Message;
            }

            if (_launched is null)
            {
                if (!TryLaunch())
                {
                    break;
                }

                sinceLaunch.Restart();
            }
            else if (_launched.HasExited)
            {
                _problem = $"The engine exited with code {_launched.ExitCode}.";
                break;
            }
            else if (sinceLaunch.Elapsed >= options.LaunchTimeout)
            {
                // It never answered: don't leave it running with nothing to shut it down on Quit.
                try
                {
                    _launched.Kill();
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception)
                {
                    // It exited meanwhile.
                }

                _problem = $"The engine didn't answer within {options.LaunchTimeout.TotalSeconds:0} s.";
                break;
            }
        }

        SetStatus(EngineStatus.NotRunning);
    }

    public Task<TResult> SendAsync<TArgs, TResult>(CommandType<TArgs, TResult> command, TArgs args, CancellationToken cancellationToken = default) =>
        Volatile.Read(ref _client) is { } client
            ? client.SendAsync(command, args, cancellationToken)
            : Task.FromException<TResult>(new ErrorCodeException(ErrorCodes.EngineDisconnected, "The App isn't connected to the engine."));

    /// <summary>
    /// Quit (AD-16): sends <c>engine.shutdown</c> and waits up to <paramref name="timeout"/> for the
    /// engine process to exit. False when it didn't in time; the App exits anyway.
    /// </summary>
    public async Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        if (Volatile.Read(ref _client) is not { IsConnected: true } client)
        {
            return true;
        }

        var elapsed = Stopwatch.StartNew();
        using var engine = OpenProcess(client.State.Pid);
        try
        {
            try
            {
                await client.SendAsync(EngineMessages.Shutdown, Empty.Value).WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (ErrorCodeException)
            {
                // The connection ended before the reply: the engine is gone or going.
            }

            await client.Completion.WaitAsync(Remaining()).ConfigureAwait(false);
            if (engine is not null)
            {
                await engine.WaitForExitAsync().WaitAsync(Remaining()).ConfigureAwait(false);
            }

            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }

        TimeSpan Remaining() => timeout - elapsed.Elapsed is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _client) is { } client)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }

        _launched?.Dispose();
    }

    private bool TryLaunch()
    {
        var start = new ProcessStartInfo(options.EnginePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(options.EnginePath) ?? "",
        };
        foreach (var argument in options.EngineArguments)
        {
            start.ArgumentList.Add(argument);
        }

        try
        {
            _launched = Process.Start(start) ?? throw new Win32Exception("The engine process didn't start.");
            return true;
        }
        catch (Win32Exception e)
        {
            _problem = $"The engine at '{options.EnginePath}' couldn't be started: {e.Message}";
            return false;
        }
    }

    private static Process? OpenProcess(int pid)
    {
        try
        {
            return Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private void SetStatus(EngineStatus status)
    {
        _status = status;
        Changed?.Invoke();
    }
}
