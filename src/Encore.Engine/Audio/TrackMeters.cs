using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Audio;
using Encore.Core.Sessions;
using Encore.Media;

namespace Encore.Engine.Audio;

/// <summary>
/// The Track meters (R6, AD-20): <c>meters.watch</c> builds a meter-only mixer for a Track Layout on
/// the hub's fan-out, with nothing captured. Each distinct device is subscribed once, cut by an
/// <see cref="AudioTimeline"/> and fanned out to its Tracks' <see cref="TrackMixer"/>s (mic mute
/// gating a Mic Device in a <c>muteAffected</c> Track, as in a capture), whose output is a
/// <see cref="PeakMeter"/> rather than an encoder. A meter thread publishes each Track's peak as
/// <c>meters.levels</c>, never twice within <see cref="PublishInterval"/>. One watch at a time: a new
/// one replaces it, and <see cref="Unwatch"/> closes its subscriptions and publishes no Tracks.
/// Thread-safe: every call is under one lock.
/// </summary>
internal sealed class TrackMeters : IDisposable
{
    /// <summary>50 ms: at most 20 publishes a second.</summary>
    public static readonly TimeSpan PublishInterval = TimeSpan.FromMilliseconds(50);

    private readonly Lock _lock = new();
    private readonly IAudioDeviceHub _hub;
    private readonly Action<MeterLevels> _publish;
    private readonly ILogger _log;
    private ActiveWatch? _watch;
    private long _generation;

    // Muted until told otherwise, as CaptureState.Initial is.
    private bool _micMuted = true;
    private bool _disposed;

    public TrackMeters(IAudioDeviceHub hub, Action<MeterLevels> publish, ILogger log)
    {
        _hub = hub;
        _publish = publish;
        _log = log;
    }

    /// <summary>
    /// Meters <paramref name="layout"/>, replacing any previous watch: its subscriptions are closed
    /// before the new ones open. A device that can't be opened is left out, with a warning logged.
    /// </summary>
    /// <exception cref="ErrorCodeException">
    /// <see cref="ErrorCodes.InvalidArguments"/>: a Track names a device key the layout lacks; the previous watch is kept.
    /// </exception>
    public void Watch(TrackLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        // Validated first, so a bad layout keeps the old watch.
        var audio = SessionAudio.FromLayout(layout);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // The new watch's first levels follow, so no empty levels in between.
            _generation++;
            _watch?.Stop();
            _watch = null;
            try
            {
                _watch = new ActiveWatch(_hub, layout, audio, _micMuted, _publish, _log);
            }
            catch
            {
                // The old watch is gone: its last levels mustn't look live.
                _publish(MeterLevels.Empty);
                throw;
            }
        }
    }

    /// <summary>Stops the watch, if any; a no-op when nothing is watched.</summary>
    public void Unwatch()
    {
        lock (_lock)
        {
            _watch?.Dispose();
            _watch = null;
        }
    }

    /// <summary>Counts <see cref="Watch"/> calls; read it to stop only the watches made before a moment.</summary>
    public long Generation
    {
        get
        {
            lock (_lock)
            {
                return _generation;
            }
        }
    }

    /// <summary>
    /// <see cref="Unwatch"/>, unless a watch arrived after <paramref name="generation"/> was read: a
    /// late unwatch for a departed client must not stop a newly connected one's watch.
    /// </summary>
    /// <returns>Whether it stopped the watch (or there was none).</returns>
    public bool UnwatchIf(long generation)
    {
        lock (_lock)
        {
            if (_generation != generation)
            {
                return false;
            }

            _watch?.Dispose();
            _watch = null;
            return true;
        }
    }

    /// <summary>The orchestrator's mic mute; a watch's gated Tracks follow it from now on.</summary>
    public void SetMicMuted(bool muted)
    {
        lock (_lock)
        {
            _micMuted = muted;
            _watch?.Schedule.Set(muted, Stopwatch.GetTimestamp());
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _watch?.Dispose();
            _watch = null;
        }
    }

    /// <summary>One watch: its device subscriptions, mixers, meters and publish thread.</summary>
    private sealed class ActiveWatch : IDisposable
    {
        private readonly List<IAudioSubscription> _subscriptions = [];
        private readonly PeakMeter[] _meters;
        private readonly Guid[] _trackIds;
        private readonly Action<MeterLevels> _publish;
        private readonly ManualResetEvent _stop = new(false);
        private readonly Thread _thread;

        public ActiveWatch(IAudioDeviceHub hub, TrackLayout layout, SessionAudio audio, bool micMuted, Action<MeterLevels> publish, ILogger log)
        {
            _publish = publish;
            var frequency = Stopwatch.Frequency;
            var anchorQpc = Stopwatch.GetTimestamp();
            Schedule = new MicMuteSchedule(micMuted);

            try
            {
                // Each device's tap drops blocks until its timeline is published below.
                var taps = new AudioTimelineTap?[audio.Devices.Count];
                for (var d = 0; d < audio.Devices.Count; d++)
                {
                    var device = audio.Devices[d];
                    var tap = new AudioTimelineTap();
                    try
                    {
                        _subscriptions.Add(hub.Subscribe(device, tap));
                        taps[d] = tap;
                    }
                    catch (ErrorCodeException e)
                    {
                        log.LogWarning("Meters leave out {Device}: {Message}", device.FriendlyName, e.Message);
                    }
                }

                // Each Track mixes the devices that opened; one with none stays at 0.
                var tracks = audio.Tracks;
                _meters = new PeakMeter[tracks.Count];
                _trackIds = new Guid[tracks.Count];
                var mixers = new TrackMixer[tracks.Count];
                var inputs = new List<int>[tracks.Count];
                for (var t = 0; t < tracks.Count; t++)
                {
                    _trackIds[t] = layout.Tracks[t].Id;
                    _meters[t] = new PeakMeter();
                    inputs[t] = [.. tracks[t].DeviceIndexes.Where(d => taps[d] is not null)];
                    mixers[t] = new TrackMixer(inputs[t].Count, _meters[t]);
                }

                for (var d = 0; d < taps.Length; d++)
                {
                    if (taps[d] is not { } tap)
                    {
                        continue;
                    }

                    var sink = TrackWiring.DeviceSink(d, audio.Devices[d].IsMic, tracks, inputs, mixers, Schedule, frequency, anchorQpc);
                    tap.Publish(new AudioTimeline(frequency, anchorQpc, sink));
                }
            }
            catch
            {
                DisposeSubscriptions();
                _stop.Dispose();
                throw;
            }

            _thread = new Thread(Run) { Name = "TrackMeters", IsBackground = true };
            _thread.Start();
        }

        public MicMuteSchedule Schedule { get; }

        /// <summary>Stops the publish thread, closes the subscriptions, then publishes no Tracks.</summary>
        public void Dispose()
        {
            Stop();
            _publish(MeterLevels.Empty);
        }

        /// <summary>Stops the publish thread and closes the subscriptions, publishing nothing.</summary>
        public void Stop()
        {
            _stop.Set();
            _thread.Join();
            DisposeSubscriptions();
            _stop.Dispose();
        }

        private void DisposeSubscriptions()
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }

        /// <summary>Publishes the peaks whenever at least <see cref="PublishInterval"/> has passed since the last publish, by Stopwatch.</summary>
        private void Run()
        {
            var interval = (long)(PublishInterval.TotalSeconds * Stopwatch.Frequency);
            var last = Stopwatch.GetTimestamp();
            while (true)
            {
                var remaining = last + interval - Stopwatch.GetTimestamp();
                if (remaining > 0)
                {
                    // Rounded up; a wait that ends early only loops.
                    var wait = TimeSpan.FromMilliseconds(Math.Ceiling(remaining * 1000.0 / Stopwatch.Frequency));
                    if (_stop.WaitOne(wait))
                    {
                        return;
                    }

                    continue;
                }

                if (_stop.WaitOne(0))
                {
                    return;
                }

                last = Stopwatch.GetTimestamp();
                var levels = new TrackLevel[_meters.Length];
                for (var t = 0; t < _meters.Length; t++)
                {
                    levels[t] = new TrackLevel(_trackIds[t], _meters[t].Take());
                }

                _publish(new MeterLevels(levels));
            }
        }
    }
}

/// <summary>
/// A Track's meter: the largest |sample| written since the last <see cref="Take"/>. Written on device
/// threads without allocating or blocking (a compare-and-swap on the peak's bits); silence is a no-op.
/// </summary>
internal sealed class PeakMeter : IAudioSampleSink
{
    private int _bits;

    public void Write(ReadOnlySpan<float> interleaved)
    {
        var peak = 0f;
        foreach (var sample in interleaved)
        {
            var magnitude = Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        if (peak <= 0)
        {
            return;
        }

        var seen = Volatile.Read(ref _bits);
        while (BitConverter.Int32BitsToSingle(seen) < peak)
        {
            var prior = Interlocked.CompareExchange(ref _bits, BitConverter.SingleToInt32Bits(peak), seen);
            if (prior == seen)
            {
                return;
            }

            seen = prior;
        }
    }

    public void WriteSilence(long frames)
    {
    }

    /// <summary>The peak since the last take, resetting it to 0.</summary>
    public float Take() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _bits, 0));
}
