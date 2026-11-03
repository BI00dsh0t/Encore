using System.Diagnostics;
using ReplayBuffer.Contracts;
using ReplayBuffer.Core.Audio;
using SettingsFlow = ReplayBuffer.Contracts.Settings.AudioFlow;

namespace ReplayBuffer.Engine.Tests;

/// <summary>
/// A hub whose every endpoint plays one sine, identical on both channels, in real time: 10 ms
/// blocks stamped contiguously from the subscription's start QPC, as the real hub's are. An
/// endpoint with amplitude 0 plays silence; one it has no tone for fails to open, as an absent device does.
/// </summary>
internal sealed class ToneHub(IReadOnlyDictionary<string, (double Amplitude, double Hz)> tones) : IAudioDeviceHub, IDisposable
{
    private readonly List<Tone> _running = [];
    private readonly Dictionary<string, int> _subscriptions = [];
    private int _active;

    /// <summary>Subscriptions not yet disposed, over every endpoint.</summary>
    public int ActiveSubscriptions
    {
        get
        {
            lock (_running)
            {
                return _active;
            }
        }
    }

    public int Subscriptions(string endpointId)
    {
        lock (_running)
        {
            return _subscriptions.GetValueOrDefault(endpointId);
        }
    }

    public IAudioSubscription Subscribe(string endpointId, SettingsFlow flow, IAudioPcmSubscriber subscriber)
    {
        if (!tones.TryGetValue(endpointId, out var played))
        {
            throw new ErrorCodeException(ErrorCodes.AudioDeviceFailed, $"No device '{endpointId}'.");
        }

        var (amplitude, hz) = played;
        var tone = new Tone(amplitude, hz, subscriber, OnDisposed);
        lock (_running)
        {
            _active++;
            _subscriptions[endpointId] = _subscriptions.GetValueOrDefault(endpointId) + 1;
            _running.Add(tone);
        }

        return tone;
    }

    public void Dispose()
    {
        lock (_running)
        {
            foreach (var tone in _running)
            {
                tone.Dispose();
            }
        }
    }

    private void OnDisposed()
    {
        lock (_running)
        {
            _active--;
        }
    }

    private sealed class Tone : IAudioSubscription
    {
        private const int BlockFrames = AudioFormat.SampleRate / 100;

        private readonly double _amplitude;
        private readonly double _hz;
        private readonly IAudioPcmSubscriber _subscriber;
        private readonly Action _disposed;
        private readonly Thread _thread;
        private volatile bool _stop;
        private int _disposedOnce;

        public Tone(double amplitude, double hz, IAudioPcmSubscriber subscriber, Action disposed)
        {
            _disposed = disposed;
            _amplitude = amplitude;
            _hz = hz;
            _subscriber = subscriber;
            _thread = new Thread(Run) { IsBackground = true, Name = "ToneHub" };
            _thread.Start();
        }

        public Exception? DeviceFault => null;

        public Exception? SubscriberFault { get; private set; }

        public void Dispose()
        {
            _stop = true;
            if (Thread.CurrentThread != _thread)
            {
                _thread.Join();
            }

            if (Interlocked.Exchange(ref _disposedOnce, 1) == 0)
            {
                _disposed();
            }
        }

        private void Run()
        {
            var origin = Stopwatch.GetTimestamp();
            var block = new float[BlockFrames * AudioFormat.Channels];
            long frame = 0;
            while (!_stop)
            {
                var startQpc = origin + (frame * Stopwatch.Frequency / AudioFormat.SampleRate);
                var due = origin + ((frame + BlockFrames) * Stopwatch.Frequency / AudioFormat.SampleRate);
                while (Stopwatch.GetTimestamp() < due && !_stop)
                {
                    Thread.Sleep(1);
                }

                for (var i = 0; i < BlockFrames; i++)
                {
                    var value = (float)(_amplitude * Math.Sin(2 * Math.PI * _hz * (frame + i) / AudioFormat.SampleRate));
                    block[2 * i] = value;
                    block[(2 * i) + 1] = value;
                }

                if (SubscriberFault is null)
                {
                    try
                    {
                        _subscriber.OnPcm(new AudioBlock(startQpc, BlockFrames, block));
                    }
                    catch (Exception e)
                    {
                        SubscriberFault = e;
                    }
                }

                frame += BlockFrames;
            }
        }
    }
}
