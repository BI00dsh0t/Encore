using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Encore.Contracts;
using Encore.Core.Audio;
using Encore.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;

namespace Encore.Platform.Audio;

/// <summary>
/// The hub's client for one endpoint (AD-13, AD-20): WASAPI shared mode, event-driven, loopback for
/// an Output Device and capture for an Input Device, converted by the audio engine to 48 kHz float
/// stereo. On a dedicated MMCSS "Pro Audio" thread, each packet's stamp goes to an
/// <see cref="AudioStampStats"/> and the packet to a <see cref="DeviceStream"/>. An idle render
/// endpoint, or a failed device, is filled with silence 50 ms behind now. A device invalidated under
/// the client (<c>AUDCLNT_E_DEVICE_INVALIDATED</c>) is held silent the same way, but is not a
/// <see cref="Fault"/>: the client calls its <c>invalidated</c> callback once, and the hub drops it.
/// </summary>
internal sealed unsafe class WasapiCapture : IDeviceClient
{
    /// <summary><c>AUDCLNT_E_DEVICE_INVALIDATED</c>: the endpoint was removed, disabled or reconfigured.</summary>
    internal const int DeviceInvalidated = unchecked((int)0x88890004);

    private const long BufferDuration100ns = 1_000_000; // 100 ms
    private const uint WaitTimeoutMs = 20;
    private const long IdleFillLagMs = 50;

    private readonly ILogger _logger;
    private readonly Action? _invalidated;
    private readonly long _qpcFrequency = Stopwatch.Frequency;
    private IAudioClient* _client;
    private IAudioCaptureClient* _capture;
    private HANDLE _event;
    private Thread? _thread;
    private DeviceStream? _stream;
    private AudioStampStats? _stampStats;
    private volatile bool _stopping;

    /// <summary>Opens and initializes the client; capture starts with <see cref="Start"/>.</summary>
    /// <exception cref="ErrorCodeException"><see cref="ErrorCodes.AudioDeviceFailed"/>: the endpoint can't be opened.</exception>
    public WasapiCapture(string endpointId, AudioFlow flow, ILogger logger, Action? invalidated = null)
    {
        _logger = logger;
        _invalidated = invalidated;
        Endpoint = new AudioEndpoint(endpointId, endpointId, flow);
        IMMDevice* device = null;
        try
        {
            AudioEndpoints.EnsureCom();
            device = AudioEndpoints.OpenDevice(endpointId);
            Endpoint = AudioEndpoints.Describe(device, flow);

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
            if (IsLoopback)
            {
                flags |= PInvoke.AUDCLNT_STREAMFLAGS_LOOPBACK;
            }

            _client->Initialize(AUDCLNT_SHAREMODE.AUDCLNT_SHAREMODE_SHARED, flags, BufferDuration100ns, 0, &format, null);

            _event = PInvoke.CreateEvent(null, false, false, default(PCWSTR));
            if (_event.IsNull)
            {
                throw new InvalidOperationException("CreateEvent failed.");
            }

            _client->SetEventHandle(_event);

            var captureIid = IAudioCaptureClient.IID_Guid;
            IAudioCaptureClient* capture = null;
            _client->GetService(&captureIid, (void**)&capture);
            _capture = capture;
        }
        catch (Exception e) when (e is not ErrorCodeException)
        {
            Dispose();
            throw new ErrorCodeException(ErrorCodes.AudioDeviceFailed, $"Could not open '{Endpoint.FriendlyName}': {e.Message}", e);
        }
        finally
        {
            if (device is not null) device->Release();
        }

        logger.LogInformation("Opened {Flow} device '{Name}' ({Mode})", flow, Endpoint.FriendlyName, IsLoopback ? "loopback" : "capture");
    }

    public AudioEndpoint Endpoint { get; }

    /// <summary>The first failure while capturing, if any; the stream was held silent after it.</summary>
    public Exception? Fault { get; private set; }

    /// <summary>Whether the caller is this client's capture thread.</summary>
    public bool OnCaptureThread => Thread.CurrentThread == _thread;

    private bool IsLoopback => Endpoint.Flow == AudioFlow.Output;

    /// <summary>Starts capture: every packet's stamp goes to <paramref name="stampStats"/>, then the packet to <paramref name="stream"/>.</summary>
    public void Start(DeviceStream stream, AudioStampStats stampStats)
    {
        _stream = stream;
        _stampStats = stampStats;
        _thread = new Thread(Run) { Name = $"WASAPI {Endpoint.Flow}", IsBackground = true };
        _thread.Start();
    }

    /// <summary>Stops the thread and the client. Every packet read so far is in the stream.</summary>
    public void Stop()
    {
        _stopping = true;
        _thread?.Join();
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        if (_capture is not null)
        {
            _capture->Release();
            _capture = null;
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
        var mmcss = PInvoke.AvSetMmThreadCharacteristics("Pro Audio", ref taskIndex);
        if (mmcss.IsInvalid)
        {
            _logger.LogWarning("MMCSS 'Pro Audio' was refused for '{Name}'", Endpoint.FriendlyName);
        }

        var stream = _stream!;
        var stampStats = _stampStats!;
        var failed = false;
        try
        {
            _client->Start();
        }
        catch (Exception e) when (IsInvalidated(e))
        {
            OnInvalidated(e);
            failed = true;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Could not start '{Name}'; holding it silent", Endpoint.FriendlyName);
            Fault = e;
            failed = true;
        }

        try
        {
            var dead = false;
            while (!_stopping)
            {
                var wait = PInvoke.WaitForSingleObject(_event, WaitTimeoutMs);
                if (dead)
                {
                    continue;
                }

                try
                {
                    if (!failed && wait == WAIT_EVENT.WAIT_OBJECT_0)
                    {
                        Drain(stream, stampStats);
                    }
                    else if (IsLoopback || failed)
                    {
                        // An idle render endpoint sends no packets: keep its stream continuous.
                        // A failed device is held silent the same way so subscribers stay aligned.
                        stream.FillTo(Stopwatch.GetTimestamp() - (IdleFillLagMs * _qpcFrequency / 1000));
                    }
                }
                catch (Exception e) when (!failed && IsInvalidated(e))
                {
                    OnInvalidated(e);
                    failed = true;
                }
                catch (Exception e) when (!failed)
                {
                    _logger.LogError(e, "Capture from '{Name}' failed; holding it silent", Endpoint.FriendlyName);
                    Fault = e;
                    failed = true;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Silence fill for '{Name}' failed; its stream stops here", Endpoint.FriendlyName);
                    dead = true;
                }
            }
        }
        finally
        {
            try
            {
                _client->Stop();
            }
            catch (Exception e)
            {
                _logger.LogDebug(e, "IAudioClient.Stop failed");
            }

            mmcss.Dispose();
        }
    }

    /// <summary>Whether <paramref name="e"/>, or an exception it wraps, carries <c>AUDCLNT_E_DEVICE_INVALIDATED</c>.</summary>
    internal static bool IsInvalidated(Exception e)
    {
        for (var current = e; current is not null; current = current.InnerException)
        {
            if (current.HResult == DeviceInvalidated)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The device went away under the client: held silent, not a fault; the hub is told once.</summary>
    private void OnInvalidated(Exception e)
    {
        _logger.LogDebug(e, "'{Name}' was invalidated; holding it silent", Endpoint.FriendlyName);
        try
        {
            _invalidated?.Invoke();
        }
        catch (Exception callback)
        {
            _logger.LogError(callback, "The invalidated callback for '{Name}' failed", Endpoint.FriendlyName);
        }
    }

    private void Drain(DeviceStream stream, AudioStampStats stampStats)
    {
        while (true)
        {
            uint next;
            _capture->GetNextPacketSize(&next);
            if (next == 0)
            {
                return;
            }

            byte* data;
            uint frames;
            uint flags;
            ulong qpcPosition;
            _capture->GetBuffer(&data, &frames, &flags, null, &qpcPosition);
            var readQpc = Stopwatch.GetTimestamp();
            try
            {
                var packetFlags = (AudioPacketFlags)(flags & 0x7);
                var stamp = QpcClock.From100ns((long)qpcPosition, _qpcFrequency);
                stampStats.Record(stamp, (int)frames, packetFlags, readQpc);
                var samples = (packetFlags & AudioPacketFlags.Silent) != 0
                    ? ReadOnlySpan<float>.Empty
                    : new ReadOnlySpan<float>(data, (int)frames * AudioFormat.Channels);
                stream.Push(stamp, (int)frames, samples, packetFlags);
            }
            finally
            {
                _capture->ReleaseBuffer(frames);
            }
        }
    }
}
