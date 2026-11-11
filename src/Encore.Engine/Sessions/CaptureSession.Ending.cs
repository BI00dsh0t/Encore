using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Core.Sessions;
using Encore.Platform.Capture;
using Encore.Platform.FFmpeg;

namespace Encore.Engine.Sessions;

// Ending: the flush, the stats file, and freeing the chains and the device.
internal sealed partial class CaptureSession
{
    public SessionOutcome End(string reason)
    {
        var (anchored, capture, watch) = StopAndTakeCapture(reason);
        _firstFrameTimer.Dispose();
        watch?.Dispose();
        WaitForRestartAndRecovery();
        EncoderChain? chain;
        bool lost;
        lock (_chainLock)
        {
            chain = _current;
            lost = _deviceLost;
        }

        try
        {
            if (anchored)
            {
                _stats?.SnapshotStop();
            }

            _log.LogInformation("Stopping: {Reason}", _stopReason);
            capture?.Dispose();
            if (!anchored)
            {
                return NeverAnchored();
            }

            // A failed restart left nothing to flush, and a lost device's chain can't be flushed.
            if (!chain!.IsDisposed && !lost)
            {
                Flush(chain);
            }
        }
        finally
        {
            CloseOutput(chain);
        }

        if (_failure is null)
        {
            LogEnd();
        }

        return new SessionOutcome(_path, _stopReason, _failure);
    }

    /// <summary>Stops the session and takes its capture and device watch out, under the gate.</summary>
    private (bool Anchored, ICaptureSource? Capture, Timer? Watch) StopAndTakeCapture(string reason)
    {
        // A first frame still opening the encoders holds the gate; let it finish.
        lock (_gate)
        {
            RequestStop(reason);
            var anchored = _anchored;
            var capture = _capture;
            _capture = null;
            var watch = _deviceWatch;
            _deviceWatch = null;
            return (anchored, capture, watch);
        }
    }

    /// <summary>A forced restart or recovery that is running finishes first; a recovery sees the stop and installs nothing.</summary>
    private void WaitForRestartAndRecovery()
    {
        Thread? restart;
        Thread? recovery;
        lock (_chainLock)
        {
            restart = _restartThread;
            recovery = _recoveryThread;
        }

        restart?.Join();

        // Bounded: an attempt hung on the driver must not freeze the orchestrator.
        if (recovery is not null && !recovery.Join(RecoveryJoinWait))
        {
            _log.LogWarning(
                "The GPU device recovery hadn't finished within {Seconds:0} s of the stop; the session ends without it",
                RecoveryJoinWait.TotalSeconds);
        }
    }

    /// <summary>The outcome of a session that stopped before its first frame: a failure, with no file.</summary>
    private SessionOutcome NeverAnchored()
    {
        var message = _failure?.Message ?? $"Capture stopped ({_stopReason}) before the first frame.";
        var code = _failure is null ? ErrorCodes.CaptureFailed : EngineFailure.CodeOf(_failure);
        return new SessionOutcome(null, _stopReason, new EngineFailure(code, message));
    }

    /// <summary>Flushes a live chain once capture has stopped, then writes the stats file.</summary>
    private void Flush(EncoderChain chain)
    {
        // No frame can arrive now; let the encoder thread offer what is queued before the flush.
        chain.CompleteVideo();

        // Capture is stopped and no frame is in flight; stop audio, then drain everything.
        chain.StopTaps();

        // Silent Tracks stop with the device taps, not after the teardown below.
        chain.StopSilenceClock();
        chain.ReportAudioFaults();
        chain.Finish(_lastFrameQpc);
        WriteStats(chain);
    }

    /// <summary>Closes the Recording tap, the chains and the device.</summary>
    private void CloseOutput(EncoderChain? chain)
    {
        // A save still waiting has every packet it will get, and so has a Recording.
        _outputFinished = true;
        chain?.Tap?.Close();
        CloseChains();
        DisposeAll();
        TakeLostTap()?.Dispose();
    }

    /// <summary>Logs how a session that didn't fail ended: what it wrote or buffered.</summary>
    private void LogEnd()
    {
        var seconds = (double)(_lastFrameQpc - _anchorQpc) / _qpcFrequency;
        if (IsMonitor)
        {
            _log.LogInformation("Monitor capture of {Seconds:0.0} s ended", seconds);
        }
        else if (_path is null && _keepRing)
        {
            _log.LogInformation("Draining: buffered {Seconds:0.0} s; Rewind is kept for saves until the drain is released", seconds);
        }
        else if (_path is null)
        {
            _log.LogInformation("Buffered {Seconds:0.0} s; Rewind is released once no save holds it", seconds);
        }
        else
        {
            _log.LogInformation("Wrote {Seconds:0.0} s to {Path}", seconds, _path);
        }
    }

    /// <summary>Takes the lost chain's Recording tap out, for the caller to close; null when there is none.</summary>
    private RecordingTap? TakeLostTap()
    {
        lock (_chainLock)
        {
            var tap = _lostTap;
            _lostTap = null;
            return tap;
        }
    }

    /// <summary>Like <see cref="End"/>, but in Replay mode the ring is kept, so a save still cuts from it until Release.</summary>
    public SessionOutcome Drain(string reason)
    {
        _keepRing = HasRing;
        return End(reason);
    }

    /// <summary>Frees the ring <see cref="Drain"/> kept, once; a save still writing keeps its own lease.</summary>
    public void Release()
    {
        if (!_keepRing || _released)
        {
            return;
        }

        _released = true;
        ReplayRingSink? ring;
        lock (_chainLock)
        {
            ring = _current?.Ring;
        }

        if (ring is not null)
        {
            ring.Dispose();
            _log.LogInformation("The drained Rewind is released once no save holds it");
        }
    }

    /// <summary>End's side of the chains: running builds and teardowns are waited for, then prepared and armed chains go.</summary>
    private void CloseChains()
    {
        List<Thread> workers;
        EncoderChain? prepared;
        lock (_chainLock)
        {
            _chainsClosed = true;
            workers = _workers.TakeAll();
            prepared = _prepared;
            _prepared = null;
            _wanted = null;
        }

        foreach (var worker in workers)
        {
            worker.Join();
        }

        EncoderChain? armed;
        lock (_gate)
        {
            lock (_chainLock)
            {
                armed = _armed;
                _armed = null;
            }
        }

        prepared?.Dispose();
        armed?.Dispose();
    }

    /// <summary>Frees the replaced chain, the current one (a drained session keeps its ring) and the device.</summary>
    private void DisposeAll()
    {
        EncoderChain? retiring;
        EncoderChain? current;
        lock (_chainLock)
        {
            retiring = _retiring;
            current = _current;
        }

        retiring?.Dispose();

        // A save still writing holds its own lease; a chain a GPU reset tore down gives up its kept ring here.
        if (current is { IsDisposed: true } && !_keepRing)
        {
            current.Ring?.Dispose();
        }

        current?.Dispose(keepRing: _keepRing);
        _device?.Dispose();
    }

    /// <summary>Writes the stats file once the trailer is written.</summary>
    private void WriteStats(EncoderChain chain)
    {
        if (_stats is null || _options.StatsPath is not { } path)
        {
            return;
        }

        var run = new SessionRun(
            _path ?? "",
            _window?.Title ?? "",
            _window?.ProcessName,
            _device?.AdapterName ?? "",
            _stopReason,
            (double)(_lastFrameQpc - _anchorQpc) / _qpcFrequency);
        if (SessionStatsReport.For(_stats, chain, _options, run) is not { } report)
        {
            return;
        }

        try
        {
            CaptureStatsFile.Write(path, report);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Fail(new EngineFailure(ErrorCodes.StatsWriteFailed, $"Could not write the stats file '{path}': {e.Message}"));
            return;
        }

        _log.LogInformation("Stats written to {Path}: {Encoded} frames encoded ({Fresh} new), latency p99 {P99:0.0} ms",
            path, report.Video.FramesEncoded, report.Video.FreshSlots, report.Latency.P99Ms);
    }
}
