using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Core.Sessions;

namespace Encore.Engine.Sessions;

// Clips: starting a save, and the Reset and Buffer Window it moves.
internal sealed partial class CaptureSession
{
    /// <summary>Starts a Clip write up to a QPC time, bound to the current chain; null when refused. See docs/code-notes.md.</summary>
    public IClipWrite? Save(long qpc)
    {
        // The anchor's release write publishes the chain's ring and encoder.
        if (!HasRing || _released || !Volatile.Read(ref _anchored))
        {
            return null;
        }

        EncoderChain? chain;
        bool lost;
        lock (_chainLock)
        {
            // A forced restart leaves nothing to cut until it is done; a lost device's chain is cut as it is.
            lost = _deviceLost;
            chain = _restarting && !lost ? null : _current;
        }

        if (lost)
        {
            if (chain?.Ring is null)
            {
                return null;
            }

            if (qpc - _anchorQpc < _qpcFrequency)
            {
                throw new ErrorCodeException(ErrorCodes.ClipTooShort, "Rewind holds less than 1 s; there is nothing to save yet.");
            }

            // No encoder runs to ask for a keyframe: the Clip ends at the ring's last packet before the press.
            return new ClipWrite(this, chain, null, qpc, chain.WindowSeconds);
        }

        // A chain disposed while the output still runs is one a failed restart left; a drained one keeps its ring.
        if (chain is null || (chain.IsDisposed && !_outputFinished) || chain.Ring is null || chain.Video is not { } video)
        {
            return null;
        }

        if (qpc - _anchorQpc < _qpcFrequency)
        {
            throw new ErrorCodeException(ErrorCodes.ClipTooShort, "Rewind holds less than 1 s; there is nothing to save yet.");
        }

        // A drained session's encoder is already disposed: never ask it for a keyframe.
        return new ClipWrite(this, chain, _outputFinished ? null : video, qpc, chain.WindowSeconds);
    }

    /// <summary>The Buffer Window from the next Reset on; the orchestrator calls it just before a Reset.</summary>
    public void SetBufferWindow(int seconds) => _pendingWindow = seconds;

    /// <summary>At a Reset: moves the ring to the Buffer Window that was asked for, seeded from the Reset keyframe on.</summary>
    private void ResizeAt(long us)
    {
        var pending = _pendingWindow;
        EncoderChain? chain;
        lock (_chainLock)
        {
            // The window waits for the next Reset while a chain is armed, a restart runs or the device is lost.
            chain = _armed is null && !_restarting && !_deviceLost ? _current : null;
        }

        if (_keepRing || _outputFinished || chain?.Ring is not { } ring || pending < 1 || pending == chain.WindowSeconds)
        {
            return;
        }

        var next = chain.NewRing(pending);
        int generation;
        try
        {
            generation = ring.Resize(next, chain.SnapStart(us));
        }
        catch
        {
            next.Dispose();
            throw;
        }

        _log.LogInformation(
            "Buffer Window {Old} s -> {New} s at the Reset {Reset:0.000} s after the anchor: "
            + "ring generation {Generation} {Storage}, {Capacity} packets preallocated",
            chain.WindowSeconds, pending, us / 1e6, generation, ReplayRings.StorageOf(next), chain.RingCapacity(pending));
        chain.WindowSeconds = pending;
    }

    /// <summary>Moves the Reset forward to <paramref name="us"/> (µs since the anchor); an earlier one is ignored.</summary>
    private void ResetTo(long us)
    {
        var current = Volatile.Read(ref _resetUs);
        while (us > current)
        {
            var seen = Interlocked.CompareExchange(ref _resetUs, us, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
