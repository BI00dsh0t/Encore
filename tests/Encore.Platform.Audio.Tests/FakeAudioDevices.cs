using System.Diagnostics;
using Encore.Contracts;
using Encore.Contracts.Messages;
using Encore.Core.Audio;
using Encore.Media;
using SettingsFlow = Encore.Contracts.Settings.AudioFlow;

namespace Encore.Platform.Audio.Tests;

/// <summary>A fake list of the PC's endpoints that the test edits; each change raises the changed event at once.</summary>
internal sealed class FakeEndpointList : IAudioEndpointSource
{
    private readonly Lock _lock = new();
    private readonly List<AudioDevice> _present = [];
    private volatile bool _failing;

    public event Action? EndpointsChanged;

    /// <summary>While set, <see cref="ListPresent"/> throws.</summary>
    public bool Failing
    {
        get => _failing;
        set => _failing = value;
    }

    public IReadOnlyList<AudioDevice> ListPresent()
    {
        if (_failing)
        {
            throw new InvalidOperationException("test listing failure");
        }

        lock (_lock)
        {
            return [.. _present];
        }
    }

    public void Add(string id, string name, string parent, SettingsFlow flow, bool raise = true)
    {
        lock (_lock)
        {
            _present.Add(new AudioDevice(id, name, parent, flow, Available: true));
        }

        if (raise)
        {
            Raise();
        }
    }

    public void Remove(string id, bool raise = true)
    {
        lock (_lock)
        {
            _present.RemoveAll(d => d.EndpointId == id);
        }

        if (raise)
        {
            Raise();
        }
    }

    public void Raise() => EndpointsChanged?.Invoke();
}

/// <summary>The fake clients' factory: each endpoint plays one sine in real time; the test can refuse, mute or invalidate one.</summary>
internal sealed class FakeDeviceBank
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, (AudioEndpoint Endpoint, double Amplitude, double Hz)> _defined = [];
    private readonly HashSet<string> _closed = [];
    private readonly Dictionary<string, int> _attempts = [];
    private readonly Dictionary<string, List<ToneClient>> _opened = [];
    private readonly HashSet<string> _invalidateOnStart = [];

    /// <summary>The next client opened on this id reports itself invalidated just after it starts, as a restarting device would.</summary>
    public void InvalidateNextOnStart(string id)
    {
        lock (_lock)
        {
            _invalidateOnStart.Add(id);
        }
    }

    public void Define(string id, string name, string parent, AudioFlow flow, double amplitude, double hz)
    {
        lock (_lock)
        {
            _defined[id] = (new AudioEndpoint(id, name, flow, parent), amplitude, hz);
        }
    }

    /// <summary>Whether a new client on <paramref name="id"/> opens; a closed endpoint throws <c>audio-device-failed</c>.</summary>
    public void SetOpenable(string id, bool openable)
    {
        lock (_lock)
        {
            if (openable)
            {
                _closed.Remove(id);
            }
            else
            {
                _closed.Add(id);
            }
        }
    }

    /// <summary>Opens tried on <paramref name="id"/>, failed ones included.</summary>
    public int Attempts(string id)
    {
        lock (_lock)
        {
            return _attempts.GetValueOrDefault(id);
        }
    }

    /// <summary>Clients opened on <paramref name="id"/>.</summary>
    public int Opens(string id)
    {
        lock (_lock)
        {
            return _opened.TryGetValue(id, out var clients) ? clients.Count : 0;
        }
    }

    /// <summary>The latest client opened on <paramref name="id"/>.</summary>
    public ToneClient Latest(string id)
    {
        lock (_lock)
        {
            return _opened[id][^1];
        }
    }

    /// <summary>The device vanishes: its client stops delivering and no new client opens.</summary>
    public void Unplug(string id)
    {
        SetOpenable(id, false);
        lock (_lock)
        {
            if (_opened.TryGetValue(id, out var clients))
            {
                clients[^1].Muted = true;
            }
        }
    }

    public IDeviceClient Create(string endpointId, AudioFlow flow, Action invalidated)
    {
        lock (_lock)
        {
            _attempts[endpointId] = _attempts.GetValueOrDefault(endpointId) + 1;
            if (!_defined.TryGetValue(endpointId, out var definition) || _closed.Contains(endpointId) || definition.Endpoint.Flow != flow)
            {
                throw new ErrorCodeException(ErrorCodes.AudioDeviceFailed, $"Could not open '{endpointId}'.");
            }

            var client = new ToneClient(definition.Endpoint, definition.Amplitude, definition.Hz, invalidated)
            {
                InvalidateOnStart = _invalidateOnStart.Remove(endpointId),
            };
            if (!_opened.TryGetValue(endpointId, out var clients))
            {
                _opened[endpointId] = clients = [];
            }

            clients.Add(client);
            return client;
        }
    }
}

/// <summary>A fake client: a real-time sine, stamped from QPC like a WASAPI client's packets.</summary>
internal sealed class ToneClient(AudioEndpoint endpoint, double amplitude, double hz, Action invalidated) : IDeviceClient
{
    private const int BlockFrames = AudioFormat.SampleRate / 100;

    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _muted;

    public AudioEndpoint Endpoint { get; } = endpoint;

    /// <summary>The fault the client reports; a test sets it.</summary>
    public Exception? Fault { get; set; }

    /// <summary>Whether the client reports itself invalidated from its thread right after it starts.</summary>
    public bool InvalidateOnStart { get; init; }

    public bool OnCaptureThread => Thread.CurrentThread == _thread;

    public bool Disposed { get; private set; }

    /// <summary>While set, the client keeps time but delivers nothing.</summary>
    public bool Muted
    {
        get => _muted;
        set => _muted = value;
    }

    /// <summary>Reports the device invalidated, as WASAPI's client would from its thread.</summary>
    public void Invalidate() => invalidated();

    public void Start(DeviceStream stream, AudioStampStats stamps)
    {
        _thread = new Thread(() => Run(stream)) { IsBackground = true, Name = "Fake tone client" };
        _thread.Start();
    }

    public void Dispose()
    {
        _stop = true;
        if (_thread is not null && !OnCaptureThread)
        {
            _thread.Join();
        }

        Disposed = true;
    }

    private void Run(DeviceStream stream)
    {
        if (InvalidateOnStart)
        {
            invalidated();
        }

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

            if (_stop)
            {
                return;
            }

            if (!_muted)
            {
                for (var i = 0; i < BlockFrames; i++)
                {
                    var value = (float)(amplitude * Math.Sin(2 * Math.PI * hz * (frame + i) / AudioFormat.SampleRate));
                    block[2 * i] = value;
                    block[(2 * i) + 1] = value;
                }

                stream.Push(startQpc, BlockFrames, block, AudioPacketFlags.None);
            }

            frame += BlockFrames;
        }
    }
}
