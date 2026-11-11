using Microsoft.Extensions.Logging;
using Encore.Platform.Capture;

namespace Encore.Engine.Sessions;

// For tests only: what a test reads from the session or forces on it.
internal sealed partial class CaptureSession
{
    /// <summary>For tests: which API the session captures through, <c>wgc</c> or <c>desktop-duplication</c>.</summary>
    internal string CaptureApi => _sources.Api;

    /// <summary>For tests: whether a size move is pending (building or waiting to arm), so a Record press now cancels it.</summary>
    internal bool MovePending
    {
        get
        {
            lock (_chainLock)
            {
                return _armMove;
            }
        }
    }

    /// <summary>For tests: how many size moves a Recording's start cancelled. Under the chain lock.</summary>
    internal int MovesCancelled { get; private set; }

    /// <summary>For tests: the Desktop Duplication capture's next acquire behaves as <c>DXGI_ERROR_ACCESS_LOST</c>.</summary>
    internal void SimulateCaptureAccessLost()
    {
        ICaptureSource? capture;
        lock (_gate)
        {
            capture = _capture;
        }

        if (capture is not DesktopDuplicationCapture duplication)
        {
            throw new InvalidOperationException("The session doesn't capture through Desktop Duplication.");
        }

        duplication.SimulateAccessLost();
    }

    /// <summary>For tests: the fault seam marks this session's device lost, as a driver reset would.</summary>
    internal void SimulateDeviceLoss()
    {
        lock (_gate)
        {
            _device?.SimulateLoss();
        }
    }

    /// <summary>A forced encoder restart, for tests: rebuilds the same spec into a new ring generation. docs/code-notes.md.</summary>
    internal bool RestartChain(string reason)
    {
        lock (_gate)
        {
            lock (_chainLock)
            {
                if (!HasRing || !Volatile.Read(ref _anchored) || _stopped || _outputFinished || _keepRing || _chainsClosed || _deviceLost
                    || _armed is not null || _retiring is not null || _restarting || _current is not { IsBuilt: true, IsDisposed: false } old)
                {
                    _log.LogInformation("The encoder restart ({Reason}) was refused: the session isn't live, or a chain switch or restart is running", reason);
                    return false;
                }

                _restarting = true;
                _restartThread = new Thread(() => Restart(old, reason)) { Name = "Encoder chain restart", IsBackground = true };
                _restartThread.Start();
            }
        }

        return true;
    }
}
