using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Core.Sessions;

namespace Encore.Engine.Sessions;

// Changing chains at a save: a chain built ahead, armed at a press, and the old one retired.
internal sealed partial class CaptureSession
{
    /// <summary>The live encoder chains: current, armed, replaced but still fed, prepared, and one being built.</summary>
    public int Chains
    {
        get
        {
            lock (_chainLock)
            {
                // A lost device's chains are torn down and rebuilt as one.
                if (_deviceLost)
                {
                    return 1;
                }

                var chains = 0;
                foreach (var chain in new[] { _current, _armed, _retiring, _prepared })
                {
                    if (chain is { IsDisposed: false })
                    {
                        chains++;
                    }
                }

                // An unwanted build is torn down as soon as it ends; a rebuild counts once the chain it replaces is gone.
                var building = _building && _wanted is not null;
                var rebuilding = _restarting && _current is not { IsDisposed: false };
                return chains + (building ? 1 : 0) + (rebuilding ? 1 : 0);
            }
        }
    }

    /// <summary>Builds a spec's chain in advance, on a background thread, for the next save press. See docs/code-notes.md.</summary>
    public void PrepareChain(SessionSpec? next)
    {
        if (!HasRing)
        {
            return;
        }

        lock (_chainLock)
        {
            if (_chainsClosed || _keepRing || _outputFinished || Volatile.Read(ref _stopped))
            {
                return;
            }

            if (_deviceLost)
            {
                // Only remembered: the recovery builds it once the new device is up.
                var rebuilt = _rebuildSpec ?? (_armed ?? _current)?.Spec ?? _spec;
                _wanted = next is null || ChainKey.SameChain(next, rebuilt) ? null : next;
                return;
            }

            if (!WantLocked(next, out var drop))
            {
                return;
            }

            if (drop is not null)
            {
                _log.LogInformation("The prepared encoder chain is no longer needed; it is torn down");
                TearDownLocked(drop);
            }

            StartBuildLocked();
        }
    }

    /// <summary>Records the spec asked for and hands back the prepared chain it replaces; false when nothing changes.</summary>
    private bool WantLocked(SessionSpec? next, out EncoderChain? drop)
    {
        drop = null;
        var current = (_armed ?? _current)?.Spec ?? _spec;
        if (next is not null && ChainKey.SameChain(next, current) && _armMove)
        {
            // The same settings leave a size move alone.
            return false;
        }

        if (next is null || ChainKey.SameChain(next, current))
        {
            // Null (a Monitor Recording takes the other chain) drops a size move too.
            drop = _prepared;
            _prepared = null;
            _wanted = null;
            DropMoveLocked();
            return true;
        }

        if (_wanted is { } wanted && ChainKey.SameChain(next, wanted))
        {
            return false;
        }

        _log.LogInformation(
            "The profile's settings need another encoder chain ({Size} at {Fps} fps, {Tracks} Tracks); it is built for the next save",
            $"{next.Snapshot.Video.Width}x{next.Snapshot.Video.Height}", next.Snapshot.Video.Fps, next.Audio.Tracks.Count);
        _wanted = next;
        drop = _prepared;
        _prepared = null;

        // The profile's chain is built at a pending move's size, and switches at a save.
        _armMove = false;
        return true;
    }

    /// <summary>Starts the build of the spec asked for, when nothing is in the way. Under the chain lock.</summary>
    private void StartBuildLocked()
    {
        if (_wanted is not { } spec || _chainsClosed || !Volatile.Read(ref _anchored) || _building || _restarting || _deviceLost
            || _prepared is not null || IsSwitchingLocked || _tearingDown > 0)
        {
            return;
        }

        _building = true;
        _workers.Start("Encoder chain build", () => Build(spec));
    }

    /// <summary>On a background thread: builds a spec's chain and hands it over as prepared when it is still wanted.</summary>
    private void Build(SessionSpec spec)
    {
        EncoderChain? chain = null;
        try
        {
            var (encoder, codec) = _encoderResolver.Resolve(spec);
            chain = NewChain(spec, encoder, codec, null, _device!);
            chain.Subscribe();
            chain.Build(_anchorQpc, first: false, latency: null);
            _log.LogInformation("The encoder chain for the next save is built: {Size} at {Fps} fps, {Tracks} Tracks",
                chain.OutputSize, chain.Fps, spec.Audio.Tracks.Count);
        }
        catch (Exception e)
        {
            _log.LogWarning(
                "The encoder chain for the profile's new settings couldn't be built; "
                + "the session keeps its own until the next Reset or settings change: {Message}",
                e.Message);
            chain?.Dispose();
            chain = null;
        }

        lock (_chainLock)
        {
            HandOverLocked(chain, spec);
        }
    }

    /// <summary>Ends a build: its chain becomes the prepared one when still wanted, else it is torn down. Under the chain lock.</summary>
    private void HandOverLocked(EncoderChain? chain, SessionSpec spec)
    {
        _building = false;

        // One built on a device lost meanwhile is torn down; the recovery builds it again.
        if (chain is not null && !_chainsClosed && !_outputFinished && !Volatile.Read(ref _stopped) && !_deviceLost
            && _wanted is { } wanted && ChainKey.SameChain(wanted, spec))
        {
            _prepared = chain;

            // A move's chain arms at once, without a save.
            TryStartArmLocked();
        }
        else if (chain is not null)
        {
            // Superseded meanwhile: torn down before the newer spec's build starts.
            TearDownLocked(chain);
        }
        else if (ReferenceEquals(_wanted, spec))
        {
            _wanted = null;

            // A move whose chain couldn't be built is forgotten: the size must hold again.
            DropMoveLocked();
        }

        // A newer spec asked for while this one built.
        StartBuildLocked();
    }

    /// <summary>Tears a chain down on a background thread; no build starts until it is gone. Under the chain lock.</summary>
    private void TearDownLocked(EncoderChain chain)
    {
        _tearingDown++;
        _workers.Start("Encoder chain teardown", () =>
        {
            try
            {
                chain.Dispose();
            }
            finally
            {
                lock (_chainLock)
                {
                    _tearingDown--;
                    StartBuildLocked();
                }
            }
        });
    }

    /// <summary>Arms the prepared chain at a save press, when one is ready and nothing else is changing. Orchestrator thread.</summary>
    private EncoderChain? TryArm(EncoderChain chain, long qpc, out long armQpc)
    {
        EncoderChain? armed = null;
        armQpc = qpc;
        lock (_gate)
        {
            if (_stopped)
            {
                return null;
            }

            // Under the gate no frame is in flight, so every frame captured after this instant reaches the new chain.
            armQpc = Math.Max(qpc, Stopwatch.GetTimestamp());

            lock (_chainLock)
            {
                if (_prepared is { } prepared && !IsSwitchingLocked && !_restarting && !_deviceLost && ReferenceEquals(chain, _current)
                    && chain.Tap is { HasWriter: false } && !_outputFinished && !_keepRing && !_chainsClosed)
                {
                    _prepared = null;
                    _wanted = null;
                    _armed = prepared;
                    _armedQpc = armQpc;
                    armed = prepared;
                }
            }
        }

        if (armed is null)
        {
            return null;
        }

        // Audio flows into it only once its taps are published, here.
        armed.StartAudio(qpc);
        _log.LogInformation("The save press {At:0.000} s after the anchor switches Rewind to the prepared encoder chain",
            MicrosAt(qpc) / 1e6);
        return armed;
    }

    /// <summary>Waits up to 3 s from began for the saves bound to a chain to cut; a lost chain's wait also ends at a stop.</summary>
    private void WaitForWriters(EncoderChain chain, long began, bool lost)
    {
        var deadline = began + (3 * _qpcFrequency);
        while (!lost || !Volatile.Read(ref _stopped))
        {
            lock (_chainLock)
            {
                if (chain.Writers == 0)
                {
                    return;
                }
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                if (lost)
                {
                    _log.LogWarning("A save bound to the lost encoder chain hadn't cut within 3 s; the chain is torn down anyway");
                }
                else
                {
                    _log.LogWarning("A save bound to the restarted encoder chain hadn't cut within 3 s; the chain is torn down anyway");
                }

                return;
            }

            Thread.Sleep(10);
        }
    }

    /// <summary>Whether the session is ending: stopped, finished, draining or closed. Read under both locks.</summary>
    private bool IsEndingLocked => _stopped || _outputFinished || _keepRing || _chainsClosed;

    /// <summary>Whether a chain switch is in flight: one is armed, or the replaced chain is still fed. Under the chain lock.</summary>
    private bool IsSwitchingLocked => _armed is not null || _retiring is not null;

    /// <summary>Ends a save's registration on a chain once it has cut or failed; a switching save records its Clip's end.</summary>
    private void EndWriter(EncoderChain chain, long? switchEndUs)
    {
        lock (_chainLock)
        {
            if (switchEndUs is { } end)
            {
                chain.SwitchEndUs = end;
            }

            chain.Writers--;
            TakeRetirementLocked(chain);
        }
    }

    /// <summary>Starts a chain's retirement once it is replaced and no save is bound to it. Under the chain lock.</summary>
    private void TakeRetirementLocked(EncoderChain chain)
    {
        if (!chain.Retiring || chain.Writers > 0 || chain.RetireStarted || _chainsClosed)
        {
            return;
        }

        chain.RetireStarted = true;
        _workers.Start("Encoder chain retirement", () => Retire(chain));
    }

    /// <summary>On a background thread: tears down the replaced chain, checks for a gap, then starts any build asked for.</summary>
    private void Retire(EncoderChain old)
    {
        lock (_gate)
        {
            old.Fed = false;
        }

        old.Dispose();
        _log.LogInformation("The replaced encoder chain is torn down");
        var next = old.Successor!;
        CheckGap(old, next);
        if (next.Fps != old.Fps)
        {
            lock (_gate)
            {
                _capture?.SetFps(next.Fps);
            }
        }

        lock (_gate)
        {
            lock (_chainLock)
            {
                if (ReferenceEquals(_retiring, old))
                {
                    _retiring = null;
                }

                StartBuildLocked();
            }
        }
    }

    /// <summary>Waits for the next chain's first keyframe; raises <c>reset-gap</c> when it is over a frame after the old Clip.</summary>
    private void CheckGap(EncoderChain old, EncoderChain next)
    {
        var end = old.SwitchEndUs;
        if (end == long.MinValue || next.Ring is not { } ring)
        {
            return;
        }

        var deadline = Stopwatch.GetTimestamp() + (long)(SaveWait.TotalSeconds * _qpcFrequency);
        while (ring.FirstKeyUs == long.MaxValue && !_chainsClosed && Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(10);
        }

        if (_chainsClosed)
        {
            return;
        }

        var first = ring.FirstKeyUs;
        if (first <= end + next.FrameUs)
        {
            return;
        }

        var message = first == long.MaxValue
            ? $"After your last saved Clip, the video didn't restart for {(MicrosAt(Stopwatch.GetTimestamp()) - end) / 1000} ms; "
                + "the next Clip starts when it does."
            : $"After your last saved Clip, the video restarted {(first - end) / 1000} ms late; the next Clip starts that much later.";
        _log.LogWarning("{Code}: {Message}", ErrorCodes.ResetGap, message);
        _options.ResetGap?.Invoke(message);
    }

    /// <summary>After a switch at a Reset: raises <c>reset-gap</c> with the gap from the last Reset to the first keyframe.</summary>
    private void ReportSwitchGap(EncoderChain next)
    {
        if (next.Ring is not { } ring)
        {
            return;
        }

        var deadline = Stopwatch.GetTimestamp() + (long)(SaveWait.TotalSeconds * _qpcFrequency);
        // End stops the session, then joins this thread: it must not wait out the deadline.
        while (ring.FirstKeyUs == long.MaxValue && !_chainsClosed && !_outputFinished
            && !Volatile.Read(ref _stopped) && Stopwatch.GetTimestamp() < deadline)
        {
            Thread.Sleep(10);
        }

        if (_chainsClosed || _outputFinished || Volatile.Read(ref _stopped))
        {
            return;
        }

        var reset = Volatile.Read(ref _resetUs);
        var first = ring.FirstKeyUs;
        var message = first == long.MaxValue
            ? "Your new video settings took effect after your last saved Clip, while a Monitor Recording runs; "
                + "the video didn't restart within 1 s, so the next Clip starts when it does."
            : "Your new video settings took effect after your last saved Clip, while a Monitor Recording runs; "
                + $"the next Clip starts {(reset == long.MinValue ? 0 : Math.Max(0, first - reset)) / 1000} ms after the last one ended.";
        _log.LogWarning("{Code}: {Message}", ErrorCodes.ResetGap, message);
        _options.ResetGap?.Invoke(message);
    }
}
