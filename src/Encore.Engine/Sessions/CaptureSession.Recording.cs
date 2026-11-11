using System.Diagnostics;
using Encore.Core.Sessions;

namespace Encore.Engine.Sessions;

// Recordings: starting one on the current chain, which a Record press reserves.
internal sealed partial class CaptureSession
{
    /// <summary>Starts a Recording from a QPC time on the current chain; null when the session can't record now.</summary>
    public IRecordingWrite? Record(long qpc)
    {
        // The anchor's release write publishes the tap and the encoder.
        if ((!_options.Replay && !IsMonitor) || _released || _outputFinished || !Volatile.Read(ref _anchored))
        {
            return null;
        }

        EncoderChain? chain;
        lock (_chainLock)
        {
            if (_armed is not null || _restarting || _deviceLost)
            {
                return null;
            }

            chain = _current;
            if (chain is null || chain.IsDisposed || chain.Tap is not { HasWriter: false } || chain.Video is null)
            {
                return null;
            }

            // The Recording keeps this chain: a pending size move is cancelled and the chain reserved for its writer.
            CancelMoveLocked();
            _recordReservedUntil = Stopwatch.GetTimestamp() + (long)(RecordReserve.TotalSeconds * _qpcFrequency);
        }

        return new RecordingWrite(this, chain, chain.Tap!, chain.Video!, qpc);
    }

    /// <summary>Whether a Record press holds the current chain until its writer attaches. Under the chain lock.</summary>
    private bool RecordReservedLocked() => Stopwatch.GetTimestamp() < _recordReservedUntil;
}
