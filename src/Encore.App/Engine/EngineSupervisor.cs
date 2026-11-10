using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Encore.Contracts;
using Encore.Contracts.Messages;

namespace Encore.App.Engine;

/// <summary>The App's half of supervision: keeps the engine connected, and kills or relaunches it when it hangs or crashes.</summary>
internal sealed class EngineSupervisor
{
    private readonly EngineConnectionOptions _options;
    private readonly EngineConnectionState _state;
    private readonly EngineConnector _connector;
    private readonly Func<AppAttach, CancellationToken, Task> _attach;
    private readonly Action _engineEnded;
    private readonly CrashLoopGuard _guard;
    private RestartBackoff _backoff;
    private TaskCompletionSource? _restartRequested;

    // Why the next launch is a restart, until the engine has been told; null for a first start.
    private EngineRestartReason? _restart;

    // An engine whose connection dropped while its process lives: reconnect to it, don't launch another.
    private Process? _live;

    /// <summary>attach sends <c>app.attach</c>; engineEnded is called when the engine exits cleanly on its own.</summary>
    public EngineSupervisor(
        EngineConnectionOptions options,
        EngineConnectionState state,
        EngineConnector connector,
        Func<AppAttach, CancellationToken, Task> attach,
        Action engineEnded)
    {
        _options = options;
        _state = state;
        _connector = connector;
        _attach = attach;
        _engineEnded = engineEnded;
        _guard = new CrashLoopGuard(options.Time);
        _backoff = new RestartBackoff(options.Time);
    }

    /// <summary>The tray's Restart after a crash loop: clears the guard and the backoff and starts the engine again.</summary>
    public void RestartAfterCrashLoop() => Volatile.Read(ref _restartRequested)?.TrySetResult();

    /// <summary>The supervise loop: connect, attach, watch, and kill or relaunch as needed, until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _state.SetStatus(EngineStatus.Connecting);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!await SuperviseOnceAsync(cancellationToken).ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException or ErrorCodeException)
            {
                _state.Problem = e.Message;
                _state.SetStatus(EngineStatus.NotRunning);
                await Task.Delay(_backoff.Next(), _options.Time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // One pass: connect, then watch the connection until it ends. False when the engine shut down and the loop is over.
    private async Task<bool> SuperviseOnceAsync(CancellationToken cancellationToken)
    {
        var outcome = await _connector.ConnectOrLaunchAsync(_live, _restart, cancellationToken).ConfigureAwait(false);
        if (outcome == ConnectOutcome.Unanswered)
        {
            // A dropped connection to a live engine that won't take a new one: it is hung.
            await EngineProcess.KillAsync(_live!, _options.Time).ConfigureAwait(false);
            _live = null;
            _restart = await RestartAsync(EngineRestartReason.Hung, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (outcome == ConnectOutcome.Failed)
        {
            return await AfterFailedConnectAsync(cancellationToken).ConfigureAwait(false);
        }

        _live = null;
        return await WatchLinkAsync(_state.Link!, cancellationToken).ConfigureAwait(false);
    }

    // No connection: the engine exited cleanly (stop), crashed (restart), failed to launch (restart), or isn't there (wait).
    private async Task<bool> AfterFailedConnectAsync(CancellationToken cancellationToken)
    {
        if (_live is not null && !EngineProcess.IsAlive(_live))
        {
            if (_live.ExitCode == 0)
            {
                EndedCleanly();
                return false;
            }

            // It crashed while reconnecting; the reconnect already set the status.
            _live = null;
            _restart = await RestartAsync(EngineRestartReason.Crashed, cancellationToken, status: false).ConfigureAwait(false);
            return true;
        }

        _live = null;
        if (_connector.LaunchFailure is { } failure)
        {
            // The engine it launched crashed or hung before serving: a restart like any other, so a crash loop ends.
            _restart = await RestartAsync(failure, cancellationToken, status: false).ConfigureAwait(false);
            return true;
        }

        await Task.Delay(_backoff.Next(), _options.Time, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // Connected: attach, watch until the connection ends or the engine hangs, then decide what happens next.
    private async Task<bool> WatchLinkAsync(EngineLink link, CancellationToken cancellationToken)
    {
        // Not awaited: the watch covers it, so an engine that hangs before replying is caught.
        var attaching = AttachAsync(_restart, cancellationToken);
        var hung = await WatchAsync(link, cancellationToken).ConfigureAwait(false);
        if (attaching.IsCompletedSuccessfully && attaching.Result)
        {
            _restart = null;
        }

        if (hung is not null)
        {
            _state.Problem = hung;
            await link.Client.DisposeAsync().ConfigureAwait(false);
            await EngineProcess.KillAsync(link.Engine, _options.Time).ConfigureAwait(false);
            _restart = await RestartAsync(EngineRestartReason.Hung, cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (!await EngineProcess.HasExitedAsync(link.Engine, _options.Time, cancellationToken).ConfigureAwait(false))
        {
            // The connection dropped (say, the writer queue overflowed) but the engine lives: reconnect, no relaunch.
            _live = link.Engine;
            _state.SetStatus(EngineStatus.Connecting);
            return true;
        }

        var code = link.Engine.ExitCode;
        if (code == 0)
        {
            EndedCleanly();
            return false;
        }

        _state.Problem = $"Encore's background service exited with code {code}.";
        _restart = await RestartAsync(EngineRestartReason.Crashed, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Every relaunch after a crash or hang: asks the crash-loop guard, then waits out the backoff.</summary>
    private async Task<EngineRestartReason?> RestartAsync(EngineRestartReason reason, CancellationToken cancellationToken, bool status = true)
    {
        if (!_guard.TryRestart())
        {
            await WaitOutCrashLoopAsync(reason, cancellationToken).ConfigureAwait(false);
            return null;
        }

        if (status)
        {
            _state.SetStatus(EngineStatus.Restarting);
        }

        await Task.Delay(_backoff.Next(), _options.Time, cancellationToken).ConfigureAwait(false);
        return reason;
    }

    // Too many restarts: say so, and relaunch nothing until the user asks from the tray.
    private async Task WaitOutCrashLoopAsync(EngineRestartReason reason, CancellationToken cancellationToken)
    {
        var restartRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _restartRequested, restartRequested);
        var what = reason == EngineRestartReason.Hung ? "hung" : "crashed";
        _state.Problem = $"Encore's background service keeps crashing ({ErrorCodes.EngineCrashLoop}), so it isn't being restarted."
            + " Restart it from the tray menu.";
        _options.Log?.Invoke(
            $"{ErrorCodes.EngineCrashLoop}: the engine {what} again after {Supervision.CrashLoopRestarts} restarts "
            + $"within {Supervision.CrashLoopWindow.TotalMinutes:0} min; not relaunching it");
        _state.SetStatus(EngineStatus.CrashLoop);
        try
        {
            await restartRequested.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _restartRequested, null);
        }

        _guard.Reset();
        _backoff = new RestartBackoff(_options.Time);
        _state.Problem = null;
        _options.Log?.Invoke("Restarting the engine after a crash loop, as asked from the tray");
        _state.SetStatus(EngineStatus.Connecting);
    }

    /// <summary>The engine exited with code 0, not from this App's Quit: the App quits too.</summary>
    private void EndedCleanly()
    {
        _state.Problem = "Encore's background service shut down.";
        _state.SetStatus(EngineStatus.NotRunning);
        _engineEnded();
    }

    /// <summary>Sends <c>app.attach</c>; true once the engine has accepted it. Never throws.</summary>
    private async Task<bool> AttachAsync(EngineRestartReason? restart, CancellationToken cancellationToken)
    {
        try
        {
            await _attach(new AppAttach(_options.AppPid, restart), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            // The watch sees the connection end, or the engine hang.
            return false;
        }
    }

    /// <summary>Watches a connection until it ends (<c>null</c>) or the engine counts as hung (why).</summary>
    private async Task<string?> WatchAsync(EngineLink link, CancellationToken cancellationToken)
    {
        while (true)
        {
            var tick = Task.Delay(Supervision.CheckInterval, _options.Time, cancellationToken);
            if (await Task.WhenAny(link.Client.Completion, tick).ConfigureAwait(false) == link.Client.Completion)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (link.Watchdog.Check() is { } hung)
            {
                return hung;
            }
        }
    }
}
