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
                if (!HasRing || !Volatile.Read(ref _anchored) || _stopped || _outputFinished || _keepRing || _chainsClosed || _deviceLost
                    || _armed is not null || _retiring is not null || _restarting || _prepared is not null || _building || _tearingDown > 0
                    || _current is not { IsBuilt: true, IsDisposed: false, Tap.HasWriter: false } old)
                {
                    _log.LogInformation("The encoder chain switch at the Reset was refused: the session isn't live, or a chain is being built, switched or recorded from; it is retried at the next Reset");
                    return false;
                }

                if (ChainKey.SameChain(next, old.Spec))
                {
                    return true;
                }

                _wanted = null;
                _restarting = true;
                _restartThread = new Thread(() => Restart(old, "settings changed while a Monitor Recording runs", next)) { Name = "Encoder chain switch", IsBackground = true };
                _restartThread.Start();
            }
        }

        return true;
    }

    /// <summary>The restart's work, on its own thread; with switchTo it is a switch: a fresh ring, and the gap is raised.</summary>
    private void Restart(EncoderChain old, string reason, SessionSpec? switchTo = null)
    {
        var began = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            // A lost device is the recovery's: it tears this chain down and rebuilds it.
            if (_stopped || IsDeviceLost())
            {
                lock (_chainLock)
                {
                    _restarting = false;
                }

                return;
            }

            old.Fed = false;
        }

        _log.LogInformation("Encoder restart ({Reason}) at {At:0.000} s after the anchor: the chain is torn down and rebuilt", reason, MicrosAt(began) / 1e6);

        // Kept out of the teardown: closing it ends the Recording, which must post back after the orchestrator hears of the restart.
        var tap = old.TakeTap();
        EncoderChain? next = null;
        var installed = false;
        try
        {
            // Each save bound to it cuts at its press, since its keyframe never comes; the ring's restart needs them done.
            var deadline = began + (3 * _qpcFrequency);
            while (true)
            {
                lock (_chainLock)
                {
                    if (old.Writers == 0)
                    {
                        break;
                    }
                }

                if (Stopwatch.GetTimestamp() >= deadline)
                {
                    _log.LogWarning("A save bound to the restarted encoder chain hadn't cut within 3 s; the chain is torn down anyway");
                    break;
                }

                Thread.Sleep(10);
            }

            // A switch's new chain takes a fresh ring: the old one goes with its chain.
            old.Dispose(keepRing: switchTo is null);
            if (_faults.Restart() is { } fault)
            {
                throw fault;
            }

            if (switchTo is not null)
            {
                var (encoder, codec) = _encoderResolver.Resolve(switchTo);
                next = NewChain(switchTo, encoder, codec, null, _device!);
                next.Subscribe();
                next.Build(_anchorQpc, first: false, latency: null);
            }
            else
            {
                next = NewChain(old.Spec, old.EncoderName, old.Codec, null, _device!);
                next.Subscribe();
                next.Build(_anchorQpc, first: false, latency: null, restartOf: old.Ring, window: old.WindowSeconds);
            }

            var now = Stopwatch.GetTimestamp();
            next.StartAudio(now);
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
                    installed = true;
                    _restarting = false;
                    CompleteMoveLocked(next);
                    if (next.Fps != old.Fps)
                    {
                        _capture?.SetFps(next.Fps);
                    }

                    StartBuildLocked();
                }
            }

            if (switchTo is not null)
            {
                _log.LogInformation("The encoder chain switched at the Reset in {Ms:0} ms, torn down first: {Size}, {Tracks} Tracks from here on",
                    (now - began) * 1000.0 / _qpcFrequency, next.OutputSize, switchTo.Audio.Tracks.Count);
                ReportSwitchGap(next);
                return;
            }

            _log.LogInformation("The encoder chain restarted in {Ms:0} ms; the frames captured meanwhile were dropped", (now - began) * 1000.0 / _qpcFrequency);
            _events.ChainRestarted(now);
        }
        catch (Exception e) when (IsDeviceLostNow() || TakeDeviceLoss(e))
        {
            // The device was lost under the restart: the recovery takes it from here, and the ring is kept.
            if (!installed)
            {
                next?.Dispose(keepRing: switchTo is null);
            }

            _log.LogWarning("The encoder restart ({Reason}) stopped: the GPU device was lost ({Message}); the session recovers instead", reason, e.Message);
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
                        _lostTap = tap;
                        tap = null;
                    }
                }
            }
        }
        catch (Exception e)
        {
            // Not yet current: the new chain and the ring go with the session, which fails here.
            if (!installed)
            {
                next?.Dispose();
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
        finally
        {
            // The old chain's Recording ends here, at its last packet, after the orchestrator was told.
            tap?.Dispose();
        }
    }
}
