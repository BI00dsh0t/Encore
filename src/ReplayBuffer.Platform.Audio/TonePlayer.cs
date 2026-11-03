using System.Diagnostics;
using ReplayBuffer.Contracts;
using ReplayBuffer.Core.Audio;
using ReplayBuffer.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace ReplayBuffer.Platform.Audio;

/// <summary>
/// A test signal for sync measurements (stories 1.12 and 1.14): WASAPI shared-mode render on one
/// endpoint of a quiet 440 Hz tone, replaced once a second by a 3 kHz burst. Each burst's QPC — when
/// its first frame plays, from <c>IAudioClock::GetPosition</c> — is recorded, allocation-free.
/// The first burst is at stream frame <see cref="BurstIntervalFrames"/>, then one every interval.
/// </summary>
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

    /// <param name="endpointId">An Output Device.</param>
    /// <param name="dbfs">The bursts' peak level; the tone plays 10 dB below it.</param>
    /// <param name="capacity">The most burst stamps kept.</param>
    /// <exception cref="ErrorCodeException"><see cref="ErrorCodes.AudioDeviceFailed"/>: the endpoint can't be opened.</exception>
    public TonePlayer(string endpointId, double dbfs = -30, int capacity = 8192)
    {
        _burstAmplitude = (float)Math.Pow(10, dbfs / 20);
        _toneAmplitude = (float)Math.Pow(10, (dbfs - 10) / 20);
        _burstQpcs = new long[capacity];
        IMMDevice* device = null;
        try
        {
            AudioEndpoints.EnsureCom();
            device = AudioEndpoints.OpenDevice(endpointId);
            Endpoint = AudioEndpoints.Describe(device, AudioFlow.Output);

            var clientIid = IAudioClient.IID_Guid;
            IAudioClient* client = null;
            device->Activate(&clientIid, CLSCTX.CLSCTX_ALL, null, (void**)&client);
            _client = client;

            var format = new WAVEFORMATEX
            {
                wFormatTag = (ushort)PInvoke.WAVE_FORMAT_IEEE_FLOAT,
                nChannels = AudioFormat.Channels,
                nSamplesPerSec = AudioFormat.SampleRate,
                wBitsPerSample = 32,
                nBlockAlign = AudioFormat.Channels * sizeof(float),
                nAvgBytesPerSec = AudioFormat.SampleRate * AudioFormat.Channels * sizeof(float),
                cbSize = 0,
            };
            var flags = PInvoke.AUDCLNT_STREAMFLAGS_EVENTCALLBACK
                | PInvoke.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                | PInvoke.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
            _client->Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, flags, BufferDuration100ns, 0, &format, null);

            _event = PInvoke.CreateEvent(null, false, false, default(PCWSTR));
            if (_event.IsNull)
            {
                throw new InvalidOperationException("CreateEvent failed.");
            }

            _client->SetEventHandle(_event);
            uint bufferFrames;
            _client->GetBufferSize(&bufferFrames);
            _bufferFrames = bufferFrames;

            var renderIid = IAudioRenderClient.IID_Guid;
            IAudioRenderClient* render = null;
            _client->GetService(&renderIid, (void**)&render);
            _render = render;

            var clockIid = IAudioClock.IID_Guid;
            IAudioClock* clock = null;
            _client->GetService(&clockIid, (void**)&clock);
            _clock = clock;
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

        if (_clock is not null)
        {
            _clock->Release();
            _clock = null;
        }

        if (_render is not null)
        {
            _render->Release();
            _render = null;
        }

        if (_client is not null)
        {
            _client->Release();
            _client = null;
        }

        if (!_event.IsNull)
        {
            PInvoke.CloseHandle(_event);
            _event = default;
        }
    }

    private void Run()
    {
        AudioEndpoints.EnsureCom();
        uint taskIndex = 0;
        using var mmcss = PInvoke.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
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
            var n = _written + i;
            var inBurst = n >= BurstIntervalFrames && n % BurstIntervalFrames < BurstFrames;
            var value = inBurst
                ? _burstAmplitude * Math.Sin(2 * Math.PI * BurstHz * n / AudioFormat.SampleRate)
                : _toneAmplitude * Math.Sin(2 * Math.PI * ToneHz * n / AudioFormat.SampleRate);
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
