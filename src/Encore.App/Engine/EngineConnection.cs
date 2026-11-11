using System.Diagnostics;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;

namespace Encore.App.Engine;

/// <summary>The App's session with its engine: its state, sending commands, and Quit. Details: docs/code-notes.md.</summary>
internal sealed class EngineConnection : IEngineSession, IAsyncDisposable
{
    private readonly EngineConnectionOptions _options;
    private readonly EngineConnectionState _state = new();
    private readonly EngineConnector _connector;
    private readonly EngineSupervisor _supervisor;
    private CancellationTokenSource? _supervise;
    private Task _supervision = Task.CompletedTask;

    public EngineConnection(EngineConnectionOptions options)
    {
        _options = options;
        _connector = new EngineConnector(options, _state);
        _supervisor = new EngineSupervisor(
            options,
            _state,
            _connector,
            (attach, cancellationToken) => SendAsync(EngineMessages.Attach, attach, cancellationToken),
            () => EngineEnded?.Invoke());
    }

    public event Action? Changed
    {
        add => _state.Changed += value;
        remove => _state.Changed -= value;
    }

    /// <summary>Raised on a background thread when the engine exits cleanly without this App's Quit: the App quits too.</summary>
    public event Action? EngineEnded;

    public EngineStatus Status => _state.Status;

    public StateSnapshot? State => _state.Link?.Client.State;

    public string? Problem => _state.Problem;

    /// <summary>Connects, launching the engine once when none answers; ends Connected, or NotRunning with a Problem.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        _state.SetStatus(EngineStatus.Connecting);
        await _connector.ConnectOrLaunchAsync(live: null, restart: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The tray's Restart after a crash loop: clears the guard and the backoff and starts the engine again.</summary>
    public void RestartAfterCrashLoop() => _supervisor.RestartAfterCrashLoop();

    /// <summary>Starts the supervise loop: connect, attach, watch, and kill or relaunch as needed.</summary>
    public void StartSupervising()
    {
        if (_supervise is not null)
        {
            throw new InvalidOperationException("Supervision has already started.");
        }

        _supervise = new CancellationTokenSource();
        var token = _supervise.Token;
        _supervision = Task.Run(() => _supervisor.RunAsync(token), CancellationToken.None);
    }

    public async Task<TResult> SendAsync<TArgs, TResult>(
        CommandType<TArgs, TResult> command,
        TArgs args,
        CancellationToken cancellationToken = default)
    {
        if (_state.Link is not { } link)
        {
            throw new ErrorCodeException(ErrorCodes.EngineDisconnected, "The App isn't connected to the engine.");
        }

        var pending = link.Watchdog.CommandStarted();
        try
        {
            return await link.Client.SendAsync(command, args, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            link.Watchdog.CommandEnded(pending);
        }
    }

    /// <summary>Quit: stops supervising, sends <c>engine.shutdown</c> and waits for the engine to exit; false when too slow.</summary>
    public async Task<bool> ShutdownAsync(TimeSpan timeout)
    {
        await StopSupervisingAsync().ConfigureAwait(false);

        // Between connections, an engine may still be running: reach it once, so it doesn't relaunch this App.
        if (_state.Link is not { Client.IsConnected: true }
            && (EngineProcess.IsAlive(_state.Link?.Engine) || EngineProcess.IsAlive(_connector.Launched)))
        {
            await _connector.AttemptAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (_state.Link is not { Client.IsConnected: true } link)
        {
            await KillUnreachableEnginesAsync().ConfigureAwait(false);
            return true;
        }

        return await SendShutdownAsync(link, timeout).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopSupervisingAsync().ConfigureAwait(false);
        if (_state.ReplaceLink(null) is { } link)
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }

        _connector.Dispose();
        _supervise?.Dispose();
    }

    // Still unreachable (just launched, or refusing connections): don't leave it running with no App.
    private async Task KillUnreachableEnginesAsync()
    {
        if (EngineProcess.IsAlive(_connector.Launched))
        {
            await EngineProcess.KillAsync(_connector.Launched!, _options.Time).ConfigureAwait(false);
        }

        if (_state.Link?.Engine is { } engine && EngineProcess.IsAlive(engine))
        {
            await EngineProcess.KillAsync(engine, _options.Time).ConfigureAwait(false);
        }
    }

    // Asks the engine to shut down and waits, within the timeout, for the connection and then the process to end.
    private static async Task<bool> SendShutdownAsync(EngineLink link, TimeSpan timeout)
    {
        var elapsed = Stopwatch.StartNew();
        try
        {
            try
            {
                await link.Client.SendAsync(EngineMessages.Shutdown, Empty.Value).WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (ErrorCodeException)
            {
                // The connection ended before the reply: the engine is gone or going.
            }

            await link.Client.Completion.WaitAsync(Remaining()).ConfigureAwait(false);
            await link.Engine.WaitForExitAsync().WaitAsync(Remaining()).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }

        TimeSpan Remaining() => timeout - elapsed.Elapsed is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private async Task StopSupervisingAsync()
    {
        if (_supervise is null)
        {
            return;
        }

        await _supervise.CancelAsync().ConfigureAwait(false);
        try
        {
            await _supervision.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
