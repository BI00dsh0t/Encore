using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Platform.Desktop;

namespace Encore.App.Engine;

/// <summary>Where the App stands with the engine.</summary>
internal enum EngineStatus
{
    Connecting,
    Connected,

    /// <summary>The engine crashed or hung, and the App is starting it again.</summary>
    Restarting,
    NotRunning,
    Disconnected,

    /// <summary>The engine crashed or hung too often: the App stopped relaunching it until the user restarts it.</summary>
    CrashLoop,
}

/// <summary>What the App's views need from the engine: its state, and a way to send commands.</summary>
internal interface IEngineSession
{
    EngineStatus Status { get; }

    /// <summary>Why the last attempt to reach the engine failed, if it did.</summary>
    string? Problem { get; }

    /// <summary>The engine's state, or <c>null</c> before the first connection.</summary>
    StateSnapshot? State { get; }

    /// <summary>Raised, on a background thread, when <see cref="Status"/> or <see cref="State"/> changes.</summary>
    event Action? Changed;

    /// <summary>Sends a command; throws ErrorCodeException when the engine refuses it or there is no connection.</summary>
    Task<TResult> SendAsync<TArgs, TResult>(CommandType<TArgs, TResult> command, TArgs args, CancellationToken cancellationToken = default);
}

/// <summary>The views' one rule for a session's state: connected, or nothing.</summary>
internal static class EngineSessions
{
    /// <summary>The session's state while it is <see cref="EngineStatus.Connected"/>, else <c>null</c>.</summary>
    public static StateSnapshot? ConnectedState(this IEngineSession engine) => ConnectedState(engine.Status, engine.State);

    /// <summary>The state when the status is <see cref="EngineStatus.Connected"/>, else <c>null</c>.</summary>
    public static StateSnapshot? ConnectedState(EngineStatus status, StateSnapshot? state) =>
        status == EngineStatus.Connected ? state : null;
}

internal sealed record EngineConnectionOptions
{
    /// <summary>The engine's file name; the App trusts only this image, in its own folder.</summary>
    public const string EngineFileName = "Encore.Engine.exe";

    public required string InstanceName { get; init; }

    /// <summary>The engine executable, launched when no engine answers.</summary>
    public required string EnginePath { get; init; }

    public IReadOnlyList<string> EngineArguments { get; init; } = ["serve"];

    /// <summary>The only engine image the App connects to, and so the only one it ever kills: the one in its own folder.</summary>
    public string TrustedEnginePath { get; init; } = Path.Combine(AppContext.BaseDirectory, EngineFileName);

    /// <summary>How long to keep trying to connect after launching the engine.</summary>
    public TimeSpan LaunchTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long one connection attempt waits for the pipe and the snapshot.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The process <c>app.attach</c> names, which the engine relaunches if it dies.</summary>
    public int AppPid { get; init; } = Environment.ProcessId;

    /// <summary>Writes one line to the App's log; the supervise loop logs a crash loop through it.</summary>
    public Action<string>? Log { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>The App's connection to its engine, and its half of supervision. Details: docs/code-notes.md.</summary>
internal sealed class EngineConnection(EngineConnectionOptions options) : IEngineSession, IAsyncDisposable
{
    /// <summary>How long a closed connection waits for the engine's process to exit before treating it as alive.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(3);

    /// <summary>How long a kill waits for the engine's process to end.</summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    private readonly string _pipeName = PipeName.ForCurrentUser(options.InstanceName);
    private readonly CrashLoopGuard _guard = new(options.Time);
    private RestartBackoff _backoff = new(options.Time);
    private TaskCompletionSource? _restartRequested;

    /// <summary>Why the engine the last connect launched failed: it exited non-zero, or never answered.</summary>
    private EngineRestartReason? _launchFailure;
    private Link? _link;
    private Process? _launched;
    private CancellationTokenSource? _supervise;
    private Task _supervision = Task.CompletedTask;
    private volatile EngineStatus _status = EngineStatus.Connecting;
    private volatile string? _problem;

    private enum Attempt
    {
        Connected,
        Untrusted,
        NoAnswer,
    }

    private enum Outcome
    {
        Connected,
        Failed,

        /// <summary>A live engine didn't answer a reconnect within <see cref="Supervision.ReplyLimit"/>.</summary>
        Unanswered,
    }

    public event Action? Changed;

    /// <summary>Raised on a background thread when the engine exits cleanly without this App's Quit: the App quits too.</summary>
    public event Action? EngineEnded;

    public EngineStatus Status => _status;

    public StateSnapshot? State => Volatile.Read(ref _link)?.Client.State;

    public string? Problem => _problem;

    /// <summary>Connects, launching the engine once when none answers; ends Connected, or NotRunning with a Problem.</summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetStatus(EngineStatus.Connecting);
        await ConnectOrLaunchAsync(live: null, restart: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The tray's Restart after a crash loop: clears the guard and the backoff and starts the engine again.</summary>
    public void RestartAfterCrashLoop() => Volatile.Read(ref _restartRequested)?.TrySetResult();

    /// <summary>Starts the supervise loop: connect, attach, watch, and kill or relaunch as needed.</summary>
    public void StartSupervising()
    {
        if (_supervise is not null)
        {
            throw new InvalidOperationException("Supervision has already started.");
        }

        _supervise = new CancellationTokenSource();
        var token = _supervise.Token;
        _supervision = Task.Run(() => SuperviseAsync(token), CancellationToken.None);
    }

    public async Task<TResult> SendAsync<TArgs, TResult>(CommandType<TArgs, TResult> command, TArgs args, CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _link) is not { } link)
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
        if (Volatile.Read(ref _link) is not { Client.IsConnected: true }
            && (IsAlive(Volatile.Read(ref _link)?.Engine) || IsAlive(_launched)))
        {
            await AttemptAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (Volatile.Read(ref _link) is not { Client.IsConnected: true } link)
        {
            // Still unreachable (just launched, or refusing connections): don't leave it running with no App.
            if (IsAlive(_launched))
            {
                await KillAsync(_launched!).ConfigureAwait(false);
            }

            if (Volatile.Read(ref _link)?.Engine is { } engine && IsAlive(engine))
            {
                await KillAsync(engine).ConfigureAwait(false);
            }

            return true;
        }

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

    public async ValueTask DisposeAsync()
    {
        await StopSupervisingAsync().ConfigureAwait(false);
        if (Interlocked.Exchange(ref _link, null) is { } link)
        {
            await link.DisposeAsync().ConfigureAwait(false);
        }

        _launched?.Dispose();
        _supervise?.Dispose();
    }

    private static bool IsAlive(Process? process)
    {
        try
        {
            return process is { HasExited: false };
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            return false;
        }
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

    private async Task SuperviseAsync(CancellationToken cancellationToken)
    {
        EngineRestartReason? restart = null;
        Process? live = null;
        SetStatus(EngineStatus.Connecting);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var outcome = await ConnectOrLaunchAsync(live, restart, cancellationToken).ConfigureAwait(false);
                if (outcome == Outcome.Unanswered)
                {
                    // A dropped connection to a live engine that won't take a new one: it is hung.
                    await KillAsync(live!).ConfigureAwait(false);
                    live = null;
                    restart = await RestartAsync(EngineRestartReason.Hung, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (outcome == Outcome.Failed)
                {
                    if (live is not null && !IsAlive(live))
                    {
                        if (live.ExitCode == 0)
                        {
                            EndedCleanly();
                            return;
                        }

                        // It crashed while reconnecting; the reconnect already set the status.
                        live = null;
                        restart = await RestartAsync(EngineRestartReason.Crashed, cancellationToken, status: false).ConfigureAwait(false);
                        continue;
                    }

                    live = null;
                    if (_launchFailure is { } failure)
                    {
                        // The engine it launched crashed or hung before serving: a restart like any other, so a crash loop ends.
                        restart = await RestartAsync(failure, cancellationToken, status: false).ConfigureAwait(false);
                        continue;
                    }

                    await Task.Delay(_backoff.Next(), options.Time, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                live = null;
                var link = Volatile.Read(ref _link)!;

                // Not awaited: the watch covers it, so an engine that hangs before replying is caught.
                var attaching = AttachAsync(restart, cancellationToken);
                var hung = await WatchAsync(link, cancellationToken).ConfigureAwait(false);
                if (attaching.IsCompletedSuccessfully && attaching.Result)
                {
                    restart = null;
                }

                if (hung is not null)
                {
                    _problem = hung;
                    await link.Client.DisposeAsync().ConfigureAwait(false);
                    await KillAsync(link.Engine).ConfigureAwait(false);
                    restart = await RestartAsync(EngineRestartReason.Hung, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!await HasExitedAsync(link.Engine, cancellationToken).ConfigureAwait(false))
                {
                    // The connection dropped (say, the writer queue overflowed) but the engine lives: reconnect, no relaunch.
                    live = link.Engine;
                    SetStatus(EngineStatus.Connecting);
                    continue;
                }

                var code = link.Engine.ExitCode;
                if (code == 0)
                {
                    EndedCleanly();
                    return;
                }

                _problem = $"Encore's background service exited with code {code}.";
                restart = await RestartAsync(EngineRestartReason.Crashed, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException or ErrorCodeException)
            {
                _problem = e.Message;
                SetStatus(EngineStatus.NotRunning);
                await Task.Delay(_backoff.Next(), options.Time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Every relaunch after a crash or hang: asks the crash-loop guard, then waits out the backoff.</summary>
    private async Task<EngineRestartReason?> RestartAsync(EngineRestartReason reason, CancellationToken cancellationToken, bool status = true)
    {
        if (_guard.TryRestart())
        {
            if (status)
            {
                SetStatus(EngineStatus.Restarting);
            }

            await Task.Delay(_backoff.Next(), options.Time, cancellationToken).ConfigureAwait(false);
            return reason;
        }

        var restartRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _restartRequested, restartRequested);
        var what = reason == EngineRestartReason.Hung ? "hung" : "crashed";
        _problem = $"Encore's background service keeps crashing ({ErrorCodes.EngineCrashLoop}), so it isn't being restarted. Restart it from the tray menu.";
        options.Log?.Invoke(
            $"{ErrorCodes.EngineCrashLoop}: the engine {what} again after {Supervision.CrashLoopRestarts} restarts "
            + $"within {Supervision.CrashLoopWindow.TotalMinutes:0} min; not relaunching it");
        SetStatus(EngineStatus.CrashLoop);
        try
        {
            await restartRequested.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _restartRequested, null);
        }

        _guard.Reset();
        _backoff = new RestartBackoff(options.Time);
        _problem = null;
        options.Log?.Invoke("Restarting the engine after a crash loop, as asked from the tray");
        SetStatus(EngineStatus.Connecting);
        return null;
    }

    /// <summary>The engine exited with code 0, not from this App's Quit: the App quits too.</summary>
    private void EndedCleanly()
    {
        _problem = "Encore's background service shut down.";
        SetStatus(EngineStatus.NotRunning);
        EngineEnded?.Invoke();
    }

    /// <summary>Sends <c>app.attach</c>; true once the engine has accepted it. Never throws.</summary>
    private async Task<bool> AttachAsync(EngineRestartReason? restart, CancellationToken cancellationToken)
    {
        try
        {
            await SendAsync(EngineMessages.Attach, new AppAttach(options.AppPid, restart), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            // The watch sees the connection end, or the engine hang.
            return false;
        }
    }

    /// <summary>Watches a connection until it ends (<c>null</c>) or the engine counts as hung (why).</summary>
    private async Task<string?> WatchAsync(Link link, CancellationToken cancellationToken)
    {
        while (true)
        {
            var tick = Task.Delay(Supervision.CheckInterval, options.Time, cancellationToken);
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

    private async Task<bool> HasExitedAsync(Process engine, CancellationToken cancellationToken)
    {
        try
        {
            await engine.WaitForExitAsync(cancellationToken).WaitAsync(ExitWait, options.Time, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>Kills the engine through the handle opened, and verified, at connect.</summary>
    private async Task KillAsync(Process engine)
    {
        try
        {
            engine.Kill();
            await engine.WaitForExitAsync().WaitAsync(KillWait, options.Time).ConfigureAwait(false);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or TimeoutException)
        {
            // It exited meanwhile, or won't die; the next launch fails on the engine mutex and is retried.
        }
    }

    /// <summary>Connects, launching the engine when none answers; with a live engine it only reconnects, for a limited time.</summary>
    private async Task<Outcome> ConnectOrLaunchAsync(Process? live, EngineRestartReason? restart, CancellationToken cancellationToken)
    {
        _launched?.Dispose();
        _launched = null;
        _launchFailure = null;
        var since = options.Time.GetTimestamp();
        while (true)
        {
            switch (await AttemptAsync(cancellationToken).ConfigureAwait(false))
            {
                case Attempt.Connected:
                    return Outcome.Connected;

                case Attempt.Untrusted:
                    // Never launch over a squatter.
                    SetStatus(EngineStatus.NotRunning);
                    return Outcome.Failed;
            }

            if (live is not null)
            {
                if (!IsAlive(live))
                {
                    _problem = "Encore's background service exited while reconnecting.";
                    SetStatus(EngineStatus.NotRunning);
                    return Outcome.Failed;
                }

                if (options.Time.GetElapsedTime(since) >= Supervision.ReplyLimit)
                {
                    _problem = $"Encore's background service didn't take a new connection within {Supervision.ReplyLimit.TotalSeconds:0} s.";
                    return Outcome.Unanswered;
                }
            }
            else if (_launched is null)
            {
                if (!TryLaunch(restart))
                {
                    break;
                }

                since = options.Time.GetTimestamp();
            }
            else if (_launched.HasExited)
            {
                _problem = $"Encore's background service exited with code {_launched.ExitCode}.";
                _launchFailure = _launched.ExitCode != 0 ? EngineRestartReason.Crashed : null;
                break;
            }
            else if (options.Time.GetElapsedTime(since) >= options.LaunchTimeout)
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

                _problem = $"Encore's background service didn't answer within {options.LaunchTimeout.TotalSeconds:0} s.";
                _launchFailure = EngineRestartReason.Hung;
                break;
            }
        }

        SetStatus(EngineStatus.NotRunning);
        return Outcome.Failed;
    }

    /// <summary>One connection attempt, with the server check.</summary>
    private async Task<Attempt> AttemptAsync(CancellationToken cancellationToken)
    {
        var watchdog = new EngineWatchdog(options.Time);
        Process? verified = null;
        var clientOptions = new EngineClientOptions
        {
            ConnectTimeout = options.AttemptTimeout,
            VerifyServer = pipe => (verified = VerifyServer(pipe)) is not null,
            OnEvent = _ =>
            {
                watchdog.EventReceived();
                Changed?.Invoke();
            },
            OnDisconnected = _ =>
            {
                if (Volatile.Read(ref _link)?.Watchdog == watchdog)
                {
                    SetStatus(EngineStatus.Disconnected);
                }
            },
        };

        try
        {
            var client = await EngineClient.ConnectAsync(_pipeName, clientOptions, cancellationToken).ConfigureAwait(false);
            if (client.State.Pid != verified!.Id)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw new ErrorCodeException(
                    ErrorCodes.EngineUntrusted,
                    $"The background service's first message names process {client.State.Pid}, but process {verified.Id} serves its pipe.");
            }

            // The snapshot is the connection's first event.
            watchdog.EventReceived();
            var link = new Link(client, verified, watchdog);
            if (Interlocked.Exchange(ref _link, link) is { } old)
            {
                await old.DisposeAsync().ConfigureAwait(false);
            }

            _problem = null;
            SetStatus(client.IsConnected ? EngineStatus.Connected : EngineStatus.Disconnected);
            return Attempt.Connected;
        }
        catch (ErrorCodeException e) when (e.Code == ErrorCodes.EngineUntrusted)
        {
            verified?.Dispose();
            _problem = $"{e.Message} ({e.Code})";
            return Attempt.Untrusted;
        }
        catch (ErrorCodeException e) when (e.Code == ErrorCodes.EngineDisconnected)
        {
            verified?.Dispose();
            _problem = e.Message;
            return Attempt.NoAnswer;
        }
    }

    /// <summary>The server check: the pipe's server must be the trusted engine image; returns its process, or null.</summary>
    private Process? VerifyServer(NamedPipeClientStream pipe)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(PipeServer.ProcessId(pipe.SafePipeHandle));

            // The handle first, then the image through it: a reused pid can't slip in between.
            _ = process.SafeHandle;
            var image = process.MainModule?.FileName;
            if (image is not null && string.Equals(Path.GetFullPath(image), Path.GetFullPath(options.TrustedEnginePath), StringComparison.OrdinalIgnoreCase))
            {
                return process;
            }
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Gone, or not ours to inspect: untrusted.
        }

        process?.Dispose();
        return null;
    }

    /// <summary>Starts the engine; after a crash or hang it passes the reason, so the engine raises <c>engine-restarted</c>.</summary>
    private bool TryLaunch(EngineRestartReason? restart)
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

        if (restart is { } reason)
        {
            start.ArgumentList.Add("--restarted");
            start.ArgumentList.Add(reason == EngineRestartReason.Hung ? "hung" : "crashed");
        }

        try
        {
            _launched = Process.Start(start) ?? throw new Win32Exception("The engine process didn't start.");
            return true;
        }
        catch (Win32Exception e)
        {
            _problem = $"Encore's background service at '{options.EnginePath}' couldn't be started: {e.Message}";
            return false;
        }
    }

    private void SetStatus(EngineStatus status)
    {
        _status = status;
        Changed?.Invoke();
    }

    /// <summary>One connection: its client, the verified engine process behind it, and its watchdog.</summary>
    private sealed record Link(EngineClient Client, Process Engine, EngineWatchdog Watchdog) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync().ConfigureAwait(false);
            Engine.Dispose();
        }
    }
}
