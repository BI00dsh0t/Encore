using System.Diagnostics;
using Encore.Contracts;
using Encore.Core.Audio;
using Encore.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;

namespace Encore.Platform.Audio;

/// <summary>A test signal for sync measurements: a quiet 440 Hz tone with a 3 kHz burst each second, each burst timed by QPC.</summary>
public sealed unsafe class TonePlayer : IDisposable
{
    public const int ToneHz = 440;
    public const int BurstHz = 3000;
    public const int BurstIntervalFrames = AudioFormat.SampleRate;
    public const int BurstFrames = AudioFormat.SampleRate / 20; // 50 ms

    private const long BufferDuration100ns = 500_000; // 50 ms
    private const uint WaitTimeoutMs = 100;

    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private readonly float _burstAmplitude;
    private readonly float _toneAmplitude;
    private readonly long[] _burstQpcs;
    private IAudioClient* _client;
    private IAudioRenderClient* _render;
    private IAudioClock* _clock;
    private HANDLE _event;
    private uint _bufferFrames;
    private ulong _clockFrequency;
    private long _written;
    private int _bursts;
    private Thread? _thread;
    private volatile bool _stopping;

    /// <summary>burstDbfs is the bursts' peak level in dBFS; the tone plays 10 dB below it. Capacity is the most stamps kept.</summary>
    public TonePlayer(string endpointId, double burstDbfs = -30, int capacity = 8192)
    {
        _burstAmplitude = (float)Math.Pow(10, burstDbfs / 20);
        _toneAmplitude = (float)Math.Pow(10, (burstDbfs - 10) / 20);
        _burstQpcs = new long[capacity];
        IMMDevice* device = null;
        try
        {
            AudioEndpoints.EnsureCom();
            device = AudioEndpoints.OpenDevice(endpointId);
            Endpoint = AudioEndpoints.Describe(device, AudioFlow.Output);
            _client = SharedAudioClient.Activate(device);
            SharedAudioClient.InitializeShared(
                _client, SharedAudioClient.EventDrivenFlags, BufferDuration100ns, AudioFormat.Channels, AudioFormat.SampleRate);
            SharedAudioClient.AttachEvent(_client, ref _event);
            uint bufferFrames;
            _client->GetBufferSize(&bufferFrames);
            _bufferFrames = bufferFrames;
            _render = (IAudioRenderClient*)SharedAudioClient.GetService(_client, IAudioRenderClient.IID_Guid);
            _clock = (IAudioClock*)SharedAudioClient.GetService(_client, IAudioClock.IID_Guid);
            ulong frequency;
            _clock->GetFrequency(&frequency);
            _clockFrequency = frequency;
        }
        catch (Exception e) when (e is not ErrorCodeException)
        {
            Dispose();
            throw new ErrorCodeException(ErrorCodes.AudioDeviceFailed, $"Could not open '{endpointId}' for playback: {e.Message}", e);
        }
        finally
        {
            if (device is not null) device->Release();
        }
    }

    public AudioEndpoint Endpoint { get; } = new("", "", AudioFlow.Output);

    /// <summary>The first failure while playing, if any; playback stopped there.</summary>
    public Exception? Fault { get; private set; }

    /// <summary>Bursts whose start has played and been stamped.</summary>
    public int BurstCount => Volatile.Read(ref _bursts);

    /// <summary>The QPC at which burst <paramref name="index"/>'s first frame played.</summary>
    public long BurstQpc(int index)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, BurstCount);
        return _burstQpcs[index];
    }

    /// <summary>Pre-fills the buffer and starts playback on its own thread.</summary>
    public void Start()
    {
        Fill(_bufferFrames);
        _client->Start();
        _thread = new Thread(Run) { Name = "WASAPI tone", IsBackground = true };
        _thread.Start();
    }

    public void Stop()
    {
        _stopping = true;
        _thread?.Join();
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        if (_client is not null)
        {
            try
            {
                _client->Stop();
            }
            catch (Exception)
            {
                // Never started, or already gone.
            }
        }

        SharedAudioClient.Release(ref _clock);
        SharedAudioClient.Release(ref _render);
        SharedAudioClient.Release(ref _client);
        SharedAudioClient.CloseEvent(ref _event);
    }

    private void Run()
    {
        AudioEndpoints.EnsureCom();
        using var proAudio = SharedAudioClient.JoinProAudio();
        try
        {
            while (!_stopping)
            {
                PInvoke.WaitForSingleObject(_event, WaitTimeoutMs);
                uint padding;
                _client->GetCurrentPadding(&padding);
                Fill(_bufferFrames - padding);
                StampBursts();
            }
        }
        catch (Exception e)
        {
            Fault = e;
        }
    }

    private void Fill(uint frames)
    {
        if (frames == 0)
        {
            return;
        }

        byte* data;
        _render->GetBuffer(frames, &data);
        var samples = new Span<float>(data, (int)frames * AudioFormat.Channels);
        for (var i = 0; i < frames; i++)
        {
            var frame = _written + i;
            var inBurst = frame >= BurstIntervalFrames && frame % BurstIntervalFrames < BurstFrames;
            var value = inBurst
                ? _burstAmplitude * Math.Sin(2 * Math.PI * BurstHz * frame / AudioFormat.SampleRate)
                : _toneAmplitude * Math.Sin(2 * Math.PI * ToneHz * frame / AudioFormat.SampleRate);
            samples[2 * i] = samples[(2 * i) + 1] = (float)value;
        }

        _render->ReleaseBuffer(frames, 0);
        _written += frames;
    }

    /// <summary>Stamps each burst once the clock has passed its first frame, extrapolating back from the clock's own QPC.</summary>
    private void StampBursts()
    {
        ulong position, qpcPosition;
        _clock->GetPosition(&position, &qpcPosition);
        var playedFrames = (double)position * AudioFormat.SampleRate / _clockFrequency;
        var qpc = QpcClock.From100ns((long)qpcPosition, _qpcFrequency);
        while (_bursts < _burstQpcs.Length)
        {
            var burstFrame = (long)(_bursts + 1) * BurstIntervalFrames;
            if (playedFrames < burstFrame)
            {
                return;
            }

            _burstQpcs[_bursts] = qpc - (long)((playedFrames - burstFrame) * _qpcFrequency / AudioFormat.SampleRate);
            Volatile.Write(ref _bursts, _bursts + 1);
        }
    }
}
