using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Core.Sessions;

namespace Encore.Engine.Sessions;

// A restart on its own thread: the chain is torn down first, then rebuilt or switched.
internal sealed partial class CaptureSession
{
    /// <summary>Switches to another spec's chain at a Reset, tearing the current one down first. See docs/code-notes.md.</summary>
    public bool SwitchChain(SessionSpec next)
    {
        ArgumentNullException.ThrowIfNull(next);
        lock (_gate)
        {
            lock (_chainLock)
            {
                if (!HasRing || !Volatile.Read(ref _anchored) || IsEndingLocked || _deviceLost || IsSwitchingLocked || _restarting
                    || _prepared is not null || _building || _tearingDown > 0
                    || _current is not { IsBuilt: true, IsDisposed: false, Tap.HasWriter: false } old)
                {
                    _log.LogInformation(
                        "The encoder chain switch at the Reset was refused: the session isn't live, or a chain is being built, "
                        + "switched or recorded from; it is retried at the next Reset");
                    return false;
                }

                if (ChainKey.SameChain(next, old.Spec))
                {
                    return true;
                }

                _wanted = null;
                _restarting = true;
                _restartThread = new Thread(() => Restart(old, "settings changed while a Monitor Recording runs", next))
                {
                    Name = "Encoder chain switch",
                    IsBackground = true,
                };
                _restartThread.Start();
            }
        }

        return true;
    }

    /// <summary>The restart's work, on its own thread; with switchTo it is a switch: a fresh ring, and the gap is raised.</summary>
    private void Restart(EncoderChain old, string reason, SessionSpec? switchTo = null)
    {
        var began = Stopwatch.GetTimestamp();
        if (!TakeOutOfFeed(old))
        {
            return;
        }

        _log.LogInformation(
            "Encoder restart ({Reason}) at {At:0.000} s after the anchor: the chain is torn down and rebuilt",
            reason, MicrosAt(began) / 1e6);

        // Kept out of the teardown: closing it ends the Recording, which must post back after the orchestrator hears of the restart.
        var swap = new ChainSwap { Tap = old.TakeTap() };
        try
        {
            Replace(old, swap, switchTo, began);
        }
        catch (Exception e) when (IsDeviceLostNow() || TakeDeviceLoss(e))
        {
            LeaveToRecovery(swap, reason, switchTo, e);
        }
        catch (Exception e)
        {
            FailRestart(old, swap, reason, switchTo, e);
        }
        finally
        {
            // The old chain's Recording ends here, at its last packet, after the orchestrator was told.
            swap.Tap?.Dispose();
        }
    }

    /// <summary>Stops the capture thread feeding the chain, under the gate; false when stopped or lost, and the restart ends.</summary>
    private bool TakeOutOfFeed(EncoderChain old)
    {
        lock (_gate)
        {
            // A lost device is the recovery's: it tears this chain down and rebuilds it.
            if (_stopped || IsDeviceLost())
            {
                lock (_chainLock)
                {
                    _restarting = false;
                }

                return false;
            }

            old.Fed = false;
        }

        return true;
    }

    /// <summary>Tears the old chain down, builds its replacement and makes it current, then tells the orchestrator.</summary>
    private void Replace(EncoderChain old, ChainSwap swap, SessionSpec? switchTo, long began)
    {
        // Each save bound to it cuts at its press, since its keyframe never comes; the ring's restart needs them done.
        WaitForWriters(old, began, lost: false);

        // A switch's new chain takes a fresh ring: the old one goes with its chain.
        old.Dispose(keepRing: switchTo is null);
        if (_faults.Restart() is { } fault)
        {
            throw fault;
        }

        var next = NewReplacement(old, switchTo);
        swap.Next = next;
        next.Subscribe();
        if (switchTo is not null)
        {
            next.Build(_anchorQpc, first: false, latency: null);
        }
        else
        {
            next.Build(_anchorQpc, first: false, latency: null, restartOf: old.Ring, window: old.WindowSeconds);
        }

        var now = Stopwatch.GetTimestamp();
        next.StartAudio(now);
        InstallReplacement(swap, next, old);
        if (switchTo is not null)
        {
            _log.LogInformation(
                "The encoder chain switched at the Reset in {Ms:0} ms, torn down first: {Size}, {Tracks} Tracks from here on",
                (now - began) * 1000.0 / _qpcFrequency, next.OutputSize, switchTo.Audio.Tracks.Count);
            ReportSwitchGap(next);
            return;
        }

        _log.LogInformation(
            "The encoder chain restarted in {Ms:0} ms; the frames captured meanwhile were dropped",
            (now - began) * 1000.0 / _qpcFrequency);
        _events.ChainRestarted(now);
    }

    /// <summary>The chain that replaces the old one, not yet built: the switch's spec, else the old chain's own.</summary>
    private EncoderChain NewReplacement(EncoderChain old, SessionSpec? switchTo)
    {
        if (switchTo is null)
        {
            return NewChain(old.Spec, old.EncoderName, old.Codec, null, _device!);
        }

        var (encoder, codec) = _encoderResolver.Resolve(switchTo);
        return NewChain(switchTo, encoder, codec, null, _device!);
    }

    /// <summary>Makes the replacement current, under both locks; a device lost meanwhile throws, for the recovery.</summary>
    private void InstallReplacement(ChainSwap swap, EncoderChain next, EncoderChain old)
    {
        lock (_gate)
        {
            lock (_chainLock)
            {
                if (_deviceLost)
                {
                    // Built on the lost device: the recovery rebuilds from the old chain's ring.
                    throw new InvalidOperationException("The GPU device was lost during the encoder restart.");
                }

                _current = next;
                swap.Installed = true;
                _restarting = false;
                CompleteMoveLocked(next);
                if (next.Fps != old.Fps)
                {
                    _capture?.SetFps(next.Fps);
                }

                StartBuildLocked();
            }
        }
    }

    /// <summary>The device was lost under the restart: the recovery takes it from here, and the ring is kept.</summary>
    private void LeaveToRecovery(ChainSwap swap, string reason, SessionSpec? switchTo, Exception e)
    {
        if (!swap.Installed)
        {
            swap.Next?.Dispose(keepRing: switchTo is null);
        }

        _log.LogWarning(
            "The encoder restart ({Reason}) stopped: the GPU device was lost ({Message}); the session recovers instead",
            reason, e.Message);
        lock (_gate)
        {
            lock (_chainLock)
            {
                _restarting = false;

                // The orchestrator already took the switch's spec: the recovery rebuilds it.
                if (switchTo is not null)
                {
                    _rebuildSpec = switchTo;
                }

                if (_lostTap is null)
                {
                    _lostTap = swap.Tap;
                    swap.Tap = null;
                }
            }
        }
    }

    /// <summary>The rebuild failed: what was not yet current goes, with the ring, and the session fails.</summary>
    private void FailRestart(EncoderChain old, ChainSwap swap, string reason, SessionSpec? switchTo, Exception e)
    {
        // Not yet current: the new chain and the ring go with the session, which fails here.
        if (!swap.Installed)
        {
            swap.Next?.Dispose();
            if (switchTo is null)
            {
                old.Ring?.Dispose();
            }
        }

        _log.LogError("The encoder restart ({Reason}) failed: {Message}", reason, e.Message);

        // Failed first, so the session is stopped before anything may act on the disposed chain again.
        Fail(new ErrorCodeException(ErrorCodes.EncodeFailed, "The encoder restart couldn't rebuild the encoder chain: " + e.Message, e));
        lock (_gate)
        {
            lock (_chainLock)
            {
                _restarting = false;
            }
        }
    }

    /// <summary>Waits for a forced restart that is running; takes the chain lock only to read its thread.</summary>
    private void JoinRestart()
    {
        Thread? restart;
        lock (_chainLock)
        {
            restart = _restartThread;
        }

        restart?.Join();
    }
}
