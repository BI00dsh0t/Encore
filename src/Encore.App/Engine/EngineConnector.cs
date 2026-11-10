using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using Encore.Contracts;
using Encore.Contracts.Ipc;
using Encore.Contracts.Messages;
using Encore.Platform.Desktop;

namespace Encore.App.Engine;

/// <summary>Connects to the engine's pipe after checking who serves it, and launches the engine when none answers.</summary>
internal sealed class EngineConnector(EngineConnectionOptions options, EngineConnectionState state) : IDisposable
{
    private readonly string _pipeName = PipeName.ForCurrentUser(options.InstanceName);
    private long _waitingSince;

    /// <summary>The engine process the last connect launched, or null when it launched none.</summary>
    public Process? Launched { get; private set; }

    /// <summary>Why the engine the last connect launched failed: it exited non-zero, or never answered.</summary>
    public EngineRestartReason? LaunchFailure { get; private set; }

    /// <summary>Connects, launching the engine when none answers; with a live engine it only reconnects, for a limited time.</summary>
    public async Task<ConnectOutcome> ConnectOrLaunchAsync(Process? live, EngineRestartReason? restart, CancellationToken cancellationToken)
    {
        Launched?.Dispose();
        Launched = null;
        LaunchFailure = null;
        _waitingSince = options.Time.GetTimestamp();
        while (true)
        {
            switch (await AttemptAsync(cancellationToken).ConfigureAwait(false))
            {
                case EngineAttempt.Connected:
                    return ConnectOutcome.Connected;

                case EngineAttempt.Untrusted:
                    // Never launch over a squatter.
                    state.SetStatus(EngineStatus.NotRunning);
                    return ConnectOutcome.Failed;
            }

            var ended = live is not null ? AfterNoAnswerFromLiveEngine(live) : AfterNoAnswerFromNoEngine(restart);
            if (ended is { } outcome)
            {
                return outcome;
            }
        }
    }

    /// <summary>One connection attempt, with the server check; a success becomes the state's current link.</summary>
    public async Task<EngineAttempt> AttemptAsync(CancellationToken cancellationToken)
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
                state.RaiseChanged();
            },
            OnDisconnected = _ =>
            {
                if (state.Link?.Watchdog == watchdog)
                {
                    state.SetStatus(EngineStatus.Disconnected);
                }
            },
        };

        try
        {
            var client = await EngineClient.ConnectAsync(_pipeName, clientOptions, cancellationToken).ConfigureAwait(false);
            await UseAsync(client, verified!, watchdog).ConfigureAwait(false);
            return EngineAttempt.Connected;
        }
        catch (ErrorCodeException e) when (e.Code == ErrorCodes.EngineUntrusted)
        {
            verified?.Dispose();
            state.Problem = $"{e.Message} ({e.Code})";
            return EngineAttempt.Untrusted;
        }
        catch (ErrorCodeException e) when (e.Code == ErrorCodes.EngineDisconnected)
        {
            verified?.Dispose();
            state.Problem = e.Message;
            return EngineAttempt.NoAnswer;
        }
    }

    public void Dispose() => Launched?.Dispose();

    // The reconnect to a live engine got no answer: fail when it has exited, give up when it takes too long, else retry.
    private ConnectOutcome? AfterNoAnswerFromLiveEngine(Process live)
    {
        if (!EngineProcess.IsAlive(live))
        {
            state.Problem = "Encore's background service exited while reconnecting.";
            state.SetStatus(EngineStatus.NotRunning);
            return ConnectOutcome.Failed;
        }

        if (options.Time.GetElapsedTime(_waitingSince) >= Supervision.ReplyLimit)
        {
            state.Problem = $"Encore's background service didn't take a new connection within {Supervision.ReplyLimit.TotalSeconds:0} s.";
            return ConnectOutcome.Unanswered;
        }

        return null;
    }

    // No engine answered: launch one, then keep trying until it answers, exits or runs out of time.
    private ConnectOutcome? AfterNoAnswerFromNoEngine(EngineRestartReason? restart)
    {
        if (Launched is null)
        {
            if (TryLaunch(restart))
            {
                _waitingSince = options.Time.GetTimestamp();
                return null;
            }
        }
        else if (Launched.HasExited)
        {
            state.Problem = $"Encore's background service exited with code {Launched.ExitCode}.";
            LaunchFailure = Launched.ExitCode != 0 ? EngineRestartReason.Crashed : null;
        }
        else if (options.Time.GetElapsedTime(_waitingSince) >= options.LaunchTimeout)
        {
            KillLaunchedThatNeverAnswered();
        }
        else
        {
            return null;
        }

        state.SetStatus(EngineStatus.NotRunning);
        return ConnectOutcome.Failed;
    }

    // It never answered: don't leave it running with nothing to shut it down on Quit.
    private void KillLaunchedThatNeverAnswered()
    {
        try
        {
            Launched!.Kill();
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            // It exited meanwhile.
        }

        state.Problem = $"Encore's background service didn't answer within {options.LaunchTimeout.TotalSeconds:0} s.";
        LaunchFailure = EngineRestartReason.Hung;
    }

    private bool TryLaunch(EngineRestartReason? restart)
    {
        try
        {
            Launched = EngineLauncher.Launch(options, restart);
            return true;
        }
        catch (Win32Exception e)
        {
            state.Problem = $"Encore's background service at '{options.EnginePath}' couldn't be started: {e.Message}";
            return false;
        }
    }

    // Makes the new client the current link, once its first message names the process that serves the pipe.
    private async Task UseAsync(EngineClient client, Process verified, EngineWatchdog watchdog)
    {
        if (client.State.Pid != verified.Id)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new ErrorCodeException(
                ErrorCodes.EngineUntrusted,
                $"The background service's first message names process {client.State.Pid}, but process {verified.Id} serves its pipe.");
        }

        // The snapshot is the connection's first event.
        watchdog.EventReceived();
        if (state.ReplaceLink(new EngineLink(client, verified, watchdog)) is { } old)
        {
            await old.DisposeAsync().ConfigureAwait(false);
        }

        state.Problem = null;
        state.SetStatus(client.IsConnected ? EngineStatus.Connected : EngineStatus.Disconnected);
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
            if (image is not null && IsTrustedImage(image))
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

    private bool IsTrustedImage(string image) =>
        string.Equals(Path.GetFullPath(image), Path.GetFullPath(options.TrustedEnginePath), StringComparison.OrdinalIgnoreCase);
}
