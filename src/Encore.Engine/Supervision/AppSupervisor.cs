using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Engine.Ipc;

namespace Encore.Engine.Supervision;

/// <summary>The engine's half of two-way supervision: restarts an attached App that exits unasked. docs/code-notes.md.</summary>
internal sealed class AppSupervisor(
    EventHub hub,
    Warnings warnings,
    IAppLauncher launcher,
    TimeProvider time,
    ILogger log) : IDisposable
{
    /// <summary>Makes the check and the raise of <see cref="RaiseEngineRestarted"/> one step.</summary>
    private static readonly Lock RestartedLock = new();

    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly RestartBackoff _backoff = new(time);
    private readonly CrashLoopGuard _guard = new(time);
    private int? _appId;
    private bool _attached;
    private bool _crashLooped;

    /// <summary>The active <c>app-crash-loop</c> warning, withdrawn when a new App attaches.</summary>
    private string? _crashLoopWarning;
    private int _generation;
    private bool _stopped;
    private int _disposed;

    /// <summary>What the watch loop does after the App exits.</summary>
    private enum ExitStep
    {
        /// <summary>Supervision stopped, another App took over, or the exit was expected.</summary>
        Leave,

        /// <summary>Too many relaunches in the window: stop relaunching.</summary>
        CrashLoop,

        Relaunch,
    }

    /// <summary>The watch loop of the current App, for tests.</summary>
    internal Task Watching { get; private set; } = Task.CompletedTask;

    /// <summary>Raises <c>engine-restarted</c> for a reason, unless one is already active.</summary>
    public static void RaiseEngineRestarted(EventHub hub, Warnings warnings, EngineRestartReason reason)
    {
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(warnings);
        lock (RestartedLock)
        {
            if (hub.State.Warnings.Any(w => w.Code == ErrorCodes.EngineRestarted))
            {
                return;
            }

            warnings.Raise(
                ErrorCodes.EngineRestarted,
                WarningSeverity.Warning,
                "supervision",
                reason == EngineRestartReason.Hung
                    ? "Encore's background service stopped answering and was restarted."
                    : "Encore's background service stopped unexpectedly and was restarted.",
                overlay: false);
        }
    }

    /// <summary>Handles <c>app.attach</c>.</summary>
    public Empty Attach(AppAttach attach)
    {
        ArgumentNullException.ThrowIfNull(attach);
        lock (_lock)
        {
            if (_stopped)
            {
                return Empty.Value;
            }
        }

        hub.Publish(EngineMessages.Attached, new AppAttached(attach.Pid));
        if (attach.EngineRestart is { } reason)
        {
            RaiseEngineRestarted(hub, warnings, reason);
        }

        string? withdraw;
        lock (_lock)
        {
            if (_stopped)
            {
                return Empty.Value;
            }

            withdraw = EndCrashLoopLocked(attach.Pid);
        }

        if (withdraw is not null)
        {
            warnings.Withdraw(withdraw);
        }

        lock (_lock)
        {
            if (_stopped)
            {
                return Empty.Value;
            }

            if (_appId == attach.Pid)
            {
                _attached = true;
                return Empty.Value;
            }
        }

        return WatchAttached(attach.Pid);
    }

    /// <summary>Stops supervising, for <c>engine.shutdown</c>: no App is relaunched after this.</summary>
    public void Stop()
    {
        lock (_lock)
        {
            StopLocked();
            if (_disposed == 0)
            {
                _stop.Cancel();
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_lock)
        {
            StopLocked();
            _stop.Cancel();
        }

        _stop.Dispose();
    }

    private void StopLocked()
    {
        _stopped = true;
        _generation++;
        _appId = null;
    }

    /// <summary>An App started by hand after a crash loop allows relaunches again; returns the warning to withdraw.</summary>
    private string? EndCrashLoopLocked(int pid)
    {
        if (!_crashLooped || _appId == pid)
        {
            return null;
        }

        _crashLooped = false;
        _guard.Reset();
        var withdraw = _crashLoopWarning;
        _crashLoopWarning = null;
        log.LogInformation("App {Pid} attached after a crash loop; relaunching it is allowed again", pid);
        return withdraw;
    }

    private Empty WatchAttached(int pid)
    {
        var opened = launcher.Open(pid);
        if (opened is null)
        {
            log.LogWarning("App {Pid} attached but is already gone", pid);
            return Empty.Value;
        }

        lock (_lock)
        {
            if (_stopped)
            {
                opened.Dispose();
                return Empty.Value;
            }

            Watch(opened, attached: true);
        }

        log.LogInformation("Supervising App {Pid}", pid);
        return Empty.Value;
    }

    /// <summary>Makes <paramref name="app"/> the watched App; its watch loop owns it. Runs under the lock.</summary>
    private void Watch(IWatchedProcess app, bool attached)
    {
        _appId = app.Id;
        _attached = attached;
        var generation = ++_generation;
        Watching = WatchAsync(app, generation);
    }

    private async Task WatchAsync(IWatchedProcess app, int generation)
    {
        try
        {
            var code = await app.Exited.WaitAsync(_stop.Token).ConfigureAwait(false);

            // Each exit counts once against the crash-loop guard, however many starts its relaunch takes.
            var count = true;
            while (true)
            {
                var step = Judge(app, code, generation, count);
                count = false;
                if (step == ExitStep.CrashLoop)
                {
                    RaiseCrashLoop(app, code);
                }

                if (step != ExitStep.Relaunch)
                {
                    return;
                }

                if (await TryStartAfterBackoffAsync(app, code).ConfigureAwait(false) is not { } started)
                {
                    continue;
                }

                if (Adopt(started, generation) is not { } adopted)
                {
                    return;
                }

                generation = adopted;
                app.Dispose();
                app = started;
                code = await started.Exited.WaitAsync(_stop.Token).ConfigureAwait(false);
                count = true;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        finally
        {
            app.Dispose();
        }
    }

    /// <summary>Decides what follows an exit; count makes it count against the crash-loop guard.</summary>
    private ExitStep Judge(IWatchedProcess app, int code, int generation, bool count)
    {
        lock (_lock)
        {
            if (_stopped || generation != _generation)
            {
                return ExitStep.Leave;
            }

            if (!_attached && code == 0)
            {
                // A started App that ended cleanly before attaching: another copy holds the instance.
                log.LogInformation("The started App {Pid} exited with code 0 before attaching; not relaunching", app.Id);
                return ExitStep.Leave;
            }

            if (count)
            {
                _crashLooped = !_guard.TryRestart();
                if (_crashLooped)
                {
                    return ExitStep.CrashLoop;
                }
            }

            return ExitStep.Relaunch;
        }
    }

    private void RaiseCrashLoop(IWatchedProcess app, int code)
    {
        log.LogError(
            "{Code}: App {Pid} exited with code {ExitCode} after {Restarts} relaunches within {Minutes} min; not relaunching it",
            ErrorCodes.AppCrashLoop,
            app.Id,
            code,
            Contracts.Supervision.CrashLoopRestarts,
            Contracts.Supervision.CrashLoopWindow.TotalMinutes);
        var raised = warnings.Raise(
            ErrorCodes.AppCrashLoop,
            WarningSeverity.Error,
            "supervision",
            "The app keeps stopping unexpectedly, so it isn't being restarted. Start it again to resume.",
            overlay: false).Id;
        bool stale;
        lock (_lock)
        {
            // An App that attached while this was being raised already ended the crash loop.
            stale = !_crashLooped;
            if (!stale)
            {
                _crashLoopWarning = raised;
            }
        }

        if (stale)
        {
            warnings.Withdraw(raised);
        }
    }

    /// <summary>Waits out the backoff, then starts the App; null when the start failed and is retried.</summary>
    private async Task<IWatchedProcess?> TryStartAfterBackoffAsync(IWatchedProcess app, int code)
    {
        var delay = _backoff.Next();
        log.LogWarning(
            "App {Pid} exited with code {Code} and no engine.shutdown; relaunching in {Delay} s",
            app.Id,
            code,
            delay.TotalSeconds);
        await Task.Delay(delay, time, _stop.Token).ConfigureAwait(false);
        try
        {
            return launcher.Start();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogError(e, "The App couldn't be started; retrying");
            return null;
        }
    }

    /// <summary>Makes a relaunched App the watched one; null when an App attached or shutdown came while it started.</summary>
    private int? Adopt(IWatchedProcess started, int generation)
    {
        lock (_lock)
        {
            if (_stopped || generation != _generation)
            {
                // Leave it to its mutex.
                started.Dispose();
                return null;
            }

            _appId = started.Id;
            _attached = false;
            generation = ++_generation;
        }

        log.LogInformation("Relaunched the App as {Pid}", started.Id);
        warnings.Raise(
            ErrorCodes.AppRestarted,
            WarningSeverity.Warning,
            "supervision",
            "The app stopped unexpectedly and was restarted.",
            overlay: false);
        return generation;
    }
}
