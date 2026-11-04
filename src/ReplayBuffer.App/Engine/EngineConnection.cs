using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using ReplayBuffer.Contracts;
using ReplayBuffer.Contracts.Ipc;
using ReplayBuffer.Contracts.Messages;
using ReplayBuffer.Platform.Desktop;

namespace ReplayBuffer.App.Engine;

/// <summary>Where the App stands with the engine.</summary>
internal enum EngineStatus
{
    Connecting,
    Connected,

    /// <summary>The engine crashed or hung, and the App is starting it again (AD-16).</summary>
    Restarting,
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

/// <summary>The views' one rule for a session's state: connected, or nothing.</summary>
internal static class EngineSessions
{
    /// <summary>The session's state while it is <see cref="EngineStatus.Connected"/>, else <c>null</c>.</summary>
    public static StateSnapshot? ConnectedState(this IEngineSession engine) => ConnectedState(engine.Status, engine.State);

    /// <summary><paramref name="state"/> when <paramref name="status"/> is <see cref="EngineStatus.Connected"/>, else <c>null</c>.</summary>
    public static StateSnapshot? ConnectedState(EngineStatus status, StateSnapshot? state) =>
        status == EngineStatus.Connected ? state : null;
}

internal sealed record EngineConnectionOptions
{
    /// <summary>The engine's file name; the App trusts only this image, in its own folder (AD-3).</summary>
    public const string EngineFileName = "ReplayBuffer.Engine.exe";

    public required string InstanceName { get; init; }

    /// <summary>The engine executable, launched when no engine answers.</summary>
    public required string EnginePath { get; init; }

    public IReadOnlyList<string> EngineArguments { get; init; } = ["serve"];

    /// <summary>
    /// The only engine image the App connects to, and so the only one it ever kills (AD-3, AD-16):
    /// <c>ReplayBuffer.Engine.exe</c> in the App's own folder.
    /// </summary>
    public string TrustedEnginePath { get; init; } = Path.Combine(AppContext.BaseDirectory, EngineFileName);

    /// <summary>How long to keep trying to connect after launching the engine.</summary>
    public TimeSpan LaunchTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long one connection attempt waits for the pipe and the snapshot.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The process <c>app.attach</c> names, which the engine relaunches if it dies.</summary>
    public int AppPid { get; init; } = Environment.ProcessId;

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// The App's connection to its engine, and its half of supervision (AD-16). <see cref="ConnectAsync"/>
/// connects, launching the engine when none answers. <see cref="StartSupervising"/> keeps it that
/// way: it attaches with <c>app.attach</c>, kills an engine that is hung, relaunches one that
/// crashed, with backoff, and raises <see cref="EngineEnded"/> when the engine ends cleanly on its
/// own. <see cref="ShutdownAsync"/> stops supervising, then shuts the engine down for Quit.
/// </summary>
internal sealed class EngineConnection(EngineConnectionOptions options) : IEngineSession, IAsyncDisposable
{
    /// <summary>How long a closed connection waits for the engine's process to exit before treating it as alive.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(3);

    /// <summary>How long a kill waits for the engine's process to end.</summary>
    private static readonly TimeSpan KillWait = TimeSpan.FromSeconds(5);

    private readonly string _pipeName = PipeName.ForCurrentUser(options.InstanceName);
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

    /// <summary>
    /// Raised, on a background thread, when the supervised engine exits with code 0 not from this App's
    /// Quit (an <c>engine.shutdown</c> from elsewhere, or Ctrl+C): the App quits too.
    /// </summary>
    public event Action? EngineEnded;

    public EngineStatus Status => _status;

    public StateSnapshot? State => Volatile.Read(ref _link)?.Client.State;

    public string? Problem => _problem;

    /// <summary>
    /// Connects, launching the engine once when no engine answers. Ends <see cref="EngineStatus.Connected"/>,
    /// or <see cref="EngineStatus.NotRunning"/> with a <see cref="Problem"/>. Doesn't supervise.
    /// </summary>
    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        SetStatus(EngineStatus.Connecting);
        await ConnectOrLaunchAsync(live: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Starts the supervise loop (AD-16): connect, attach, watch, and kill or relaunch as needed.</summary>
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

    /// <summary>
    /// Quit (AD-16): stops supervising, sends <c>engine.shutdown</c> and waits up to <paramref name="timeout"/>
    /// for the engine process to exit. False when it didn't in time; the App exits anyway.
    /// </summary>
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
        var backoff = new RestartBackoff(options.Time);
        EngineRestartReason? restart = null;
        Process? live = null;
        SetStatus(EngineStatus.Connecting);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var outcome = await ConnectOrLaunchAsync(live, cancellationToken).ConfigureAwait(false);
                if (outcome == Outcome.Unanswered)
                {
                    // A dropped connection to a live engine that won't take a new one: it is hung.
                    await KillAsync(live!).ConfigureAwait(false);
                    live = null;
                    restart = EngineRestartReason.Hung;
                    SetStatus(EngineStatus.Restarting);
                    await Task.Delay(backoff.Next(), options.Time, cancellationToken).ConfigureAwait(false);
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

                        restart = EngineRestartReason.Crashed;
                    }

                    live = null;
                    await Task.Delay(backoff.Next(), options.Time, cancellationToken).ConfigureAwait(false);
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
                    restart = EngineRestartReason.Hung;
                    SetStatus(EngineStatus.Restarting);
                    await Task.Delay(backoff.Next(), options.Time, cancellationToken).ConfigureAwait(false);
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

                _problem = $"The engine exited with code {code}.";
                restart = EngineRestartReason.Crashed;
                SetStatus(EngineStatus.Restarting);
                await Task.Delay(backoff.Next(), options.Time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException or ErrorCodeException)
            {
                _problem = e.Message;
                SetStatus(EngineStatus.NotRunning);
                await Task.Delay(backoff.Next(), options.Time, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>The engine exited with code 0, not from this App's Quit: the App quits too.</summary>
    private void EndedCleanly()
    {
        _problem = "The engine shut down.";
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

    /// <summary>Kills the engine through the handle opened, and verified, at connect (AD-16).</summary>
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

    /// <summary>
    /// Connects, launching the engine when none answers, unless <paramref name="live"/> is an engine still
    /// running: then it only reconnects, and gives up after <see cref="Supervision.ReplyLimit"/>.
    /// </summary>
    private async Task<Outcome> ConnectOrLaunchAsync(Process? live, CancellationToken cancellationToken)
    {
        _launched?.Dispose();
        _launched = null;
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
                    _problem = "The engine exited while reconnecting.";
                    SetStatus(EngineStatus.NotRunning);
                    return Outcome.Failed;
                }

                if (options.Time.GetElapsedTime(since) >= Supervision.ReplyLimit)
                {
                    _problem = $"The engine didn't take a new connection within {Supervision.ReplyLimit.TotalSeconds:0} s.";
                    return Outcome.Unanswered;
                }
            }
            else if (_launched is null)
            {
                if (!TryLaunch())
                {
                    break;
                }

                since = options.Time.GetTimestamp();
            }
            else if (_launched.HasExited)
            {
                _problem = $"The engine exited with code {_launched.ExitCode}.";
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

                _problem = $"The engine didn't answer within {options.LaunchTimeout.TotalSeconds:0} s.";
                break;
            }
        }

        SetStatus(EngineStatus.NotRunning);
        return Outcome.Failed;
    }

    /// <summary>One connection attempt, with the server check (AD-3).</summary>
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
                    $"The engine's snapshot names process {client.State.Pid}, but process {verified.Id} serves its pipe.");
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

    /// <summary>
    /// The server check (AD-3): the process serving the pipe is <see cref="EngineConnectionOptions.TrustedEnginePath"/>.
    /// Returns that process with its handle open, the one handle the App may kill it through, or <c>null</c>.
    /// </summary>
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
