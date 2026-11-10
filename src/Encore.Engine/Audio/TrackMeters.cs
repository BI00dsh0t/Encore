using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Contracts.Settings;
using Encore.Core.Audio;
using Encore.Core.Sessions;
using Encore.Media;

namespace Encore.Engine.Audio;

/// <summary>The Track meters: <c>meters.watch</c> builds a meter-only mixer for a layout; nothing is captured. Thread-safe.</summary>
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

    /// <summary>Meters a layout, replacing any previous watch; a device that can't be opened is left out with a warning.</summary>
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

    /// <summary>Unwatches unless a newer watch arrived since generation was read, so a late unwatch can't stop a new one.</summary>
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
            var anchor = Stopwatch.GetTimestamp();
            Schedule = new MicMuteSchedule(micMuted);
            try
            {
                var taps = SubscribeTaps(hub, audio, log);
                _meters = new PeakMeter[audio.Tracks.Count];
                _trackIds = new Guid[audio.Tracks.Count];
                for (var track = 0; track < audio.Tracks.Count; track++)
                {
                    _trackIds[track] = layout.Tracks[track].Id;
                    _meters[track] = new PeakMeter();
                }

                WireTracks(audio, taps, anchor);
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

        /// <summary>One tap per device that opened; each drops blocks until its timeline is published.</summary>
        private AudioTimelineTap?[] SubscribeTaps(IAudioDeviceHub hub, SessionAudio audio, ILogger log)
        {
            var taps = new AudioTimelineTap?[audio.Devices.Count];
            for (var index = 0; index < audio.Devices.Count; index++)
            {
                var device = audio.Devices[index];
                var tap = new AudioTimelineTap();
                try
                {
                    _subscriptions.Add(hub.Subscribe(device, tap));
                    taps[index] = tap;
                }
                catch (ErrorCodeException e)
                {
                    log.LogWarning("Meters leave out {Device}: {Message}", device.FriendlyName, e.Message);
                }
            }

            return taps;
        }

        /// <summary>Each Track mixes the devices that opened into its meter; one with none stays at 0.</summary>
        private void WireTracks(SessionAudio audio, AudioTimelineTap?[] taps, long anchor)
        {
            var frequency = Stopwatch.Frequency;
            var tracks = audio.Tracks;
            var mixers = new TrackMixer[tracks.Count];
            var inputs = new List<int>[tracks.Count];
            for (var track = 0; track < tracks.Count; track++)
            {
                inputs[track] = [.. tracks[track].DeviceIndexes.Where(device => taps[device] is not null)];
                mixers[track] = new TrackMixer(inputs[track].Count, _meters[track]);
            }

            for (var device = 0; device < taps.Length; device++)
            {
                if (taps[device] is not { } tap)
                {
                    continue;
                }

                var sink = TrackWiring.DeviceSink(device, audio.Devices[device].IsMic, tracks, inputs, mixers, Schedule, frequency, anchor);
                tap.Publish(new AudioTimeline(frequency, anchor, sink));
            }
        }

        private void DisposeSubscriptions()
        {
            foreach (var subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }

        /// <summary>Publishes the peaks once at least <see cref="PublishInterval"/> has passed since the last publish.</summary>
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
