using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Core.Output;
using Encore.Platform.Capture;

namespace Encore.Engine.Sessions;

// Matching the game's size: a new size that held moves Rewind to a chain of that size.
internal sealed partial class CaptureSession
{
    /// <summary>Whether a size move may build its chain ahead (false while a Monitor Recording holds the other chain).</summary>
    public void SetBuildAhead(bool allowed) => _buildAhead = allowed;

    /// <summary>Watches one frame's size, under the gate: a size that held 3 s starts a move, unless a Recording is attached.</summary>
    private void WatchSourceSize(long qpc, VideoSize content)
    {
        EncoderChain? current;
        bool moving;
        bool reserved;
        lock (_chainLock)
        {
            current = _current;
            moving = _movingTo is not null;
            reserved = RecordReservedLocked();
            TryStartArmLocked();
        }

        if (moving || current is not { IsBuilt: true, IsDisposed: false, Spec.Snapshot.Video.MatchSource: true })
        {
            return;
        }

        if (_sizeWatch.Observe(
                qpc, content.Width, content.Height, current.OutputSize.Width, current.OutputSize.Height, current.Codec,
                reserved || current.Tap is { HasWriter: true }) is { } target)
        {
            StartMove(new VideoSize(target.Width, target.Height));
        }
    }

    /// <summary>Moves Rewind to a new size, under the gate; refused while anything else is changing. See docs/code-notes.md.</summary>
    private void StartMove(VideoSize target)
    {
        lock (_chainLock)
        {
            if (IsEndingLocked || _deviceLost || _restarting || _building || _tearingDown > 0
                || IsSwitchingLocked || _prepared is not null || RecordReservedLocked()
                || _current is not { IsBuilt: true, IsDisposed: false, Tap.HasWriter: false } old)
            {
                _log.LogInformation(
                    "The game's size changed to {Size} and held; the move waits, since another chain change or a Recording is running",
                    target);
                return;
            }

            _movingTo = target;
            if (_buildAhead)
            {
                _log.LogInformation("The game's size changed to {Size} and held 3 s: Rewind moves to it (built ahead)", target);
                _wanted = old.Spec;
                _armMove = true;
                StartBuildLocked();
            }
            else
            {
                _log.LogInformation(
                    "The game's size changed to {Size} and held 3 s: Rewind moves to it, torn down first while a Monitor Recording runs",
                    target);
                _wanted = null;
                _restarting = true;
                _restartThread = new Thread(() => Restart(old, "the game's size changed while a Monitor Recording runs", old.Spec))
                {
                    Name = "Encoder chain move",
                    IsBackground = true,
                };
                _restartThread.Start();
            }
        }
    }

    /// <summary>Queues <see cref="ArmMove"/> when a move's chain is prepared and nothing blocks it. Under the chain lock.</summary>
    private void TryStartArmLocked()
    {
        if (!_armMove || _armPending || _prepared is null || IsSwitchingLocked || _restarting || _deviceLost
            || _current is not { Tap.HasWriter: false } || _chainsClosed || RecordReservedLocked())
        {
            return;
        }

        _armPending = true;
        _workers.Start("Encoder chain move", ArmMove);
    }

    /// <summary>Arms a size move's prepared chain with no save, on a background thread; never while a Recording is attached.</summary>
    private void ArmMove()
    {
        if (_faults.MoveArmDelay > TimeSpan.Zero)
        {
            Thread.Sleep(_faults.MoveArmDelay);
        }

        EncoderChain? armed = null;
        long armQpc;
        lock (_gate)
        {
            armQpc = Stopwatch.GetTimestamp();
            lock (_chainLock)
            {
                _armPending = false;
                if (!IsEndingLocked && _armMove && _prepared is { } prepared && !IsSwitchingLocked && !_restarting && !_deviceLost
                    && _current is { Tap.HasWriter: false } current && !RecordReservedLocked())
                {
                    _prepared = null;
                    _wanted = null;
                    _armMove = false;
                    _armed = prepared;
                    _armedQpc = armQpc;
                    current.SwitchEndUs = MicrosAt(armQpc);
                    armed = prepared;
                }
            }
        }

        if (armed is null)
        {
            return;
        }

        armed.StartAudio(armQpc);
        _log.LogInformation(
            "The size move switches Rewind to its new encoder chain {At:0.000} s after the anchor", MicrosAt(armQpc) / 1e6);
    }

    /// <summary>A move ends once a chain at its size is current: that size is the source's from here. Under the chain lock.</summary>
    private void CompleteMoveLocked(EncoderChain current)
    {
        if (_movingTo is { } moved && SourceOutputSize.For(moved.Width, moved.Height, current.Codec) is { } size
            && size == (current.OutputSize.Width, current.OutputSize.Height))
        {
            _sourceSize = moved;
            _movingTo = null;
            _armMove = false;
        }
    }

    /// <summary>A Recording starts on the current chain, so a pending size move is cancelled. Under the chain lock.</summary>
    private void CancelMoveLocked()
    {
        if (!_armMove)
        {
            return;
        }

        _log.LogInformation(
            "A Recording starts: the size move to {Size} is cancelled, and found again after it if the size still holds", _movingTo);
        MovesCancelled++;
        DropMoveLocked();
        _wanted = null;
        if (_prepared is { } prepared)
        {
            _prepared = null;
            TearDownLocked(prepared);
        }
    }

    /// <summary>Forgets a pending move, so the size must hold again. Under the chain lock.</summary>
    private void DropMoveLocked()
    {
        _movingTo = null;
        _armMove = false;
    }
}
