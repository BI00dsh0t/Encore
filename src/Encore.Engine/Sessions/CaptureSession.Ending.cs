using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Core.Sessions;
using Encore.Platform.Audio;
using Encore.Platform.Capture;
using Encore.Platform.FFmpeg;
using SessionFlow = Encore.Contracts.Settings.AudioFlow;

namespace Encore.Engine.Sessions;

// Ending: the flush, the stats file, and freeing the chains and the device.
internal sealed partial class CaptureSession
{
    public SessionOutcome End(string reason)
    {
        bool anchored;
        ICaptureSource? capture;
        Timer? watch;

        // A first frame still opening the encoders holds the gate; let it finish.
        lock (_gate)
        {
            RequestStop(reason);
            anchored = _anchored;
            capture = _capture;
            _capture = null;
            watch = _deviceWatch;
            _deviceWatch = null;
        }

        _firstFrameTimer.Dispose();
        watch?.Dispose();

        // A forced restart or recovery that is running finishes first; a recovery sees the stop and installs nothing.
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
            _log.LogWarning("The GPU device recovery hadn't finished within {Seconds:0} s of the stop; the session ends without it", RecoveryJoinWait.TotalSeconds);
        }

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
                var message = _failure?.Message ?? $"Capture stopped ({_stopReason}) before the first frame.";
                return new SessionOutcome(null, _stopReason, new EngineFailure(_failure is null ? ErrorCodes.CaptureFailed : EngineFailure.CodeOf(_failure), message));
            }

            // A failed restart left nothing to flush, and a lost device's chain can't be flushed.
            if (!chain!.IsDisposed && !lost)
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
        }
        finally
        {
            // A save still waiting has every packet it will get, and so has a Recording.
            _outputFinished = true;
            chain?.Tap?.Close();
            CloseChains();
            DisposeAll();
            RecordingTap? lostTap;
            lock (_chainLock)
            {
                lostTap = _lostTap;
                _lostTap = null;
            }

            lostTap?.Dispose();
        }

        if (_failure is null)
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

        return new SessionOutcome(_path, _stopReason, _failure);
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
        var encoder = chain.Video;
        if (_stats is null || _options.StatsPath is not { } path || encoder is null
            || chain.AudioEncoders.Count != chain.Spec.Audio.Tracks.Count || chain.AudioTimelines.Count != chain.AudioTracks.Count)
        {
            return;
        }

        var cadence = _stats.Cadence;
        var arrived = cadence.Frames;
        var stepped = arrived - cadence.NonIncreasing; // frames that advanced the capture clock
        var span = cadence.SpanSeconds;
        var slots = stepped + cadence.MissedSlots;
        var paced = chain.Paced;
        var pacer = paced?.Pacer;
        var emitted = pacer?.EmittedSlots ?? 0;
        var repeated = pacer?.RepeatedSlots ?? 0;
        var video = new VideoReport(
            chain.OutputSize.Width,
            chain.OutputSize.Height,
            arrived,
            encoder.EncodedFrames,
            repeated,
            (paced?.QueueDrops ?? 0) + encoder.PoolMisses,
            cadence.NonIncreasing,
            cadence.MissedSlots,
            cadence.Gaps,
            cadence.MaxStepMilliseconds,
            span > 0 ? (stepped - 1) / span : 0,
            emitted > 0 ? repeated * 100.0 / emitted : 0,
            slots > 0 ? cadence.MissedSlots * 100.0 / slots : 0,
            pacer?.FreshSlots ?? 0,
            pacer?.SkippedSlots ?? 0,
            pacer?.HeldReplaced ?? 0);

        // Every device, with the hub's readouts and the samples encoded by the first Track that holds it.
        var audio = new List<AudioReport>();
        for (var i = 0; i < chain.AudioTracks.Count; i++)
        {
            var track = chain.AudioTracks[i];
            if (track.Subscription is AudioDeviceSubscription { Diagnostics: var diagnostics })
            {
                var role = track.Device.Flow == SessionFlow.Output ? "output" : "input";
                var first = chain.Spec.Audio.FirstTrackOf(i);
                var samples = first >= 0 ? chain.AudioEncoders[first].SamplesWritten : 0;
                audio.Add(CaptureStatsCollector.Report(role, track.Device.FriendlyName, diagnostics.Stamps, diagnostics.Stream, chain.AudioTimelines[i], samples));
            }
        }

        var report = new CaptureStatsReport(
            CaptureStatsReport.CurrentSchema,
            DateTimeOffset.Now,
            _path ?? "",
            _window?.Title ?? "",
            _window?.ProcessName,
            _device?.AdapterName ?? "",
            _options.RequestedSeconds,
            _stopReason,
            (double)(_lastFrameQpc - _anchorQpc) / _qpcFrequency,
            _stats.GpuPriority,
            new EncoderReport(
                _options.Stages.ToString().ToLowerInvariant(),
                _options.NvencPreset,
                _options.NvencTune,
                _options.Color == ColorPath.X2 ? "x2" : "x1",
                paced is not null),
            video,
            CaptureStatsCollector.Report(_stats.Latency, encoder.FlushedPackets),
            _stats.Resources(),
            audio);

        try
        {
            CaptureStatsCollector.Write(path, report);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Fail(new EngineFailure(ErrorCodes.StatsWriteFailed, $"Could not write the stats file '{path}': {e.Message}"));
            return;
        }

        _log.LogInformation("Stats written to {Path}: {Encoded} frames encoded ({Fresh} new), latency p99 {P99:0.0} ms",
            path, video.FramesEncoded, video.FreshSlots, report.Latency.P99Ms);
    }
}
